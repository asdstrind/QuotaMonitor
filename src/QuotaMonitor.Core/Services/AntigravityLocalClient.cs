using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace QuotaMonitor.Core.Services;

public sealed class AntigravityReadException(string message, bool authenticationFailed = false) : Exception(message)
{
    public bool AuthenticationFailed { get; } = authenticationFailed;
}

public static class AntigravityLocalClient
{
    // Fixed script, no interpolated shell arguments. CSRF stays in memory; no account/token stores are read.
    // Same-user ownership, installation path, PID-owned loopback listeners, no proxies/redirects.
    private const string Discovery = """
        $ErrorActionPreference='Stop'
        $sid=[System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $result=@(Get-CimInstance Win32_Process -Filter "Name LIKE 'language_server%'" | ForEach-Object {
          $s=$_
          if($s.ExecutablePath -notmatch '\\antigravity\\.*language_server[^\\]*\.exe$'){return}
          $owner=Invoke-CimMethod -InputObject $s -MethodName GetOwnerSid
          if($owner.ReturnValue -ne 0 -or $owner.Sid -ne $sid){return}
          $token=[regex]::Match($s.CommandLine,'--csrf_token(?:=|\s+)["'']?([^\s"'']+)').Groups[1].Value
          if(!$token){return}
          $ports=@(Get-NetTCPConnection -State Listen -OwningProcess $s.ProcessId -ErrorAction SilentlyContinue | Where-Object {$_.LocalAddress -eq '127.0.0.1'} | Select-Object -ExpandProperty LocalPort -Unique)
          if($ports.Count -gt 0){[pscustomobject]@{Token=$token;Ports=$ports}}
        } | Select-Object -First 4)
        ConvertTo-Json -InputObject $result -Compress -Depth 4
        """;
    public static async Task<string> ReadAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) throw new AntigravityReadException("本地 Language Server 读取仅支持 Windows。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Discovery)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new AntigravityReadException("无法发现本地 Language Server。");
        string discovery;
        try
        {
            var output = ReadBounded(process.StandardOutput, 65536, token);
            var errors = ReadBounded(process.StandardError, 8192, token);
            await process.WaitForExitAsync(token);
            discovery = await output;
            await errors; // Never expose stderr; command lines may contain local secrets.
            if (process.ExitCode != 0) throw new AntigravityReadException("本地 Language Server 发现失败。");
        }
        finally { if (!process.HasExited) process.Kill(true); }
        using var endpoints = JsonDocument.Parse(discovery);
        if (endpoints.RootElement.ValueKind != JsonValueKind.Array || endpoints.RootElement.GetArrayLength() == 0)
            throw new AntigravityReadException("Antigravity 未连接；请打开已登录的官方客户端。");
        // Multiple sessions cannot safely be assigned to the same account without identity access.
        if (endpoints.RootElement.GetArrayLength() != 1) throw new AntigravityReadException("发现多个 Antigravity 会话，无法确认额度归属。");
        var endpoint = endpoints.RootElement[0];
        var csrf = endpoint.GetProperty("Token").GetString();
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri is { Scheme: "https", Host: "127.0.0.1" };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        foreach (var portField in endpoint.GetProperty("Ports").EnumerateArray().Take(8))
        {
            if (!portField.TryGetInt32(out var port) || port is < 1 or > 65535) continue;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary");
                request.Headers.Add("X-Codeium-Csrf-Token", csrf);
                request.Headers.Add("Connect-Protocol-Version", "1");
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new AntigravityReadException("Antigravity 会话未认证；请在官方客户端检查登录状态。", true);
                if (!response.IsSuccessStatusCode) continue;
                if (response.Content.Headers.ContentLength > 1_048_576) throw new AntigravityReadException("本地额度响应超过安全大小限制。");
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(stream);
                return await ReadBounded(reader, 1_048_576, token);
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        throw new AntigravityReadException("Antigravity 本地额度接口不可用。");
    }
    private static async Task<string> ReadBounded(StreamReader reader, int limit, CancellationToken ct)
    {
        var output = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (output.Length + count > limit) throw new AntigravityReadException("本地响应超过大小限制。");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
}
