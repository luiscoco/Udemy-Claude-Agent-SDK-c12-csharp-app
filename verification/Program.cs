using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using LessonC12.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
    count++;
}
ExperimentResult Run(string json) => Lesson.Run(JsonSerializer.Deserialize<JsonElement>(json));
void Reject(string json, string name)
{
    try { Run(json); } catch (ArgumentException) { Check(true, name); return; }
    throw new Exception("Expected validation failure: " + name);
}
foreach (var scenario in Lesson.Cases)
{
    var result = Lesson.Run(scenario.Input);
    Check(result.Outcome == scenario.Expected && scenario.Checks.All(c => result.Evidence[c.Key] == c.Value), scenario.Label);
}
Check(Run("""{"statuses":["terminated"],"archiveSucceeds":"ignored"}""").Evidence["cleanup"] == "not-needed", "Termination bypasses archive");
Check(Run("""{"statuses":["idle","terminated"],"archiveSucceeds":false}""").Outcome == "paused", "First settled status wins");
Check(Run("""{"statuses":["running","rescheduling"]}""").Outcome == "unconfirmed", "Interrupt/rescheduling does not prove a stop");
Check(Run("""{"statuses":[]}""").Outcome == "unconfirmed", "Empty observation window");
Check(Run("""{"statuses":["idle"]}""").Evidence["cleanup"] == "pending", "Missing archive flag defaults false");
Check(Run("""{"statuses":["idle"],"archiveSucceeds":null}""").Outcome == "paused", "Null archive flag defaults false");
Check(Run(JsonSerializer.Serialize(new { statuses = Enumerable.Repeat("running", 200) })).Outcome == "unconfirmed", "200 statuses accepted");
Reject(JsonSerializer.Serialize(new { statuses = Enumerable.Repeat("running", 201) }), "201 statuses rejected");
Reject("[]", "Object required");
Reject("{}", "Statuses required");
Reject("""{"statuses":"idle"}""", "Statuses array required");
Reject("""{"statuses":["idle"],"archiveSucceeds":"true"}""", "Archive boolean required");

