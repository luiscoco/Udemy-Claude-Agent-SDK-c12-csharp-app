using System.Net;
using System.Text;
using System.Text.Json;

sealed class ScenarioFixtureHandler(string mode, CancellationTokenSource caller) : HttpMessageHandler
{
    public JsonElement SessionBody { get; private set; }
    public bool Archived { get; private set; }
    public bool ArchiveBeforeIdle { get; private set; }
    public bool CleanupTokenWasLive { get; private set; }
    private bool interrupted;
    private bool idleObserved;
    private int afterPolls;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path.EndsWith("/sessions"))
        {
            if (mode == "create-failure") return Failure();
            SessionBody = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            return Json("""{"id":"session_live_fixture","status":"idle"}""");
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/events"))
        {
            var data = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (data.GetProperty("events")[0].GetProperty("type").GetString() == "user.interrupt")
            {
                interrupted = true;
                CleanupTokenWasLive = !cancellationToken.IsCancellationRequested;
                if (mode == "interrupt-failure") return Failure();
            }
            else
            {
                if (mode == "work-failure") return Failure();
                if (mode == "caller-cancel") caller.Cancel();
            }
            return Json("""{"data":[]}""");
        }
        if (request.Method == HttpMethod.Get && path.EndsWith("/session_live_fixture"))
        {
            if (!interrupted)
            {
                if (mode == "caller-after-work") caller.Cancel();
                return Json("""{"id":"session_live_fixture","status":"running"}""");
            }
            if (mode == "short-timeout") await Task.Delay(Timeout.Infinite, cancellationToken);
            var status = mode == "terminated" ? "terminated" : ++afterPolls == 1 ? "rescheduling" : "idle";
            if (status == "idle") idleObserved = true;
            return Json(JsonSerializer.Serialize(new { id = "session_live_fixture", status }));
        }
        if (request.Method == HttpMethod.Post && path.EndsWith("/archive"))
        {
            ArchiveBeforeIdle = !idleObserved;
            if (mode == "archive-failure") return Failure();
            Archived = true;
            return Json("""{"id":"session_live_fixture","status":"idle"}""");
        }
        throw new Exception("Unexpected scenario SDK request: " + path);
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Failure() => new(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
}

