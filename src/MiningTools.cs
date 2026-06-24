using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Extensions.Mcp;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LongRunningMcp;

/// <summary>
/// Two MCP tools that implement the "budgeted single call + poll fallback" pattern for
/// long-running work, backed by a Durable Functions orchestration (a proof-of-work miner).
///
/// This is a WORKAROUND until the MCP Task extension (SEP-2663) is supported by the Functions
/// MCP trigger. Once tasks are native, the server can return a task handle and the client polls
/// tasks/get via the SDK, making this two-tool pattern unnecessary.
/// </summary>
public class MiningTools
{
    private readonly ILogger<MiningTools> _logger;
    private readonly int _waitBudgetSeconds;
    private readonly int _defaultDifficulty;

    public MiningTools(ILogger<MiningTools> logger, IConfiguration config)
    {
        _logger = logger;

        // The wait budget is bounded by the *client* tool-call timeout (non-standard, often ~30-60s),
        // not the Functions host timeout. Default conservatively to stay under aggressive clients.
        _waitBudgetSeconds = config.GetValue("WaitBudgetSeconds", 20);

        // Default mining difficulty (leading zero bits). Higher = longer. Callers can override it
        // per request via the tool's optional "difficulty" argument.
        _defaultDifficulty = config.GetValue("MiningDifficulty", 24);
    }

    /// <summary>
    /// Starts the mining orchestration and awaits it up to a short budget.
    /// Quick jobs return their result inline (the poll tool is never needed);
    /// long jobs return a workflow_id handle and an instruction to poll.
    /// </summary>
    [Function(nameof(StartMining))]
    public async Task<string> StartMining(
        [McpToolTrigger("start_mining",
            "Mines a short chain of proof-of-work blocks. Returns the result directly if it finishes "
            + "quickly; otherwise returns a workflow_id to poll with get_mining_result. Higher "
            + "difficulty takes longer.")]
            ToolInvocationContext context,
        [McpToolProperty("difficulty",
            "Optional mining difficulty (leading zero bits). Higher = longer. Omit to use the default.")]
            string? difficulty,
        [DurableClient] DurableTaskClient durableClient)
    {
        // The argument is optional; when omitted (or not a positive int) fall back to the default.
        int effectiveDifficulty = int.TryParse(difficulty, out int d) && d > 0 ? d : _defaultDifficulty;

        // The MCP binding returns a dashed GUID to get_mining_result, so we create the instance
        // with a dashed-GUID id (ToString's "D" format) so the ids match on lookup.
        string instanceId = Guid.NewGuid().ToString();
        await durableClient.ScheduleNewOrchestrationInstanceAsync(
            nameof(MiningOrchestrator.RunOrchestrator), effectiveDifficulty,
            new StartOrchestrationOptions(instanceId));

        _logger.LogInformation("Started mining orchestration {InstanceId} at difficulty {Difficulty}",
            instanceId, effectiveDifficulty);

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
                "Mining {InstanceId} exceeded {Budget}s budget; returning poll handle.",
                instanceId, _waitBudgetSeconds);

            return Serialize(new MiningResult(
                Status: "running",
                WorkflowId: instanceId,
                PollAfterSeconds: 5,
                Next: $"Call get_mining_result with workflow_id \"{instanceId}\" in about 5 seconds."));
        }
    }

    /// <summary>
    /// Polls a previously started mining workflow by its workflow_id.
    /// </summary>
    [Function(nameof(GetMiningResult))]
    public async Task<string> GetMiningResult(
        [McpToolTrigger("get_mining_result",
            "Gets the status/result of a mining workflow started by start_mining. "
            + "If status is 'running', wait poll_after_seconds and call again.")]
            ToolInvocationContext context,
        // workflow_id is REQUIRED -> the schema-level dependency that forces start_mining to have
        // been called first. This makes tool ordering robust without relying on the agent.
        [McpToolProperty("workflow_id", "The workflow_id returned by start_mining.", isRequired: true)]
            string workflowId,
        [DurableClient] DurableTaskClient durableClient)
    {
        OrchestrationMetadata? metadata =
            await durableClient.GetInstancesAsync(workflowId, getInputsAndOutputs: true);

        if (metadata is null)
        {
            // Distinct from "failed": the work didn't error, the handle is unknown (bad id, or the
            // instance history was purged after its retention window). The agent's right move is to
            // start a fresh workflow, not to keep polling -- so it gets its own status.
            return Serialize(new MiningResult(
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
    /// stop and likely start over). Failed and Terminated both drive the same action, so they share
    /// status "failed". To avoid losing information, the precise terminal state is preserved in
    /// "reason", and a human-readable detail in "error". "running" means ONLY the non-terminal
    /// states, so the agent never polls a workflow that is already finished.
    /// </summary>
    private static MiningResult ToResult(OrchestrationMetadata metadata) =>
        metadata.RuntimeStatus switch
        {
            OrchestrationRuntimeStatus.Completed => new MiningResult(
                "completed", metadata.InstanceId, Result: metadata.ReadOutputAs<string>()),

            OrchestrationRuntimeStatus.Failed
            or OrchestrationRuntimeStatus.Terminated => new MiningResult(
                "failed", metadata.InstanceId,
                Reason: metadata.RuntimeStatus == OrchestrationRuntimeStatus.Failed ? "error" : "terminated",
                Error: DescribeFailure(metadata)),

            // Running / Pending / Suspended / ContinuedAsNew -> still in flight.
            _ => new MiningResult(
                Status: "running",
                WorkflowId: metadata.InstanceId,
                PollAfterSeconds: 5,
                Next: $"Call get_mining_result with workflow_id \"{metadata.InstanceId}\" in about 5 seconds.")
        };

    /// <summary>
    /// Pulls a human-readable detail for a failed/terminated workflow. The detail lives in a
    /// different place depending on how it ended, and neither is guaranteed to be populated:
    ///   - Failed     -> FailureDetails.ErrorMessage
    ///   - Terminated -> the terminate reason, stored as the instance output
    /// Each branch falls back to a generic message so we never assume a property is set.
    /// </summary>
    private static string DescribeFailure(OrchestrationMetadata metadata) =>
        metadata.RuntimeStatus == OrchestrationRuntimeStatus.Failed
            ? metadata.FailureDetails?.ErrorMessage ?? "Orchestration failed."
            : SafeOutput(metadata) ?? "Orchestration was terminated.";

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

    private static string Serialize(MiningResult result) => JsonSerializer.Serialize(result, JsonOpts);
}

/// <summary>
/// The closed contract returned to the MCP client. Null fields are omitted from the JSON.
/// "status" drives agent behavior; "reason" preserves the precise terminal cause when status is
/// "failed" (error | terminated) so no information is lost.
/// </summary>
public record MiningResult(
    string Status,
    string WorkflowId,
    string? Result = null,
    string? Reason = null,
    string? Error = null,
    int? PollAfterSeconds = null,
    string? Next = null);
