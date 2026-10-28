using System.Text;
using System.Text.Json;

namespace LessonC12.Services;

public record RecordedSession(string SessionId, string Scenario, string Outcome, string RemoteStop, string Cleanup, DateTimeOffset At);

public sealed class LabService(IWebHostEnvironment environment, LiveCommentary provider, LiveScenarios liveScenarios)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public bool LiveConfigured => provider.Configured;
    public bool CleanupConfigured => provider.CleanupConfigured;
    public string Source => File.ReadAllText(Path.Combine(environment.ContentRootPath, "Services", "Lesson.cs"));

    private Dictionary<string, (DateTimeOffset At, LiveScenarioResult Result)> ReadLiveSessions()
    {
        var sessions = new Dictionary<string, (DateTimeOffset At, LiveScenarioResult Result)>(StringComparer.Ordinal);
        foreach (var file in new[] { "live-runs.jsonl", "live-cleanups.jsonl" })
        {
            var path = Path.Combine(environment.ContentRootPath, ".lab-data", file);
            if (!File.Exists(path)) continue;
            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var result = root.GetProperty("result").Deserialize<LiveScenarioResult>(Json);
                    var at = root.GetProperty("at").GetDateTimeOffset();
                    if (result?.SessionId is { Length: > 0 } id
                        && result.Source is "live-provider-scenario" or "live-provider-cleanup"
                        && (!sessions.TryGetValue(id, out var existing) || at >= existing.At))
                        sessions[id] = (at, result);
                }
                catch (JsonException) { /* An interrupted append can leave an incomplete line. */ }
                catch (KeyNotFoundException) { /* Ignore unrelated journal entries. */ }
                catch (FormatException) { /* Ignore malformed timestamps. */ }
            }
        }
        return sessions;
    }

    public RecordedSession[] PendingSessions() => ReadLiveSessions()
        .Where(item => item.Value.Result.Cleanup is not ("archived" or "not-needed"))
        .OrderByDescending(item => item.Value.At)
        .Select(item => new RecordedSession(item.Key, item.Value.Result.Scenario, item.Value.Result.Outcome,
            item.Value.Result.RemoteStop, item.Value.Result.Cleanup, item.Value.At)).ToArray();

    public async Task<LiveScenarioResult> RecheckCleanupAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A recorded session ID is required.");
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("An experiment is already running. Wait for it to finish.");
        try
        {
            // The journal is a local allowlist: arbitrary account sessions cannot be targeted.
            if (!ReadLiveSessions().TryGetValue(sessionId, out var recorded))
                throw new ArgumentException("Only sessions recorded by this application can be rechecked.");
            var result = await liveScenarios.RecheckAsync(recorded.Result);
            var directory = Path.Combine(environment.ContentRootPath, ".lab-data");
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(Path.Combine(directory, "live-cleanups.jsonl"),
                JsonSerializer.Serialize(new { id = Guid.NewGuid(), at = DateTimeOffset.UtcNow, lesson = 12, result },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n");
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<LiveScenarioResult> RunLiveScenarioAsync(string scenario, CancellationToken cancellationToken = default)
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("An experiment is already running. Wait for it to finish.");
        try
        {
            var result = await liveScenarios.RunAsync(scenario, cancellationToken);
            var directory = Path.Combine(environment.ContentRootPath, ".lab-data");
            Directory.CreateDirectory(directory);
            // Preserve cleanup evidence even when the browser request was cancelled.
            await File.AppendAllTextAsync(Path.Combine(directory, "live-runs.jsonl"),
                JsonSerializer.Serialize(new { id = Guid.NewGuid(), at = DateTimeOffset.UtcNow, lesson = 12, result },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n");
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<object> RunAsync(JsonElement input, bool live, CancellationToken cancellationToken = default)
    {
        if (Encoding.UTF8.GetByteCount(input.GetRawText()) > 64000) throw new ArgumentException("Input exceeds the sample limit");
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("An experiment is already running. Wait for it to finish.");
        try
        {
            var result = Lesson.Run(input);
            if (live) return await provider.ExplainAsync(result, cancellationToken);
            var id = Guid.NewGuid();
            var directory = Path.Combine(environment.ContentRootPath, ".lab-data");
            Directory.CreateDirectory(directory);
            var entry = new { id, at = DateTimeOffset.UtcNow, lesson = 12, result };
            await File.AppendAllTextAsync(Path.Combine(directory, "runs.jsonl"), JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n", cancellationToken);
            return new { id, result.Source, result.Outcome, result.Summary, result.Evidence, result.Steps };
        }
        finally { gate.Release(); }
    }
}
