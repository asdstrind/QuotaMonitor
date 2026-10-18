param([string]$OutputDirectory='publish\v1.6.4-win-x64',[switch]$SkipOnlineAudit)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$version=([xml](Get-Content -LiteralPath (Join-Path $root 'src\QuotaMonitor\QuotaMonitor.csproj') -Raw)).Project.PropertyGroup.Version
$sdk=Join-Path $root '.dotnet\dotnet.exe'
if(!(Test-Path -LiteralPath $sdk)){$sdk='dotnet'}
$output=[IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if(Test-Path -LiteralPath $output){throw 'Choose a new empty output directory; existing releases are preserved.'}
$map="$root=/_/QuotaMonitor"
$auditArgs=@()
if($SkipOnlineAudit){$auditArgs=@('-p:NuGetAudit=false')}
& $sdk publish (Join-Path $root 'src\QuotaMonitor\QuotaMonitor.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -p:IncludeSourceRevisionInInformationalVersion=false "-p:PathMap=$map" -o $output --nologo -m:1 /nr:false @auditArgs
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
if(Get-ChildItem -LiteralPath $output -Recurse -Filter *.pdb){throw 'Unexpected PDB in release.'}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE'),(Join-Path $root 'THIRD_PARTY_NOTICES.md'),(Join-Path $root 'TRADEMARKS.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $output -Recurse
New-Item -ItemType Directory -Path (Join-Path $output 'docs') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'docs\ANTIGRAVITY-HISTORY.md') -Destination (Join-Path $output 'docs')
Copy-Item -LiteralPath (Join-Path $root 'docs\CONFIGURATION.md') -Destination (Join-Path $output 'docs')
New-Item -ItemType Directory -Path (Join-Path $output 'bridge') | Out-Null
$bridgeOutput=Join-Path $output 'bridge\dsh-balance-bridge'
New-Item -ItemType Directory -Path $bridgeOutput | Out-Null
foreach($name in @('package.json','README.md','LICENSE','TRADEMARKS.md','cordis.patch.yml','lib')){
    Copy-Item -LiteralPath (Join-Path $root "bridge\dsh-balance-bridge\$name") -Destination $bridgeOutput -Recurse
}
Copy-Item -LiteralPath (Join-Path $root "docs\RELEASE-v$version.md") -Destination (Join-Path $output 'README.md')
$archive=Join-Path (Split-Path -Parent $output) "QuotaMonitor-v$version-win-x64.zip"
if(Test-Path -LiteralPath $archive){throw 'Archive already exists; choose a different parent output directory.'}
Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -CompressionLevel Optimal
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  QuotaMonitor-v$version-win-x64.zip" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
Write-Output $archive
