using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ResearchMcp;

/// <summary>
/// Two MCP tools that implement the "budgeted single call + poll fallback" pattern for
/// long-running work, backed by a Durable Functions orchestration.
///
/// This is a WORKAROUND until the MCP Task extension (SEP-2663) is supported by the Functions
/// MCP trigger. Once tasks are native, the server can return a task handle and the client polls
/// tasks/get via the SDK, making this two-tool pattern unnecessary.
/// </summary>
public class ResearchTools
{
    private readonly ILogger<ResearchTools> _logger;
    private readonly int _waitBudgetSeconds;

    public ResearchTools(ILogger<ResearchTools> logger, IConfiguration config)
    {
        _logger = logger;

        // The wait budget is configurable, NOT a hard-coded constant. The right value is bounded by
        // the *client* tool-call timeout (non-standard, often ~30-60s), not the Functions host
        // timeout. Default conservatively to stay under aggressive clients.
        _waitBudgetSeconds = config.GetValue("ResearchWaitBudgetSeconds", 20);
    }

    /// <summary>
    /// Starts the research orchestration and awaits it up to a short budget.
    /// Fast workflows return their result inline (the poll tool is never needed);
    /// slow workflows return a workflow_id handle and an instruction to poll.
    /// </summary>
    [Function(nameof(StartResearch))]
    public async Task<string> StartResearch(
        [McpToolTrigger("start_research",
            "Researches a topic by gathering information from multiple sources in parallel. "
            + "Returns the result directly if it finishes quickly; otherwise returns a workflow_id "
            + "to poll with get_research_result.")]
            ToolInvocationContext context,
        [McpToolProperty("topic", "The subject to research.", isRequired: true)] string topic,
        [DurableClient] DurableTaskClient durableClient)
    {
        // Use an explicit, dashed-GUID instance id. The MCP tool-argument binding canonicalizes
        // GUID-shaped strings to dashed "D" format on the way back into get_research_result, so the
        // id we hand out must already be in that exact form -- otherwise the poll lookup (an exact
        // string match in Durable) would miss. Durable's *default* instance id is the dash-less "N"
        // format, which would not round-trip. See README "A gotcha worth knowing".
        string instanceId = Guid.NewGuid().ToString(); // dashed "D" format
        await durableClient.ScheduleNewOrchestrationInstanceAsync(
            nameof(ResearchOrchestrator.RunOrchestrator), topic,
            new StartOrchestrationOptions(instanceId));

        _logger.LogInformation("Started research orchestration {InstanceId} for topic '{Topic}'",
            instanceId, topic);

        // Budgeted wait: WaitForInstanceCompletionAsync blocks until the orchestration reaches a
        // terminal state OR the CancellationToken fires. We impose the budget via CancelAfter.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_waitBudgetSeconds));
        try
        {
            OrchestrationMetadata metadata = await durableClient.WaitForInstanceCompletionAsync(
                instanceId, getInputsAndOutputs: true, cts.Token);

            // Finished within budget -> return the terminal result (completed or failed) inline.
            return Serialize(ToResult(metadata));
        }
        catch (OperationCanceledException)
        {
            // Budget expired. The orchestration is still running in Durable storage and is NOT
            // lost. Hand back a poll handle plus explicit next-step guidance for the agent.
            _logger.LogInformation(
                "Research {InstanceId} exceeded {Budget}s budget; returning poll handle.",
                instanceId, _waitBudgetSeconds);

            return Serialize(new ResearchResult(
                Status: "running",
                WorkflowId: instanceId,
                PollAfterSeconds: 5,
                Next: $"Call get_research_result with workflow_id \"{instanceId}\" in about 5 seconds."));
        }
    }

    /// <summary>
    /// Polls a previously started research workflow by its workflow_id.
    /// </summary>
    [Function(nameof(GetResearchResult))]
    public async Task<string> GetResearchResult(
        [McpToolTrigger("get_research_result",
            "Gets the status/result of a research workflow started by start_research. "
            + "If status is 'running', wait poll_after_seconds and call again.")]
            ToolInvocationContext context,
        // workflow_id is REQUIRED -> the schema-level dependency that forces start_research to have
        // been called first. This makes tool ordering robust without relying on the agent.
        [McpToolProperty("workflow_id", "The workflow_id returned by start_research.", isRequired: true)]
            string workflowId,
        [DurableClient] DurableTaskClient durableClient)
    {
        OrchestrationMetadata? metadata =
            await durableClient.GetInstancesAsync(workflowId, getInputsAndOutputs: true);

        if (metadata is null)
        {
            // Distinct from "failed": the work didn't error, the handle is unknown (bad id, or the
            // instance history was purged after its retention window). The agent's right move is to
            // start a fresh orchestration, not to keep polling -- so it gets its own status.
            return Serialize(new ResearchResult(
                Status: "not_found",
                WorkflowId: workflowId,
                Error: $"No workflow found with id \"{workflowId}\"."));
        }

        // Identical status mapping as the budgeted-wait path. A workflow that fails AFTER the
        // budget (i.e. during polling) is reported exactly like one that fails during the wait.
        return Serialize(ToResult(metadata));
    }

