param(
    [ValidateSet('round_robin', 'least_conn', 'ip_hash')]
    [string]$Method = 'round_robin',
    [switch]$ThirdInstance,
    [ValidateRange(1, 20)]
    [int]$Workers = 2,
    [ValidateRange(0, 30000)]
    [int]$ProcessingDelayMs = 0
)
$ErrorActionPreference = 'Stop'
$labRoot = Split-Path -Parent $PSScriptRoot
function Wait-App([string]$Url) {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 3
            if ($response.StatusCode -eq 200) { return }
        }
        catch { Start-Sleep -Milliseconds 500 }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Application did not become ready: $Url"
}
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Install and start Docker Desktop with Linux containers first.'
}
$previousMethod = $env:LB_METHOD
$previousReplicas = $env:LB_REPLICAS
$previousDelay = $env:PROCESSING_DELAY_MS
try {
    $env:LB_METHOD = $Method
    $env:LB_REPLICAS = if ($ThirdInstance) { 'three' } else { 'two' }
    $env:PROCESSING_DELAY_MS = "$ProcessingDelayMs"
    $composeArgs = @('compose', '-f', (Join-Path $labRoot 'compose.yaml'), '--profile', 'extra')
    & docker @composeArgs config --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Invalid Docker Compose configuration.' }
    # Warm the common key ring on app1 before app2/app3 start.
    & docker @composeArgs up -d --build --scale "rankcalculator=$Workers" redis rabbitmq eventslogger1 eventslogger2 rankcalculator app1
    if ($LASTEXITCODE -ne 0) { throw 'Could not start Redis/RabbitMQ/loggers/workers/app1.' }
    Wait-App 'http://localhost:5001/'
    $services = @('app2')
    if ($ThirdInstance) { $services += 'app3' }
    & docker @composeArgs up -d --build @services
    if ($LASTEXITCODE -ne 0) { throw 'Could not start application replicas.' }
    Wait-App 'http://localhost:5002/'
    if ($ThirdInstance) { Wait-App 'http://localhost:5003/' }
    & docker @composeArgs up -d --force-recreate --no-deps nginx
    if ($LASTEXITCODE -ne 0) { throw 'Could not start Nginx.' }
    & docker @composeArgs exec -T nginx nginx -t
    if ($LASTEXITCODE -ne 0) { throw 'Nginx configuration check failed.' }
    Wait-App 'http://localhost:8080/'
    if (-not $ThirdInstance) {
        & docker @composeArgs stop app3
        if ($LASTEXITCODE -ne 0) { throw 'Could not stop the optional replica.' }
    }
    Write-Host "Ready: http://localhost:8080/ (method: $Method, workers: $Workers)"
}
finally {
    $env:LB_METHOD = $previousMethod
    $env:LB_REPLICAS = $previousReplicas
    $env:PROCESSING_DELAY_MS = $previousDelay
}
