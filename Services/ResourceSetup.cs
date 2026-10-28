using Anthropic;
using Anthropic.Models.Beta.Environments;

namespace LessonC12.Services;

public static class ResourceSetup
{
    public static async Task RunAsync(IConfiguration configuration, string root)
    {
        if (!LocalConfiguration.Valid(configuration["ANTHROPIC_API_KEY"]))
            throw new ArgumentException("Set ANTHROPIC_API_KEY before running --setup.");
        using var client = new AnthropicClient { ApiKey = configuration["ANTHROPIC_API_KEY"]!, MaxRetries = 0, Timeout = TimeSpan.FromSeconds(30) };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var path = Path.Combine(root, ".env");
        async Task Save(string key, string value)
        {
            var lines = File.Exists(path) ? (await File.ReadAllLinesAsync(path)).ToList() : [];
            lines.RemoveAll(line => line.TrimStart().StartsWith(key + "=", StringComparison.Ordinal));
            lines.Add($"{key}={value}");
            await File.WriteAllLinesAsync(path, lines);
        }
        if (!LocalConfiguration.Valid(configuration["CLAUDE_MANAGED_ENVIRONMENT_ID"]))
        {
            var environment = await client.Beta.Environments.Create(new()
            {
                Name = "C12 student environment",
                Config = new BetaCloudConfigParams { Networking = new BetaLimitedNetworkParams() },
                Betas = [LiveCommentary.Beta]
            }, deadline.Token);
            await Save("CLAUDE_MANAGED_ENVIRONMENT_ID", environment.ID);
            Console.WriteLine("Environment ID saved to .env.");
        }
        if (!LocalConfiguration.Valid(configuration["CLAUDE_MANAGED_AGENT_ID"]))
        {
            var agent = await client.Beta.Agents.Create(new()
            {
                Name = "C12 text-only student agent",
                Model = new Anthropic.Models.Beta.Agents.Model((Anthropic.Core.ApiEnum<string, Anthropic.Models.Beta.Agents.BetaManagedAgentsModel>)(
                    string.IsNullOrWhiteSpace(configuration["CLAUDE_MODEL"]) ? "claude-opus-5-5" : configuration["CLAUDE_MODEL"]!)),
                System = LiveCommentary.Instructions, Tools = [], McpServers = [], Skills = [], Betas = [LiveCommentary.Beta]
            }, deadline.Token);
            await Save("CLAUDE_MANAGED_AGENT_ID", agent.ID);
            Console.WriteLine("Agent ID saved to .env.");
        }
        Console.WriteLine("Prerequisite IDs are saved. Existing configured IDs were reused. Restart the application.");
    }
}
