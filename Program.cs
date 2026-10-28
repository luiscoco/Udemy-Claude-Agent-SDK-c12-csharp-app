using LessonC12.Components;
using LessonC12.Services;
using System.Text.Json;

LocalConfiguration.LoadDotEnv(Directory.GetCurrentDirectory());
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.WebHost.UseStaticWebAssets();
builder.Configuration.AddEnvironmentVariables();
if (args.Contains("--setup"))
{
    try { await ResourceSetup.RunAsync(builder.Configuration, builder.Environment.ContentRootPath); }
    catch (ArgumentException error)
    {
        Console.Error.WriteLine(error.Message);
        Environment.ExitCode = 1;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Setup failed ({error.GetType().Name}). Resource creation may be uncertain; inspect your account before retrying. Completed IDs remain in .env.");
        Environment.ExitCode = 1;
    }
    return;
}
if (!string.IsNullOrWhiteSpace(builder.Configuration["PORT"]))
    builder.WebHost.UseUrls($"http://127.0.0.1:{builder.Configuration["PORT"]}");

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddSingleton<LiveCommentary>();
builder.Services.AddSingleton<LiveScenarios>();
builder.Services.AddSingleton<LabService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapGet("/api/lesson", (LabService lab) => Results.Json(new
{
    number = 12, title = Lesson.Title, description = Lesson.Description,
    concepts = Lesson.Concepts, scope = Lesson.Scope, cases = Lesson.Cases, liveConfigured = lab.LiveConfigured,
    liveScenarios = LiveScenarios.Cases
}));
app.MapGet("/api/source", (LabService lab) => Results.Text(lab.Source, "text/plain"));
app.MapGet("/api/live-sessions", (HttpResponse response, LabService lab) =>
{
    response.Headers.CacheControl = "no-store";
    return Results.Json(lab.PendingSessions());
});
foreach (var path in new[] { "/api/run", "/api/live", "/api/live-scenario", "/api/live-cleanup" })
{
    app.MapPost(path, async (HttpRequest request, LabService lab) =>
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        if (request.Headers.TryGetValue("Origin", out var origin) && origin.ToString() != $"{request.Scheme}://{request.Host}")
            return Results.Json(new { error = "Foreign origin" }, statusCode: 403);
        if (!request.HasJsonContentType()) return Results.Json(new { error = "JSON input required" }, statusCode: 415);
        if (request.ContentLength > 64000) return Results.Json(new { error = "Input exceeds the sample limit" }, statusCode: 413);
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
            {
                if (buffer.Length + read > 64000) return Results.Json(new { error = "Input exceeds the sample limit" }, statusCode: 413);
                buffer.Write(chunk, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (request.Path == "/api/live-cleanup")
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("sessionId", out var sessionId)
                    || sessionId.ValueKind != JsonValueKind.String)
                    return Results.BadRequest(new { error = "A recorded session ID is required." });
                return Results.Json(await lab.RecheckCleanupAsync(sessionId.GetString()!, request.HttpContext.RequestAborted));
            }
            if (request.Path == "/api/live-scenario")
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("scenario", out var scenario)
                    || scenario.ValueKind != JsonValueKind.String)
                    return Results.BadRequest(new { error = "A live scenario ID is required." });
                return Results.Json(await lab.RunLiveScenarioAsync(scenario.GetString()!, request.HttpContext.RequestAborted));
            }
            return Results.Json(await lab.RunAsync(document.RootElement, request.Path == "/api/live", request.HttpContext.RequestAborted));
        }
        catch (JsonException) { return Results.BadRequest(new { error = "Input must be valid JSON" }); }
        catch (ArgumentException error) { return Results.BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return Results.Json(new { error = error.Message }, statusCode: error.Message.StartsWith("An experiment") ? 409 : 400); }
    });
}

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
