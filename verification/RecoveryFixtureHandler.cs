using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

sealed class RecoveryFixtureHandler(string mode) : HttpMessageHandler
{
    public int Creates { get; private set; }
    public bool WorkSent { get; private set; }
    public bool Interrupted { get; private set; }
    public bool Archived { get; private set; }
    public bool ArchiveBeforeIdle { get; private set; }
    private bool idleObserved;
    public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path.EndsWith("/sessions"))
        {
            Creates++;
            throw new Exception("Cleanup must not create a session");
        }
        if (request.Method == HttpMethod.Get && path.EndsWith("/session_live_fixture"))
        {
            if (mode == "retrieve-failure") return Failure();
            if (mode == "timeout" && Interrupted) throw new TaskCanceledException("Fixture deadline");
            if (mode == "blocked")
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            var status = mode == "terminated" ? "terminated" : mode is "running" or "timeout" && !Interrupted ? "running" : "idle";
            idleObserved = status == "idle";
            return Json(JsonSerializer.Serialize(new
            {
                id = "session_live_fixture", status,
                archived_at = mode == "already-archived" ? (DateTimeOffset?)DateTimeOffset.UtcNow : null
            }));
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/events"))
        {
            var body = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(token));
            if (body.GetProperty("events")[0].GetProperty("type").GetString() != "user.interrupt") WorkSent = true;
            Interrupted = true;
            return Json("""{"data":[]}""");
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/archive"))
        {
            ArchiveBeforeIdle = !idleObserved;
            if (mode == "archive-failure") return Failure();
            Archived = true;
            return Json("""{"id":"session_live_fixture","status":"idle"}""");
        }
        throw new Exception("Unexpected recovery request " + path);
    }
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Failure() => new(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
}

sealed class VerificationEnvironment(string root) : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "Verification";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = root;
    public string WebRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}

