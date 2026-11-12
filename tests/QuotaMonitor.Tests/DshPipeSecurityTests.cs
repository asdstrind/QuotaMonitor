using System.IO.Pipes;
using System.Runtime.InteropServices;
using QuotaMonitor.Core.Services;

namespace QuotaMonitor.Tests;
public class DshPipeSecurityTests
{
    [DllImport("advapi32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ImpersonateAnonymousToken(IntPtr thread);
    [DllImport("advapi32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)] private static extern bool RevertToSelf();
    [Fact] public async Task AnonymousWindowsIdentityCannotConnectToCurrentUserPipe()
    {
        var name="QuotaMonitor.SecurityTest."+Guid.NewGuid();
        await using var receiver=new DshBalanceReceiver(name);
        var pending=receiver.ReadAsync(CancellationToken.None);
        Assert.True(ImpersonateAnonymousToken(new IntPtr(-2)));
        try {
            using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut);
            Assert.Throws<UnauthorizedAccessException>(()=>client.Connect(500));
        } finally { Assert.True(RevertToSelf()); }
        Assert.Null((await pending).Balance);
    }
}
