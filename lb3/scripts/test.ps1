param(
    [ValidateSet('round_robin', 'least_conn', 'ip_hash')]
    [string]$Method = 'round_robin',
    [switch]$ThirdInstance,
    [switch]$TestFailover,
    [switch]$TestAsync
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

function Wait-Result([string]$Location) {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        $response = Get-Page ('http://localhost:8080' + $Location)
        $html = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if ($html -match 'id="rank">([^<]+)<') { return $html }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Timed out waiting for rank calculation.'
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

    if ($TestAsync) {
        try {
            docker compose -f (Join-Path $labRoot 'compose.yaml') stop rankcalculator
            if ($LASTEXITCODE -ne 0) { throw 'Could not stop workers.' }
            $pendingLocation = Submit-Text 'http://localhost:8080/' 'http://localhost:8080/' ('Pending ' + [Guid]::NewGuid())
            $pending = Get-Page ('http://localhost:8080' + $pendingLocation)
            $pendingHtml = $pending.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($pendingHtml -notmatch 'id="pending"' -or $pendingHtml -match 'id="rank"') {
                throw 'Rank must remain pending while all workers are stopped.'
            }
        }
        finally {
            docker compose -f (Join-Path $labRoot 'compose.yaml') start rankcalculator
            if ($LASTEXITCODE -ne 0) { throw 'Could not restart workers.' }
        }
        $null = Wait-Result $pendingLocation
        $locations = @()
        1..16 | ForEach-Object {
            $locations += Submit-Text 'http://localhost:8080/' 'http://localhost:8080/' ('Concurrent ' + [Guid]::NewGuid())
        }
        $workers = @{}
        foreach ($location in $locations) {
            $html = Wait-Result $location
            $workerMatch = [regex]::Match($html, 'id="worker">([^<]+)<')
            if (-not $workerMatch.Success) { throw 'Worker identity is missing.' }
            $workers[$workerMatch.Groups[1].Value] = $true
        }
        if ($workers.Count -lt 2) { throw 'Expected at least two competing workers; start with -Workers 2.' }
        Write-Host "Async processing passed; completed jobs from $($workers.Count) workers."
    }

    $text = 'Lab two ' + [Guid]::NewGuid().ToString()
    # GET and POST deliberately use different replicas with the same cookie jar.
    $location = Submit-Text 'http://localhost:5001/' 'http://localhost:5002/' $text
    $completedHtml = Wait-Result $location
    $rankMatch = [regex]::Match($completedHtml, 'id="rank">([^<]+)<')
    $rank = [double]::Parse($rankMatch.Groups[1].Value.Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture)
    $expectedRank = [regex]::Matches($text, '[^a-zA-Zа-яА-ЯёЁ]').Count / [double]$text.Length
    if ([Math]::Abs($rank - $expectedRank) -gt 0.000001) { throw 'The worker calculated an incorrect rank.' }
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
