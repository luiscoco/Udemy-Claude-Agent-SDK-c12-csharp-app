using System.Text.Json;

namespace LessonC12.Services;

public record ExperimentResult(string Source, string Outcome, string Summary,
    Dictionary<string, string> Evidence, string[] Steps);
public record Scenario(string Id, string Label, JsonElement Input, string Expected,
    Dictionary<string, string> Checks);

public static class Lesson
{
    public const string Title = "Interrupts, Cancellation and Cleanup";
    public const string Description = "People use cancel to describe several different actions. Aborting the browser request stops the browser from waiting.";
    public const string Scope = "Executes a cleanup state machine with authored provider statuses. No remote interrupt or archive is issued by the local mode. Each run starts from a fresh local seed. The run journal stores results, not production workflow state.";
    public static readonly string[] Concepts = ["Independent cleanup deadline", "Interrupt acknowledgement is not a stop", "Confirm idle or terminated"];
    public static readonly Scenario[] Cases =
    [
        new("0", "Confirm and archive", JsonSerializer.SerializeToElement(new { statuses = new[] { "running", "rescheduling", "idle" }, archiveSucceeds = true }), "passed", new() { ["remoteStop"] = "confirmed" }),
        new("1", "Preserve stop uncertainty", JsonSerializer.SerializeToElement(new { statuses = new[] { "running", "rescheduling" }, archiveSucceeds = true }), "unconfirmed", new() { ["remoteStop"] = "unconfirmed" }),
        new("2", "Preserve an archive failure", JsonSerializer.SerializeToElement(new { statuses = new[] { "idle" }, archiveSucceeds = false }), "paused", new() { ["cleanup"] = "pending" })
    ];

    public static ExperimentResult Run(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an object");
        if (!input.TryGetProperty("statuses", out var statuses) || statuses.ValueKind != JsonValueKind.Array || statuses.GetArrayLength() > 200)
            throw new ArgumentException("statuses must be a list of at most 200 values");
        var settled = statuses.EnumerateArray().Select(s => s.ValueKind == JsonValueKind.String ? s.GetString() : s.ToString())
            .FirstOrDefault(s => s is "idle" or "terminated");
        if (settled is null)
            return new("local-experiment", "unconfirmed", "No settled state was observed before the cleanup window ended",
                new() { ["remoteStop"] = "unconfirmed", ["cleanup"] = "pending" },
                ["Close host admission", "Send interrupt through a fresh cleanup signal", "Poll within the independent deadline"]);

        // Match the original reducer: archiveSucceeds is read only for idle.
        var cleanup = "not-needed";
        if (settled == "idle")
        {
            var archive = false;
            if (input.TryGetProperty("archiveSucceeds", out var value) && value.ValueKind != JsonValueKind.Null)
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("archiveSucceeds must be true or false");
                archive = value.GetBoolean();
            }
            cleanup = archive ? "archived" : "pending";
        }
        return new("local-experiment", cleanup == "pending" ? "paused" : "passed", "Remote stop and cleanup are distinct outcomes",
            new() { ["remoteStop"] = "confirmed", ["lastStatus"] = settled, ["cleanup"] = cleanup },
            ["Do not reuse an aborted run signal", "Rescheduling is not settled", "Archive only after confirming idle"]);
    }
}
