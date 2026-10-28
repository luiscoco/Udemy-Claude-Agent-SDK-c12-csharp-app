param([string]$BaseUrl = "http://127.0.0.1:5012")
$ErrorActionPreference = "Stop"
$lesson = Invoke-RestMethod "$BaseUrl/api/lesson"
if ($lesson.liveScenarios.Count -ne 3) { throw "Live scenario metadata missing" }
foreach ($body in @('{}', '{"scenario":"unknown"}', '{"scenario":42}')) {
    $response = Invoke-WebRequest "$BaseUrl/api/live-scenario" -Method Post -ContentType "application/json" -Body $body -SkipHttpErrorCheck
    if ([int]$response.StatusCode -ne 400) { throw "Invalid live scenario must be rejected" }
}
Write-Output "PASS: Live scenario discovery and validation (no provider calls)"
foreach ($body in @('{}', '{"sessionId":"not-recorded-by-this-app"}', '{"sessionId":42}')) {
    $response = Invoke-WebRequest "$BaseUrl/api/live-cleanup" -Method Post -ContentType "application/json" -Body $body -SkipHttpErrorCheck
    if ([int]$response.StatusCode -ne 400) { throw "Invalid or unrecorded cleanup target must be rejected" }
}
$pendingResponse = Invoke-WebRequest "$BaseUrl/api/live-sessions"
if ($pendingResponse.Headers.'Cache-Control' -notcontains 'no-store') { throw "Pending sessions must not be cached" }
Write-Output "PASS: Cleanup discovery, no-cache and recorded-session allowlist"
foreach ($case in $lesson.cases) {
    $result = Invoke-RestMethod "$BaseUrl/api/run" -Method Post -ContentType "application/json" -Body ($case.input | ConvertTo-Json -Depth 10)
    if ($result.outcome -ne $case.expected) { throw "Scenario failed: $($case.label)" }
    foreach ($check in $case.checks.PSObject.Properties) {
        if ($result.evidence.($check.Name) -ne $check.Value) { throw "Evidence mismatch: $($check.Name)" }
    }
    Write-Output "PASS: $($case.label)"
}
$requests = @(
    @{ Body = "{}"; Type = "application/json"; Status = 400; Headers = @{} },
    @{ Body = "{"; Type = "application/json"; Status = 400; Headers = @{} },
    @{ Body = "{}"; Type = "text/plain"; Status = 415; Headers = @{} },
    @{ Body = "{}"; Type = "application/json"; Status = 403; Headers = @{Origin="https://foreign.example"} },
    @{ Body = ('x' * 64001); Type = "application/json"; Status = 413; Headers = @{} }
)
foreach ($request in $requests) {
    $response = Invoke-WebRequest "$BaseUrl/api/run" -Method Post -ContentType $request.Type -Body $request.Body -Headers $request.Headers -SkipHttpErrorCheck
    if ([int]$response.StatusCode -ne $request.Status) { throw "Expected $($request.Status), got $($response.StatusCode)" }
    Write-Output "PASS: HTTP $($request.Status)"
}
foreach ($route in @("/", "/guide.html", "/architecture.html", "/api/source", "/astronaut.png", "/style.css", "/student.css", "/theme.js")) {
    $response = Invoke-WebRequest "$BaseUrl$route"
    if ($response.StatusCode -ne 200) { throw "Route failed: $route" }
    Write-Output "PASS: $route"
}
Write-Output "All HTTP checks passed."