    /// <summary>
    /// The single source of truth for mapping a Durable runtime status to the closed
    /// { completed | failed | running } contract, shared by the wait path and the poll path.
    ///
    /// "status" is a BEHAVIORAL signal: it tells the agent what to do (use the result / poll again /
    /// stop and likely start over). Failed, Terminated, and Canceled all drive the same action, so
    /// they share status "failed". To avoid losing information, the precise terminal state is
    /// preserved in "reason", and a human-readable detail in "error". "running" means ONLY the
    /// non-terminal states, so the agent never polls a workflow that is already finished.
    /// </summary>
    private static ResearchResult ToResult(OrchestrationMetadata metadata) =>
        metadata.RuntimeStatus switch
        {
            OrchestrationRuntimeStatus.Completed => new ResearchResult(
                "completed", metadata.InstanceId, Result: metadata.ReadOutputAs<string>()),

            OrchestrationRuntimeStatus.Failed
            or OrchestrationRuntimeStatus.Terminated
#pragma warning disable CS0618 // Canceled exists for compatibility; included for defensive completeness.
            or OrchestrationRuntimeStatus.Canceled => new ResearchResult(
                "failed", metadata.InstanceId,
                Reason: metadata.RuntimeStatus switch
                {
                    OrchestrationRuntimeStatus.Failed => "error",
                    OrchestrationRuntimeStatus.Terminated => "terminated",
                    _ => "canceled"
                },
                Error: DescribeFailure(metadata)),
#pragma warning restore CS0618

            // Running / Pending / Suspended / ContinuedAsNew -> still in flight.
            _ => new ResearchResult(
                Status: "running",
                WorkflowId: metadata.InstanceId,
                PollAfterSeconds: 5,
                Next: $"Call get_research_result with workflow_id \"{metadata.InstanceId}\" in about 5 seconds.")
        };

    /// <summary>
    /// Pulls a human-readable detail per terminal state. The reason lives in a DIFFERENT place
    /// depending on how the orchestration ended, and none is guaranteed to be populated:
    ///   - Failed     -> FailureDetails.ErrorMessage
    ///   - Terminated -> the terminate reason is stored as the instance output
    ///   - Canceled   -> usually no detail
    /// Each branch falls back to a generic message so we never assume a property is set.
    /// </summary>
    private static string DescribeFailure(OrchestrationMetadata metadata) =>
#pragma warning disable CS0618 // Canceled exists for compatibility; included for defensive completeness.
        metadata.RuntimeStatus switch
        {
            OrchestrationRuntimeStatus.Failed =>
                metadata.FailureDetails?.ErrorMessage ?? "Orchestration failed.",
            OrchestrationRuntimeStatus.Terminated =>
                SafeOutput(metadata) ?? "Orchestration was terminated.",
            OrchestrationRuntimeStatus.Canceled =>
                SafeOutput(metadata) ?? "Orchestration was canceled.",
            _ => metadata.RuntimeStatus.ToString()
        };
#pragma warning restore CS0618

    private static string? SafeOutput(OrchestrationMetadata metadata)
    {
        if (string.IsNullOrEmpty(metadata.SerializedOutput))
            return null;
        try { return metadata.ReadOutputAs<string>(); }
        catch { return metadata.SerializedOutput; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string Serialize(ResearchResult result) => JsonSerializer.Serialize(result, JsonOpts);
}

/// <summary>
/// The closed contract returned to the MCP client. Null fields are omitted from the JSON.
/// "status" drives agent behavior; "reason" preserves the precise terminal cause when status is
/// "failed" (error | terminated | canceled) so no information is lost.
/// </summary>
public record ResearchResult(
    string Status,
    string WorkflowId,
    string? Result = null,
    string? Reason = null,
    string? Error = null,
    int? PollAfterSeconds = null,
    string? Next = null);
