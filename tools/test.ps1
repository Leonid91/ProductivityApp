# Every test, one project per kind (fictitious statements only):
#   tests\Finance.UnitTests         the engine's code alone, no file, database or network
#   tests\Finance.IntegrationTests  the engine with a real database, files, PDFs and a stand-in for the Internet
#   ui\tests                        the page's own code
#   tests\Finance.EndToEndTests     the real app, driven through its page (it runs off-screen: no window appears)
# The timings (tests\Finance.PerformanceTests) are run on their own, on a quiet machine.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet test --project (Join-Path $root 'tests\Finance.UnitTests\Finance.UnitTests.csproj')
if ($LASTEXITCODE -ne 0) { throw 'unit tests failed' }
dotnet test --project (Join-Path $root 'tests\Finance.IntegrationTests\Finance.IntegrationTests.csproj')
if ($LASTEXITCODE -ne 0) { throw 'integration tests failed' }
Push-Location (Join-Path $root 'ui')
try { npm test; if ($LASTEXITCODE -ne 0) { throw 'page tests failed' } } finally { Pop-Location }
$work = Join-Path ([IO.Path]::GetTempPath()) 'productivityapp-e2e'
$samples = Join-Path $work 'samples'
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
dotnet run --project (Join-Path $root 'tools\SampleStatements') -- generate $samples | Out-Null
dotnet build (Join-Path $root 'src\Finance.App\Finance.App.csproj') -c Debug | Out-Null
$env:DOTNET_DISABLE_GUI_ERRORS = '1'
$env:E2E_DOTNET_ROOT = Split-Path -Parent (Get-Command dotnet).Source
node (Join-Path $root 'tests\Finance.EndToEndTests\run.mjs') (Join-Path $root 'src\Finance.App\bin\Debug\net10.0-windows10.0.19041.0\ProductivityApp.exe') $samples (Join-Path $work 'run')
if ($LASTEXITCODE -ne 0) { throw 'end-to-end check failed' }
Write-Host "Screenshots: $(Join-Path $work 'run')"
