param([switch]$Publish,[switch]$Test,[string]$PublishDirectory='publish\v1.6.4-win-x64')
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$sdk=Join-Path $PSScriptRoot '.dotnet\dotnet.exe'
if(!(Test-Path -LiteralPath $sdk)){$sdk='dotnet'}
& $sdk build (Join-Path $PSScriptRoot 'QuotaEstimation.sln') -c Release --nologo -m:1 /nr:false
if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}
if($Test){& $sdk test (Join-Path $PSScriptRoot 'tests\QuotaMonitor.Tests\QuotaMonitor.Tests.csproj') -c Release --no-build --logger 'trx;LogFileName=v1.6.4-tests.trx' --results-directory (Join-Path $PSScriptRoot 'evidence\ui-v1.6.4\tests');if($LASTEXITCODE -ne 0){exit $LASTEXITCODE}}
if($Publish){$releasePath=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot $PublishDirectory));if(Test-Path -LiteralPath (Join-Path $releasePath 'QuotaMonitor.exe')){throw '目标目录已包含 Release。请选择新的 -PublishDirectory，保留旧版。'};& $sdk publish (Join-Path $PSScriptRoot 'src\QuotaMonitor\QuotaMonitor.csproj') -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o $releasePath --nologo -m:1 /nr:false;exit $LASTEXITCODE}


