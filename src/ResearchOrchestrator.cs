using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;

namespace ResearchMcp;

/// <summary>
/// The Durable orchestration that does the actual long-running work.
///
/// It calls several "data source" activities in sequence and aggregates the results. Each source is
/// slow on its own, so the total easily exceeds a client's tool-call timeout -- which is exactly the
/// long-running case this sample addresses. Durable runs the steps reliably (with checkpointing and
/// retry support) regardless of how long they take.
///
/// In a real sample each activity would use a binding/integration that MCP authors underuse, e.g.
/// Azure AI Search, Cosmos DB, Blob storage, a web search API, or Azure OpenAI for summarization.
/// Here they are simulated with delays so the sample runs without external dependencies.
///
/// Tip: set the app setting/env var ResearchSourceDelaySeconds to control how long each source
/// takes. Because the steps run sequentially, the total is roughly the per-source delay times the
/// number of sources. Small values finish within the wait budget and return inline; large values
/// exceed the budget so you can exercise the poll path.
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

        // Query each source in sequence, collecting findings as we go.
        var findings = new List<string>
        {
            await context.CallActivityAsync<string>(nameof(SearchInternalDocs), topic),
            await context.CallActivityAsync<string>(nameof(SearchWeb), topic),
            await context.CallActivityAsync<string>(nameof(LookupFinancials), topic),
            await context.CallActivityAsync<string>(nameof(LookupCrmHistory), topic),
        };

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
