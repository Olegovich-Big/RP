$ErrorActionPreference = 'Stop'
$labRoot = Split-Path -Parent $PSScriptRoot
$environmentFile = Join-Path $labRoot '.env'
if (-not (Test-Path -LiteralPath $environmentFile)) {
    function New-LocalSecret {
        $bytes = New-Object byte[] 32
        $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
        return [Convert]::ToBase64String($bytes)
    }
    $content = "REDIS_PASSWORD=$(New-LocalSecret)`nRABBITMQ_USER=valuator`nRABBITMQ_PASSWORD=$(New-LocalSecret)`n"
    [IO.File]::WriteAllText($environmentFile, $content)
    Write-Host 'Created lb7/.env with random local middleware passwords. Keep this file with the lab data; it is excluded from Git.'
}
