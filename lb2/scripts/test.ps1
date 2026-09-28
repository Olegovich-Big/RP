param(
    [ValidateSet('round_robin', 'least_conn', 'ip_hash')]
    [string]$Method = 'round_robin',
    [switch]$ThirdInstance,
    [switch]$TestFailover
)
$ErrorActionPreference = 'Stop'
$labRoot = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.AllowAutoRedirect = $false
$handler.CookieContainer = New-Object System.Net.CookieContainer
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(15)

function Get-Page([string]$Url) {
    $response = $client.GetAsync($Url).GetAwaiter().GetResult()
    if ([int]$response.StatusCode -ne 200) { throw "GET $Url returned $($response.StatusCode)" }
    return $response
}

function Submit-Text([string]$FormUrl, [string]$PostUrl, [string]$Text) {
    $page = Get-Page $FormUrl
    $html = $page.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $tokenMatch = [regex]::Match($html, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
    if (-not $tokenMatch.Success) { throw 'Antiforgery token missing.' }
    $fields = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    $fields.Add('Text', $Text)
    $fields.Add('__RequestVerificationToken', [System.Net.WebUtility]::HtmlDecode($tokenMatch.Groups[1].Value))
    $body = New-Object System.Net.Http.FormUrlEncodedContent($fields)
    $response = $client.PostAsync($PostUrl, $body).GetAwaiter().GetResult()
    if ([int]$response.StatusCode -ne 302) { throw "POST returned $($response.StatusCode); expected 302." }
    return $response.Headers.Location.OriginalString
}

try {
    $expectedInstances = @('app1', 'app2')
    if ($ThirdInstance) { $expectedInstances += 'app3' }
    foreach ($port in @(5001, 5002)) { $null = Get-Page "http://localhost:$port/" }
    if ($ThirdInstance) { $null = Get-Page 'http://localhost:5003/' }

    $instances = @{}
    1..18 | ForEach-Object {
        $response = Get-Page 'http://localhost:8080/health'
        $name = @($response.Headers.GetValues('X-Valuator-Instance'))[0]
        $instances[$name] = 1 + $instances[$name]
    }
    if ($Method -eq 'ip_hash') {
        if ($instances.Count -ne 1) { throw 'IP hash did not keep this client on one replica.' }
    }
    else {
        foreach ($name in $expectedInstances) {
            if (-not $instances.ContainsKey($name)) { throw "No request reached $name." }
        }
    }
    Write-Host "Distribution: $($instances | ConvertTo-Json -Compress)"

    $text = 'Lab two ' + [Guid]::NewGuid().ToString()
    # GET and POST deliberately use different replicas with the same cookie jar.
    $location = Submit-Text 'http://localhost:5001/' 'http://localhost:5002/' $text
    $first = Get-Page ('http://localhost:5001' + $location)
    $second = Get-Page ('http://localhost:5002' + $location)
    $firstHtml = $first.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $secondHtml = $second.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ($firstHtml -notmatch 'id="similarity">0<' -or $secondHtml -notmatch 'id="similarity">0<') {
        throw 'A new text must have similarity 0 on both replicas.'
    }
    $duplicateLocation = Submit-Text 'http://localhost:8080/' 'http://localhost:8080/' $text
    $duplicate = Get-Page ('http://localhost:8080' + $duplicateLocation)
    if ($duplicate.Content.ReadAsStringAsync().GetAwaiter().GetResult() -notmatch 'id="similarity">1<') {
        throw 'Duplicate text must have similarity 1.'
    }

    if ($TestFailover) {
        try {
            docker compose -f (Join-Path $labRoot 'compose.yaml') stop app1
            if ($LASTEXITCODE -ne 0) { throw 'Could not stop app1 for the failure experiment.' }
            1..10 | ForEach-Object {
                $response = Get-Page 'http://localhost:8080/health'
                if (@($response.Headers.GetValues('X-Valuator-Instance'))[0] -eq 'app1') {
                    throw 'Request reached stopped app1.'
                }
            }
            $null = Get-Page ('http://localhost:8080' + $location)
            Write-Host 'Failover passed; previously saved result remains accessible.'
        }
        finally {
            docker compose -f (Join-Path $labRoot 'compose.yaml') start app1
            if ($LASTEXITCODE -ne 0) { throw 'Could not restore app1.' }
        }
    }
    Write-Host 'Passed: balancing, shared antiforgery tokens, shared results, duplicate detection.'
}
finally {
    $client.Dispose()
    $handler.Dispose()
}