var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ANTHROPIC_API_KEY"] = "test-key", ["CLAUDE_MANAGED_AGENT_ID"] = "agent_test",
    ["CLAUDE_MANAGED_ENVIRONMENT_ID"] = "env_test"
}).Build();
foreach (var mode in new[] { "success", "send-failure", "eof" })
{
    var handler = new FixtureHandler(mode);
    var provider = new LiveCommentary(configuration, NullLogger<LiveCommentary>.Instance,
        () => new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 });
    try
    {
        var response = JsonSerializer.SerializeToElement(await provider.ExplainAsync(Lesson.Run(Lesson.Cases[0].Input)));
        Check(mode == "success" && response.GetProperty("answer").GetString() == "Local cleanup was archived.", "SDK session streaming and text extraction");
        Check(response.GetProperty("stopReason").GetString() == "end_turn", "SDK idle stop reason");
        Check(response.GetProperty("experimentHash").GetString()?.Length == 64, "Experiment evidence hash");
    }
    catch (InvalidOperationException error) when (mode != "success")
    {
        Check(error.Message.Contains("stop unconfirmed"), mode + " preserves uncertainty");
        Check(handler.InterruptSent && handler.CleanupTokenWasLive, mode + " independent cleanup token");
    }
    Check(handler.SessionBody.GetProperty("agent").GetProperty("tools").GetArrayLength() == 0, mode + " text-only session overrides");
    Check(handler.SessionBody.GetProperty("budget").GetProperty("max_list_cost").GetProperty("amount").GetString() == "10", mode + " session budget");
    Check(handler.BetaPresent, mode + " SDK beta header");
}
foreach (var mode in new[] { "success", "short-timeout", "defer", "archive-failure", "terminated", "interrupt-failure", "work-failure", "create-failure", "caller-cancel", "caller-after-work" })
{
    using var caller = new CancellationTokenSource();
    var handler = new ScenarioFixtureHandler(mode, caller);
    var service = new LiveScenarios(configuration, NullLogger<LiveScenarios>.Instance,
        () => new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 },
        (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    var scenario = mode == "short-timeout" ? "short-cleanup-window" : mode == "defer" ? "defer-archive" : "confirm-and-archive";
    var result = await service.RunAsync(scenario, caller.Token);
    Check(result.Source == "live-provider-scenario", mode + " live evidence source");
    var expected = mode switch { "short-timeout" or "create-failure" => "unconfirmed", "defer" or "archive-failure" => "paused", "work-failure" or "caller-cancel" => "incomplete", _ => "passed" };
    Check(result.Outcome == expected, mode + " observed outcome");
    Check(handler.Archived == (mode is "success" or "interrupt-failure" or "work-failure" or "caller-cancel" or "caller-after-work"), mode + " archive policy");
    Check(!handler.ArchiveBeforeIdle, mode + " archive never precedes confirmed idle");
    if (mode != "create-failure") Check(handler.CleanupTokenWasLive, mode + " independent cleanup");
    if (mode == "caller-cancel") Check(caller.IsCancellationRequested && result.RemoteStop == "confirmed", "Caller cancellation does not cancel cleanup");
    if (mode == "caller-after-work") Check(caller.IsCancellationRequested && result.WorkAccepted && result.Cleanup == "archived", "Cancellation after acknowledged work still archives independently");
    if (mode == "short-timeout") Check(result.RemoteStop == "unconfirmed" && !handler.Archived, "Short window preserves uncertainty");
    if (mode == "defer") Check(result.Cleanup == "deferred", "Deferred archive is explicit");
    if (mode == "interrupt-failure") Check(!result.InterruptAcknowledged && result.RemoteStop == "confirmed", "Interrupt failure and observed stop are distinct");
    if (mode != "create-failure")
    {
        Check(handler.SessionBody.GetProperty("budget").GetProperty("max_list_cost").GetProperty("amount").GetString() == "1", mode + " bounded budget");
        Check(handler.SessionBody.GetProperty("agent").GetProperty("tools").GetArrayLength() == 0, mode + " tools disabled");
    }
}
try
{
    var service = new LiveScenarios(configuration, NullLogger<LiveScenarios>.Instance);
    await service.RunAsync("unknown");
    throw new Exception("Unknown scenario accepted");
}
catch (ArgumentException) { Check(true, "Unknown live scenario rejected before network access"); }
var previous = new LiveScenarioResult("live-provider-scenario", "unconfirmed", "Fixture", "short-cleanup-window",
    "session_live_fixture", true, true, true, "unconfirmed", "pending", 1, [], [], []);
foreach (var mode in new[] { "idle", "running", "terminated", "already-archived", "archive-failure", "retrieve-failure", "timeout" })
{
    var handler = new RecoveryFixtureHandler(mode);
    var service = new LiveScenarios(configuration, NullLogger<LiveScenarios>.Instance,
        () => new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 },
        (_, token) => { token.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    var result = await service.RecheckAsync(previous);
    Check(result.Outcome == (mode is "retrieve-failure" or "timeout" ? "unconfirmed" : mode == "archive-failure" ? "paused" : "passed"), mode + " recovery outcome");
    Check(handler.Creates == 0 && !handler.WorkSent, mode + " recovery creates no work or session");
    Check(handler.Interrupted == (mode is "running" or "timeout"), mode + " recovery interrupts only unsettled sessions");
    Check(!handler.ArchiveBeforeIdle, mode + " recovery requires a fresh idle observation");
    if (mode == "already-archived") Check(!handler.Archived && result.Cleanup == "archived", "Already archived recovery is idempotent");
    if (mode == "archive-failure") Check(result.RemoteStop == "confirmed" && result.Cleanup == "pending", "Recovery preserves archive failure");
}
var temporary = Directory.CreateTempSubdirectory("lesson-c12-verification-");
try
{
    var journalDirectory = Path.Combine(temporary.FullName, ".lab-data");
    Directory.CreateDirectory(journalDirectory);
    await File.WriteAllTextAsync(Path.Combine(journalDirectory, "live-runs.jsonl"),
        JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow.AddMinutes(-1), result = previous }, LabService.Json).ReplaceLineEndings("") + "\n{incomplete\n");
    var handler = new RecoveryFixtureHandler("blocked");
    var provider = new LiveCommentary(configuration, NullLogger<LiveCommentary>.Instance);
    var recovery = new LiveScenarios(configuration, NullLogger<LiveScenarios>.Instance,
        () => new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(handler), MaxRetries = 0 });
    var lab = new LabService(new VerificationEnvironment(temporary.FullName), provider, recovery);
    Check(lab.PendingSessions().Length == 1, "Pending sessions survive restart and tolerate an incomplete journal line");
    try { await lab.RecheckCleanupAsync("unrecorded-session"); throw new Exception("Unknown session accepted"); }
    catch (ArgumentException) { Check(true, "Unrecorded sessions rejected before network access"); }
    using var recoveryCaller = new CancellationTokenSource();
    var cleanup = lab.RecheckCleanupAsync("session_live_fixture", recoveryCaller.Token);
    await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    recoveryCaller.Cancel();
    try { await lab.RunAsync(Lesson.Cases[0].Input, false); throw new Exception("Concurrent run admitted"); }
    catch (InvalidOperationException) { Check(true, "Recovery shares the run admission gate"); }
    handler.Release.TrySetResult();
    Check((await cleanup).Cleanup == "archived", "Recorded recovery finishes archive");
    Check(recoveryCaller.IsCancellationRequested, "Admitted recovery completes despite caller cancellation");
    var restarted = new LabService(new VerificationEnvironment(temporary.FullName), provider, recovery);
    Check(restarted.PendingSessions().Length == 0, "Recovery journal removes completed sessions after restart");
    Check(File.ReadAllLines(Path.Combine(journalDirectory, "live-cleanups.jsonl")).Length == 1, "Recovery evidence saved separately");
}
finally
{
    // Only remove the dedicated directory created by this verification run.
    if (temporary.Parent?.FullName == Path.TrimEndingDirectorySeparator(Path.GetTempPath())
        && temporary.Name.StartsWith("lesson-c12-verification-", StringComparison.Ordinal))
        temporary.Delete(recursive: true);
}
Console.WriteLine($"All {count} checks passed. No live provider requests were made.");

