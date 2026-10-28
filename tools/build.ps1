# Builds the interface, then publishes the single self-contained ProductivityApp.exe into .\publish
# Needs the .NET 10 SDK and Node.js 22 or later.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location (Join-Path $root 'ui')
try {
    npm ci --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'interface build failed' }
} finally { Pop-Location }
dotnet publish (Join-Path $root 'src\Finance.App\Finance.App.csproj') -c Release -o (Join-Path $root 'publish')
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Get-Item (Join-Path $root 'publish\ProductivityApp.exe') | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
