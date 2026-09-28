$ErrorActionPreference = 'Stop'
$labRoot = Split-Path -Parent $PSScriptRoot
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker Desktop is required.'
}
# Preserve Redis data and the shared key ring; never use down --volumes here.
docker compose -f (Join-Path $labRoot 'compose.yaml') --profile extra down
if ($LASTEXITCODE -ne 0) { throw 'Could not stop the lab containers.' }
Write-Host 'Lab stopped. Redis, RabbitMQ data and shared keys are preserved.'
