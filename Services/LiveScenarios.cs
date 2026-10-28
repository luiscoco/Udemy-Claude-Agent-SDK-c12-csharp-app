using System.Diagnostics;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Sessions;
using Anthropic.Models.Beta.Sessions.Events;

namespace LessonC12.Services;

public record LiveScenario(string Id, string Label, string Description);
public record LiveObservation(DateTimeOffset At, string Phase, string Status);
public record LiveScenarioResult(
    string Source, string Outcome, string Summary, string Scenario, string? SessionId,
    bool WorkAccepted, bool ActiveStatusObserved, bool InterruptAcknowledged,
    string RemoteStop, string Cleanup, int CleanupWindowSeconds,
    List<LiveObservation> Observations, List<string> Errors, List<string> Steps);

public sealed class LiveScenarios(
    IConfiguration configuration, ILogger<LiveScenarios> logger,
    Func<AnthropicClient>? clientFactory = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{

    public async Task<LiveScenarioResult> RecheckAsync(LiveScenarioResult previous)
    {
        if (string.IsNullOrWhiteSpace(previous.SessionId)) throw new ArgumentException("A recorded session ID is required.");
        if (!LocalConfiguration.Valid(configuration["ANTHROPIC_API_KEY"]))
            throw new ArgumentException("Configure the API key before rechecking cleanup.");
        using var client = clientFactory?.Invoke() ?? new AnthropicClient
        {
            ApiKey = configuration["ANTHROPIC_API_KEY"]!, MaxRetries = 0, Timeout = TimeSpan.FromSeconds(15)
        };
        // Rechecking does not start work and has its own bounded cleanup token.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var observations = new List<LiveObservation>();
        var errors = new List<string>();
        var steps = new List<string> { "Rechecking the recorded session; no session or work message is created." };
        var remoteStop = "unconfirmed";
        var cleanupState = "pending";
        var interruptAcknowledged = false;
        var interruptAttempted = false;
        void Failure(string phase, Exception error)
        {
            errors.Add($"{phase}: {error.GetType().Name}");
            logger.LogWarning("Cleanup recheck {Phase} failed: {Type}", phase, error.GetType().Name);
        }
        try
        {
            while (true)
            {
                var session = await client.Beta.Sessions.Retrieve(previous.SessionId,
                    new() { Betas = [LiveCommentary.Beta] }, deadline.Token);
                var data = JsonSerializer.SerializeToElement(session);
                var status = data.GetProperty("status").GetString() ?? "unknown";
                observations.Add(new(DateTimeOffset.UtcNow, interruptAttempted ? "after-recheck-interrupt" : "recheck", status));
                if (status is "idle" or "terminated")
                {
                    remoteStop = "confirmed";
                    steps.Add($"Fresh observation confirmed {status}.");
                    if (data.TryGetProperty("archived_at", out var archivedAt) && archivedAt.ValueKind != JsonValueKind.Null)
                    {
                        cleanupState = "archived";
                        steps.Add("Provider already reports archived_at; no archive request repeated.");
                    }
                    else if (status == "terminated")
                    {
                        cleanupState = "not-needed";
                        steps.Add("Terminated session needs no archive in this model.");
                    }
                    else
                    {
                        try
                        {
                            await client.Beta.Sessions.Archive(previous.SessionId,
                                new() { Betas = [LiveCommentary.Beta] }, deadline.Token);
                            cleanupState = "archived";
                            steps.Add("Provider acknowledged archive after a fresh idle observation.");
                        }
                        catch (Exception error) { Failure("archive", error); }
                    }
                    break;
                }
                if (!interruptAttempted)
                {
                    interruptAttempted = true;
                    try
                    {
                        await client.Beta.Sessions.Events.Send(previous.SessionId,
                            new() { Events = [new BetaManagedAgentsUserInterruptEventParams()], Betas = [LiveCommentary.Beta] },
                            deadline.Token);
                        interruptAcknowledged = true;
                        steps.Add("Provider acknowledged a fresh interrupt; continuing to observe the stop.");
                    }
                    catch (Exception error) { Failure("interrupt", error); }
                }
                await (delay?.Invoke(TimeSpan.FromMilliseconds(500), deadline.Token)
                    ?? Task.Delay(TimeSpan.FromMilliseconds(500), deadline.Token));
            }
        }
        catch (Exception error) { Failure("stop-observation", error); }
        var outcome = remoteStop != "confirmed" ? "unconfirmed" : cleanupState == "pending" ? "paused" : "passed";
        var summary = outcome == "passed" ? "Recorded session cleanup completed; no new session was created."
            : outcome == "paused" ? "Stop confirmed; archive remains pending. Recheck before retrying."
            : "Current stop could not be confirmed. Cleanup remains pending.";
        return new("live-provider-cleanup", outcome, summary, previous.Scenario, previous.SessionId,
            previous.WorkAccepted, previous.ActiveStatusObserved, interruptAcknowledged, remoteStop, cleanupState,
            20, observations, errors, steps);
    }

    public static readonly LiveScenario[] Cases =
    [
        new("confirm-and-archive", "Interrupt, confirm and archive",
            "Send work, interrupt it, poll for idle or terminated for up to 20 seconds, and archive only after idle."),
        new("short-cleanup-window", "Try a short cleanup window",
            "Allow one second for interrupt and stop observation. A fast stop may still succeed; uncertainty is never forced."),
        new("defer-archive", "Confirm stop and defer archive",
            "Confirm the real stop, then deliberately leave an idle session unarchived to demonstrate pending cleanup. This is not an archive failure.")
    ];

    public async Task<LiveScenarioResult> RunAsync(string scenario, CancellationToken cancellationToken = default)
    {
        if (!Cases.Any(item => item.Id == scenario)) throw new ArgumentException("Choose a valid live scenario.");
        if (!new[] { "ANTHROPIC_API_KEY", "CLAUDE_MANAGED_AGENT_ID", "CLAUDE_MANAGED_ENVIRONMENT_ID" }
            .All(key => LocalConfiguration.Valid(configuration[key])))
            throw new ArgumentException("Configure the API key, agent ID and environment ID before running a live scenario.");

        using var client = clientFactory?.Invoke() ?? new AnthropicClient
        {
            ApiKey = configuration["ANTHROPIC_API_KEY"]!, MaxRetries = 0, Timeout = TimeSpan.FromSeconds(15)
        };
        var observations = new List<LiveObservation>();
        var errors = new List<string>();
        var steps = new List<string>();
        string? sessionId = null;
        var workAccepted = false;
        var activeObserved = false;
        var interruptAcknowledged = false;
        var remoteStop = "unconfirmed";
        var cleanupState = "pending";
        var cleanupSeconds = scenario == "short-cleanup-window" ? 1 : 20;

        void RecordFailure(string phase, Exception error)
        {
            // Exception bodies can contain supplied configuration. Retain the error type only.
            errors.Add($"{phase}: {error.GetType().Name}");
            logger.LogWarning("Live scenario {Phase} failed: {ErrorType}", phase, error.GetType().Name);
        }
        Task Pause(CancellationToken token) => delay?.Invoke(TimeSpan.FromMilliseconds(500), token)
            ?? Task.Delay(TimeSpan.FromMilliseconds(500), token);
        async Task<string> Observe(string phase, CancellationToken token)
        {
            var session = await client.Beta.Sessions.Retrieve(sessionId!, new() { Betas = [LiveCommentary.Beta] }, token);
            var status = JsonSerializer.SerializeToElement(session).GetProperty("status").GetString() ?? "unknown";
            observations.Add(new(DateTimeOffset.UtcNow, phase, status));
            return status;
        }

        // The work token may be cancelled by the caller. Cleanup below deliberately does not inherit it.
        using var work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        work.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var session = await client.Beta.Sessions.Create(new()
            {
                Agent = new BetaManagedAgentsAgentWithOverridesParams
                {
                    ID = configuration["CLAUDE_MANAGED_AGENT_ID"]!,
                    System = "You are participating in a cancellation teaching exercise. Produce text only. No tools are available.",
                    Tools = [], McpServers = [], Skills = []
                },
                EnvironmentID = configuration["CLAUDE_MANAGED_ENVIRONMENT_ID"]!,
                Budget = new BetaManagedAgentsBudgetLimit { MaxListCost = new() { Amount = "1", Currency = "USD" } },
                Betas = [LiveCommentary.Beta]
            }, work.Token);
            sessionId = session.ID;
            steps.Add("Created a fresh text-only session with a $1 budget and no retries.");
            await client.Beta.Sessions.Events.Send(sessionId, new()
            {
                Events = [new BetaManagedAgentsUserMessageEventParams
                {
                    Content = [new BetaManagedAgentsTextBlock
                    {
                        Text = "Write a detailed numbered explanation of 100 considerations for reliable cancellation and cleanup in distributed systems."
                    }]
                }],
                Betas = [LiveCommentary.Beta]
            }, work.Token);
            workAccepted = true;
            steps.Add("Provider acknowledged the work message. This alone does not prove model execution.");

            // Observe activity when possible, but do not invent it if the work finishes quickly.
            var started = Stopwatch.GetTimestamp();
            do
            {
                var status = await Observe("before-interrupt", work.Token);
                if (status is "running" or "rescheduling") { activeObserved = true; break; }
                if (status == "terminated") break;
                await Pause(work.Token);
            } while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5));
        }
        catch (Exception error) { RecordFailure("work", error); }
        finally
        {
            if (sessionId is not null)
            {
                steps.Add("Closed work admission; started an independent cleanup deadline.");
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(cleanupSeconds));
                try
                {
                    await client.Beta.Sessions.Events.Send(sessionId, new()
                    {
                        Events = [new BetaManagedAgentsUserInterruptEventParams()], Betas = [LiveCommentary.Beta]
                    }, cleanup.Token);
                    interruptAcknowledged = true;
                    steps.Add("Provider acknowledged the interrupt; stop is still unconfirmed.");
                }
                catch (Exception error) { RecordFailure("interrupt", error); }

                try
                {
                    while (true)
                    {
                        var status = await Observe("after-interrupt", cleanup.Token);
                        if (status is "idle" or "terminated")
                        {
                            remoteStop = "confirmed";
                            steps.Add($"Observed {status} after the interrupt attempt.");
                            if (status == "terminated") cleanupState = "not-needed";
                            else if (scenario == "defer-archive")
                            {
                                cleanupState = "deferred";
                                steps.Add("Deliberately deferred archive; the real session remains idle and unarchived.");
                            }
                            else
                            {
                                try
                                {
                                    await client.Beta.Sessions.Archive(sessionId, new() { Betas = [LiveCommentary.Beta] }, cleanup.Token);
                                    cleanupState = "archived";
                                    steps.Add("Provider acknowledged archive after idle was observed.");
                                }
                                catch (Exception error) { RecordFailure("archive", error); }
                            }
                            break;
                        }
                        await Pause(cleanup.Token);
                    }
                }
                catch (Exception error) { RecordFailure("stop-observation", error); }
            }
        }

        var outcome = remoteStop != "confirmed" ? "unconfirmed"
            : cleanupState is "pending" or "deferred" ? "paused"
            : !workAccepted ? "incomplete" : "passed";
        var summary = remoteStop != "confirmed"
            ? sessionId is null
                ? "Session creation was not confirmed. Inspect your account before retrying."
                : "No settled stop was confirmed within the cleanup window. Inspect the session before retrying."
            : cleanupState == "deferred" ? "Remote stop confirmed; archive deliberately deferred."
            : cleanupState == "pending" ? "Remote stop confirmed; archive remains pending."
            : !workAccepted ? "Cleanup completed, but the work message was not confirmed."
            : "Real session stop and cleanup confirmed. Observations describe what the provider actually reported.";
        return new("live-provider-scenario", outcome, summary, scenario, sessionId, workAccepted, activeObserved,
            interruptAcknowledged, remoteStop, cleanupState, cleanupSeconds, observations, errors, steps);
    }
}