sealed class FixtureHandler(string mode) : HttpMessageHandler
{
    public JsonElement SessionBody { get; private set; }
    public bool InterruptSent { get; private set; }
    public bool CleanupTokenWasLive { get; private set; }
    public bool BetaPresent { get; private set; }
    private readonly TaskCompletionSource sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        BetaPresent = request.Headers.TryGetValues("anthropic-beta", out var values) && values.Any(v => v.Contains(LiveCommentary.Beta));
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path.EndsWith("/sessions"))
        {
            SessionBody = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            return Json("""{"id":"session_fixture"}""");
        }
        if (request.Method == HttpMethod.Get && path.EndsWith("/events/stream"))
        {
            await sent.Task.WaitAsync(cancellationToken);
            if (mode == "send-failure") await Task.Delay(Timeout.Infinite, cancellationToken);
            var data = mode == "eof" ? "" : """
event: agent.message
data: {"type":"agent.message","id":"evt_message","processed_at":"2026-10-28T12:00:00Z","content":[{"type":"text","text":"Local cleanup was archived."}]}

event: session.status_idle
data: {"type":"session.status_idle","id":"evt_idle","processed_at":"2026-10-28T12:00:01Z","stop_reason":{"type":"end_turn"}}


""";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/events"))
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (body.GetProperty("events")[0].GetProperty("type").GetString() == "user.interrupt")
            {
                InterruptSent = true;
                CleanupTokenWasLive = !cancellationToken.IsCancellationRequested;
                return Json("""{"data":[]}""");
            }
            sent.TrySetResult();
            return mode == "send-failure" ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") } : Json("""{"data":[]}""");
        }
        throw new Exception("Unexpected SDK request: " + request.Method + " " + path);
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
