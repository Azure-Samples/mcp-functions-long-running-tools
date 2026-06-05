using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace ResearchMcp;

/// <summary>
/// The Durable orchestration that does the actual long-running work.
///
/// This is fan-out/fan-in -- the one thing a single stateless MCP tool function can't do well.
/// It dispatches several "data source" activities IN PARALLEL (Task.WhenAll) and aggregates the
/// results. Each source is slow on its own; sequentially they'd blow the client timeout, but the
/// orchestration runs them concurrently with Durable's retry/reliability on top.
///
/// In a real sample each activity would use a binding/integration that MCP authors underuse, e.g.
/// Azure AI Search, Cosmos DB, Blob storage, a web search API, or Azure OpenAI for summarization.
/// Here they are simulated with delays so the sample runs without external dependencies.
///
/// Tip: set the app setting/env var ResearchSourceDelaySeconds to control how long each source
/// takes. Small values (default 3) finish within the wait budget and return inline; large values
/// (e.g. 30) exceed the budget so you can exercise the poll path.
/// </summary>
public static class ResearchOrchestrator
{
    private const int DefaultSourceDelaySeconds = 3;

    [Function(nameof(RunOrchestrator))]
    public static async Task<string> RunOrchestrator(
        [OrchestrationTrigger] TaskOrchestrationContext context,
        string topic)
    {
        ILogger logger = context.CreateReplaySafeLogger(nameof(ResearchOrchestrator));
        logger.LogInformation("Orchestrating research for '{Topic}'", topic);

        // Fan out: kick off every source in parallel.
        var tasks = new List<Task<string>>
        {
            context.CallActivityAsync<string>(nameof(SearchInternalDocs), topic),
            context.CallActivityAsync<string>(nameof(SearchWeb), topic),
            context.CallActivityAsync<string>(nameof(LookupFinancials), topic),
            context.CallActivityAsync<string>(nameof(LookupCrmHistory), topic),
        };

        // Fan in: wait for all of them.
        string[] findings = await Task.WhenAll(tasks);

        // Aggregate (in a real sample, summarize via Azure OpenAI here).
        var report = new StringBuilder();
        report.AppendLine($"# Research report: {topic}");
        report.AppendLine();
        foreach (string finding in findings)
        {
            report.AppendLine($"- {finding}");
        }

        return report.ToString();
    }

    [Function(nameof(SearchInternalDocs))]
    public static async Task<string> SearchInternalDocs([ActivityTrigger] string topic)
    {
        await Task.Delay(SourceDelay()); // simulate Azure AI Search
        return $"Internal docs: 4 documents referencing '{topic}'.";
    }

    [Function(nameof(SearchWeb))]
    public static async Task<string> SearchWeb([ActivityTrigger] string topic)
    {
        await Task.Delay(SourceDelay()); // simulate web search API
        return $"Web: recent news and articles about '{topic}'.";
    }

    [Function(nameof(LookupFinancials))]
    public static async Task<string> LookupFinancials([ActivityTrigger] string topic)
    {
        await Task.Delay(SourceDelay()); // simulate financial data API
        return $"Financials: latest figures related to '{topic}'.";
    }

    [Function(nameof(LookupCrmHistory))]
    public static async Task<string> LookupCrmHistory([ActivityTrigger] string topic)
    {
        await Task.Delay(SourceDelay()); // simulate Cosmos DB CRM lookup
        return $"CRM: existing relationship history for '{topic}'.";
    }

    private static TimeSpan SourceDelay()
    {
        string? raw = Environment.GetEnvironmentVariable("ResearchSourceDelaySeconds");
        int seconds = int.TryParse(raw, out int parsed) ? parsed : DefaultSourceDelaySeconds;
        return TimeSpan.FromSeconds(seconds);
    }
}
