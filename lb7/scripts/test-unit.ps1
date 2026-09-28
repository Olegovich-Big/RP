param([switch]$WithRedis)
$ErrorActionPreference = 'Stop'
$labRoot = Split-Path -Parent $PSScriptRoot
$previousRedis = $env:VALUATOR_TEST_REDIS
try {
    if ($WithRedis) {
        $settings = @{}
        Get-Content -LiteralPath (Join-Path $labRoot '.env') | ForEach-Object {
            if ($_ -match '^([^#=]+)=(.*)$') { $settings[$Matches[1]] = $Matches[2] }
        }
        if (-not $settings['REDIS_PASSWORD']) { throw 'REDIS_PASSWORD missing from .env' }
        $env:VALUATOR_TEST_REDIS = 'localhost:6379,password=' + $settings['REDIS_PASSWORD']
    }
    dotnet test (Join-Path $labRoot 'ds-2024.sln') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Unit/integration tests failed.' }
}
finally { $env:VALUATOR_TEST_REDIS = $previousRedis }
