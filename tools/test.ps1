# Unit and integration tests (fictitious statements only), then the end-to-end check of the real app.
# The app runs off-screen during the end-to-end check: no window appears.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet test (Join-Path $root 'tests\Finance.Tests\Finance.Tests.csproj')
if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
$work = Join-Path ([IO.Path]::GetTempPath()) 'productivityapp-e2e'
$samples = Join-Path $work 'samples'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
dotnet run --project (Join-Path $root 'tools\SampleStatements') -- generate $samples | Out-Null
dotnet build (Join-Path $root 'src\Finance.App\Finance.App.csproj') -c Debug | Out-Null
$env:DOTNET_DISABLE_GUI_ERRORS = '1'
$env:E2E_DOTNET_ROOT = Split-Path -Parent (Get-Command dotnet).Source
node (Join-Path $root 'tools\e2e\run.mjs') (Join-Path $root 'src\Finance.App\bin\Debug\net10.0-windows10.0.19041.0\ProductivityApp.exe') $samples (Join-Path $work 'run')
if ($LASTEXITCODE -ne 0) { throw 'end-to-end check failed' }
Write-Host "Screenshots: $(Join-Path $work 'run')"
