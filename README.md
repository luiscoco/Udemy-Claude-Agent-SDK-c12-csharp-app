# C12 · Blazor / .NET 10 migration

This standalone C# application migrates ../lesson-c12-app using Blazor Interactive Server and the official Anthropic NuGet package pinned to **13.3.0**. The original TypeScript app is unchanged.

## Run

Install the .NET 10 SDK, then run from this folder:

```powershell
dotnet restore
dotnet run
```

Open http://localhost:5012. No credentials are needed for local scenarios. Stop with Ctrl+C. Copying .env.example sets PORT=5012 and binds to 127.0.0.1; use the URL printed in the terminal. ASPNETCORE_URLS or --urls can be used when PORT is absent.

## Configure live scenarios and commentary

```powershell
Copy-Item .env.example .env
notepad .env
```

Set ANTHROPIC_API_KEY, CLAUDE_MANAGED_AGENT_ID and CLAUDE_MANAGED_ENVIRONMENT_ID. Environment variables and .NET user secrets also work. Existing environment variables take precedence over .env; the standard environment configuration provider takes precedence over user secrets. Never commit credentials. Restart after editing configuration.

If you need resource IDs, explicitly run:

```powershell
dotnet run -- --setup
```

This provisions a limited-network environment before a text-only agent, saves each completed ID immediately in .env, and reuses configured IDs. CLAUDE_MODEL defaults to claude-opus-5-5; choose a model available to your account. A network failure may leave resource creation uncertain: inspect your account before retrying.

Live commentary creates a Managed Agents session using AnthropicClient.Beta.Sessions with the managed-agents-2026-04-01 beta header. It disables tools, MCP servers and skills, sets a $10 session budget, disables retries, and bounds observation to 60 seconds. API use may incur charges. On failure, an independent 3 second token attempts an interrupt and preserves stop uncertainty. No remote archive is issued. Successful commentary reports session ID, answer, stop reason, event types, usage and a hash of the local result. Commentary does not certify an integration.

## Live run scenarios

After configuring credentials, restart the app, find **Live run scenarios**, choose an option and press **Run live scenario**.

| Scenario | Actual behavior |
| --- | --- |
| Interrupt, confirm and archive | Create a fresh session, send work, interrupt, poll for idle or terminated, archive after idle |
| Try a short cleanup window | Allow one second for interrupt and observation; the actual result may succeed or remain unconfirmed |
| Confirm stop and defer archive | Observe the stop, then deliberately leave idle unarchived; this is deferred cleanup, not a provider archive failure |

These runs use real Anthropic SDK operations and a $1 session budget. Work is bounded to 20 seconds; the separate cleanup window is 20 seconds (one second for the short-window scenario). Cancel work; continue cleanup cancels the work token without cancelling cleanup. Only the newly created session is affected. Tools, MCP servers and skills are disabled, and retries are disabled.

The local status array and archive checkbox do not control live results. The app displays the real session ID, work-message acknowledgement, whether activity was observed, interrupt acknowledgement, timestamped statuses, errors, stop confirmation and cleanup outcome. A confirmed stop does not prove that an interrupt caused it. An archive failure is reported only if the real request fails. Unconfirmed or deferred sessions require review in your account.

Results are saved separately to .lab-data/live-runs.jsonl, including cleanup evidence when the work request was cancelled. These journals are sample observations, not a persistent workflow engine. No API calls run automatically on startup.

Scripts can use POST /api/live-scenario with JSON such as:

```json
{"scenario":"confirm-and-archive"}
```

The provider operations follow the [session retrieval](https://platform.claude.com/docs/en/api/csharp/beta/sessions/retrieve) and [archive](https://platform.claude.com/docs/en/api/csharp/beta/sessions/archive) API references.

## Finish cleanup from a previous run

Under **Recheck and finish cleanup**, refresh the pending list, choose a recorded session, then press **Recheck and finish cleanup**. This retrieves its current status without creating a session or sending work. An idle session is archived immediately; an unsettled session receives an interrupt and is polled under a fresh 20 second cleanup deadline. Terminated sessions need no archive in this model. A non-null archived_at avoids repeating an archive.

The server accepts only IDs already present in this app's local live journals. The latest result determines which sessions remain pending. Refreshing or restarting the app retains this list. Cleanup results append to .lab-data/live-cleanups.jsonl, preserving the original observations. A failed archive remains pending; an uncertain archive is rechecked before another attempt.

GET /api/live-sessions lists pending recorded sessions with no-store caching. POST /api/live-cleanup accepts:

```json
{"sessionId":"a_session_id_already_recorded_by_this_app"}
```

All run and cleanup actions share one admission gate. Once admitted, cleanup continues independently of browser cancellation. The journals provide a local recovery allowlist for this teaching app; they are not a production workflow database.

## Migration map

| Original TypeScript | C# / Blazor |
| --- | --- |
| src/lesson.ts | Services/Lesson.cs |
| src/core.ts validation and result | Services/Lesson.cs records and validation |
| src/server.ts | Program.cs and Services/LabService.cs |
| src/provider.ts | Services/LiveCommentary.cs |
| scripts/setup-resources.mts | Services/ResourceSetup.cs; --setup |
| src/client.ts and index.html | Components/Pages/Home.razor |
| guide.html and architecture.html | Guide.razor and Architecture.razor |
| public styling, theme and artwork | wwwroot/ |

The three authored scenarios, editable inputs, evidence, execution steps, theme switch, responsive layout, source view, and guide/architecture URLs are retained. Architecture and walkthrough content now reference the actual C# source. The local reducer uses the first idle or terminated observation. Local runs append to .lab-data/runs.jsonl; each run starts fresh. The journal is not production workflow state.

The JSON endpoints are GET /api/lesson, GET /api/source, POST /api/run, POST /api/live (commentary), POST /api/live-scenario, GET /api/live-sessions and POST /api/live-cleanup. POST bodies are limited to 64,000 bytes, statuses to 200 values, foreign origins are rejected, and all run modes share a concurrency gate. The Blazor circuit invokes the same service directly.

## Verify

```powershell
dotnet build
dotnet run --project verification/Verification.csproj
```

The verification executable checks local behavior and mocked SDK flows, including real-scenario interrupt/poll/archive ordering, short-window expiry, archive failure, deferred archive, termination, work failure, creation uncertainty and caller cancellation with independent cleanup. It makes no real provider calls. Browser verification also inspects the live scenario controls without clicking the paid run button.

Start the application and run the HTTP verification in another terminal:

```powershell
pwsh -File verification/http.ps1
```

Browser verification can be run with Python Playwright installed:

```powershell
python verification/browser.py
```

## Publish

```powershell
dotnet publish -c Release -o publish
Set-Location publish
dotnet LessonC12.dll --urls http://localhost:5012
```

Source files needed by the teaching views are included in publish output. Credentials and the local journal are excluded.

