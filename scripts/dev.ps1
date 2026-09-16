param([switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
$sumiRoot = Split-Path $PSScriptRoot -Parent
dotnet build (Join-Path $sumiRoot 'Sumi.slnx')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not $BuildOnly) {
    dotnet run --no-build --project (Join-Path $sumiRoot 'src\Sumi\Sumi.csproj') -- --data-dir (Join-Path $sumiRoot '.dev-data')
}
