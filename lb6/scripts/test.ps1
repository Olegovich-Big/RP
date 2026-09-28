param(
    [ValidateSet('round_robin', 'least_conn', 'ip_hash')]
    [string]$Method = 'round_robin',
    [switch]$ThirdInstance,
    [switch]$TestFailover,
    [switch]$TestAsync,
    [switch]$TestEvents,
    [switch]$TestShards
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

function Submit-Text([string]$FormUrl, [string]$PostUrl, [string]$Text, [string]$Country = 'Russia', [int]$ExpectedStatus = 302) {
    $page = Get-Page $FormUrl
    $html = $page.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $tokenMatch = [regex]::Match($html, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
    if (-not $tokenMatch.Success) { throw 'Antiforgery token missing.' }
    $fields = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    $fields.Add('Text', $Text)
    $fields.Add('Country', $Country)
    $fields.Add('__RequestVerificationToken', [System.Net.WebUtility]::HtmlDecode($tokenMatch.Groups[1].Value))
    $body = New-Object System.Net.Http.FormUrlEncodedContent($fields)
    $response = $client.PostAsync($PostUrl, $body).GetAwaiter().GetResult()
    if ([int]$response.StatusCode -ne $ExpectedStatus) { throw "POST returned $($response.StatusCode); expected $ExpectedStatus." }
    if ($ExpectedStatus -ne 302) { return $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() }
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

function Wait-Events([string]$Service, [string]$Id, [double]$Rank, [double]$Similarity) {
    $subscriber = $Service.Replace('events', '')
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        $lines = & docker compose -f (Join-Path $labRoot 'compose.yaml') logs --no-color $Service
        if ($LASTEXITCODE -ne 0) { throw "Could not read logs for $Service" }
        $seen = @{}
        foreach ($line in $lines) {
            $match = [regex]::Match($line, ('EVENT ' + $subscriber + ' (\{.*\})'))
            if (-not $match.Success) { continue }
            $event = $match.Groups[1].Value | ConvertFrom-Json
            if ($event.TextId -ne $Id) { continue }
            $expected = if ($event.Type -eq 'RankCalculated') { $Rank } else { $Similarity }
            if ($event.Type -notin @('RankCalculated', 'SimilarityCalculated')) { throw 'Unknown event type.' }
            if ([Math]::Abs([double]$event.Value - $expected) -gt 0.000001) { throw "Wrong event value in $Service" }
            $seen[$event.Type] = $true
        }
        if ($seen.Count -eq 2) { return }
        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "$Service did not receive both calculation events for $Id."
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

    if ($TestEvents) {
        $id = [regex]::Match($location, 'id=([0-9a-f-]+)').Groups[1].Value
        $duplicateId = [regex]::Match($duplicateLocation, 'id=([0-9a-f-]+)').Groups[1].Value
        $null = Wait-Result $duplicateLocation
        foreach ($service in @('eventslogger1', 'eventslogger2')) {
            Wait-Events $service $id $expectedRank 0
            Wait-Events $service $duplicateId $expectedRank 1
        }
        # A stopped subscriber must not prevent the other subscriber from receiving events.
        try {
            docker compose -f (Join-Path $labRoot 'compose.yaml') stop eventslogger2
            if ($LASTEXITCODE -ne 0) { throw 'Could not stop logger2.' }
            $offlineLocation = Submit-Text 'http://localhost:8080/' 'http://localhost:8080/' $text
            $null = Wait-Result $offlineLocation
            $offlineId = [regex]::Match($offlineLocation, 'id=([0-9a-f-]+)').Groups[1].Value
            Wait-Events 'eventslogger1' $offlineId $expectedRank 1
        }
        finally {
            docker compose -f (Join-Path $labRoot 'compose.yaml') start eventslogger2
            if ($LASTEXITCODE -ne 0) { throw 'Could not restore logger2.' }
        }
        Wait-Events 'eventslogger2' $offlineId $expectedRank 1
        Write-Host 'Pub/sub passed: both loggers received both event types and logger2 caught up after downtime.'
    }

    if ($TestShards) {
        function Read-Redis([string]$Region, [string[]]$Arguments) {
            $output = & docker compose -f (Join-Path $labRoot 'compose.yaml') exec -T ("redis-" + $Region.ToLowerInvariant()) redis-cli --raw @Arguments
            if ($LASTEXITCODE -ne 0) { throw "Redis query failed for $Region" }
            $value = ($output -join "`n").Trim()
            if ($value -match '^ERR ') { throw "Redis command failed: $value" }
            return $value
        }
        $regionalText = 'All countries ' + [Guid]::NewGuid()
        $expectedRank = [regex]::Matches($regionalText, '[^a-zA-Zа-яА-ЯёЁ]').Count / [double]$regionalText.Length
        $countries = [ordered]@{ Russia = 'RU'; France = 'EU'; Germany = 'EU'; UAE = 'ASIA'; India = 'ASIA' }
        $form = (Get-Page 'http://localhost:8080/').Content.ReadAsStringAsync().GetAwaiter().GetResult()
        foreach ($country in $countries.Keys) {
            if ($form -notmatch ('value="' + $country + '"')) { throw "Country missing from dropdown: $country" }
        }
        $beforeInvalid = Read-Redis 'MAIN' @('DBSIZE')
        $invalidHtml = Submit-Text 'http://localhost:8080/' 'http://localhost:8080/' $regionalText 'USA' 200
        if ($invalidHtml -notmatch 'validation-summary-errors' -or (Read-Redis 'MAIN' @('DBSIZE')) -ne $beforeInvalid) {
            throw 'Invalid country must show a validation error without creating a route.'
        }
        $seenRegions = @{}
        foreach ($country in $countries.Keys) {
            $region = $countries[$country]
            $location = Submit-Text 'http://localhost:5001/' 'http://localhost:5002/' $regionalText $country
            $id = [regex]::Match($location, 'id=([0-9a-f-]+)').Groups[1].Value
            $html = Wait-Result $location
            if ($html -notmatch ('id="country">' + $country + '<') -or $html -notmatch ('id="region">' + $region + '<')) {
                throw "Wrong country/region on Summary for $country"
            }
            $similarity = if ($seenRegions.ContainsKey($region)) { 1 } else { 0 }
            if ($html -notmatch ('id="similarity">' + $similarity + '<')) { throw 'Duplicates must be scoped to region.' }
            $seenRegions[$region] = $true
            if ((Read-Redis 'MAIN' @('GET', $id)) -ne $region) { throw "Wrong shard map for $id" }
            foreach ($candidate in @('RU', 'EU', 'ASIA')) {
                $expected = if ($candidate -eq $region) { '1' } else { '0' }
                foreach ($prefix in @('TEXT-', 'RANK-', 'SIMILARITY-', 'COUNTRY-', 'REGION-', 'WORKER-')) {
                    if ((Read-Redis $candidate @('EXISTS', ($prefix + $id))) -ne $expected) {
                        throw "Data misplaced: $candidate/$prefix$id"
                    }
                }
            }
            foreach ($service in @('eventslogger1', 'eventslogger2')) {
                Wait-Events $service $id $expectedRank $similarity
            }
            foreach ($service in @('app1', 'app2', 'rankcalculator')) {
                if ($service -eq 'app1') { $null = Get-Page ('http://localhost:5001' + $location) }
                $logs = (& docker compose -f (Join-Path $labRoot 'compose.yaml') logs --no-color $service) -join "`n"
                if ($LASTEXITCODE -ne 0 -or $logs -notmatch ("LOOKUP: " + $id + ", " + $region)) {
                    throw "Missing LOOKUP log in $service for $id"
                }
            }
        }
        $keys = (Read-Redis 'MAIN' @('--scan')) -split "`n"
        foreach ($key in $keys) {
            $parsed = [Guid]::Empty
            if (-not [Guid]::TryParse($key, [ref]$parsed)) { throw "Unexpected MAIN key: $key" }
            if ((Read-Redis 'MAIN' @('GET', $key)) -notin @('RU', 'EU', 'ASIA')) { throw "Invalid route: $key" }
        }
        Write-Host 'Sharding passed: five countries, three isolated regions, map-only MAIN, LOOKUP logs, regional duplicates and events.'
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
