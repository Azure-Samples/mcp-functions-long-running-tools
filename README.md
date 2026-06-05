<!--
---
name: Long-running MCP tools using Durable Functions (.NET/C#)
description: Run long-running MCP tools on Azure Functions by backing them with Durable Functions and a budgeted start/poll pattern.
page_type: sample
languages:
- csharp
products:
- azure-functions
- azure
urlFragment: mcp-functions-long-running-tools
---
-->

# Long-running MCP tools using Durable Functions

A sample [Azure Functions](https://learn.microsoft.com/azure/azure-functions/) MCP server (.NET
isolated) that shows how to run **long-running MCP tools** — tool calls that take longer than an MCP
client is willing to wait — by backing them with [Durable Functions](https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-overview)
and a **budgeted start + poll** pattern.

> **Status: workaround.** This pattern is a pragmatic bridge until the MCP **Task extension**
> ([SEP-2663](https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2663)) is supported
> by the Azure Functions MCP trigger. Once tasks are native, the protocol handles async itself (the
> server returns `resultType: "task"` and the client polls `tasks/get` via the SDK), and this
> two-tool pattern becomes unnecessary.

## The problem

An MCP `tools/call` is request/response. If a tool kicks off work that takes minutes, the **client's
request timeout** fires long before the work finishes, and the agent sees a failed tool call — even
though the work may still be running. Client tool-call timeouts are **not standardized** by the MCP
spec; in practice they're often in the ~30–60s range and vary per client. So a single tool call must
not block for the full duration of a long workflow.

## The approach: budgeted single call + poll fallback

Two MCP tools are exposed:

1. **`start_research`** — starts a Durable orchestration (which queries several data sources in
   sequence and aggregates), then **awaits completion up to a short budget** (~20s, configurable).
   - If the workflow finishes **within budget** → the **result is returned inline**. The second tool
     is never needed. This is the common case and it removes any "did the agent remember to poll?"
     risk.
   - If the budget expires → a **handle** (`workflow_id`) is returned plus an explicit instruction to
     poll. The orchestration keeps running in Durable storage regardless of the client connection.

2. **`get_research_result`** — takes the `workflow_id` (a **required** parameter) and returns the
   current state: `completed` (with result), `failed` (with error), `running` (poll again), or
   `not_found` (unknown/expired id).

Ordering is made robust by design: `workflow_id` is a **required** parameter of the poll tool (so the
agent can't poll without first starting), the "running" response carries `poll_after_seconds` and a
`next` instruction, and the budgeted wait means fast workflows never hit the second tool at all.

> **Known weakness.** Even so, the poll path still relies on the **LLM correctly remembering — and
> not hallucinating — the `workflow_id`** it was handed. If the model garbles or invents an id, the
> poll lands on the wrong instance or none at all (which is why `get_research_result` returns
> `not_found` rather than guessing). The budgeted wait mitigates this by resolving most calls without
> a second hop, but it's the core reason the MCP Task extension — where the SDK, not the model,
> carries the handle — is the better long-term answer.

## What's in the box

| Path | What it is |
|------|------------|
| [`src/ResearchTools.cs`](src/ResearchTools.cs) | The two MCP tools and the shared status-mapping helper. |
| [`src/ResearchOrchestrator.cs`](src/ResearchOrchestrator.cs) | Durable orchestration + simulated source activities. |
| [`src/Program.cs`](src/Program.cs) | Isolated-worker host setup. |
| [`src/host.json`](src/host.json) | Host config, including the MCP server name/instructions. |
| [`.vscode/mcp.json`](.vscode/mcp.json) | Registers the local MCP server for VS Code. |

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) storage emulator
  (Durable Functions needs blob/queue/table storage locally). Install with `npm install -g azurite`.
- [VS Code](https://code.visualstudio.com/) with [GitHub Copilot](https://code.visualstudio.com/docs/copilot/overview)
  (agent mode) to call the tools as an MCP client.

## Run it locally

**1. Start Azurite** (in its own terminal):

```bash
azurite --silent --location ./.azurite
```

**2. Start the Functions host** (from the `src` folder):

```bash
cd src
dotnet run
```

You should see both MCP tools register and the endpoint print:

```
MCP server endpoint: http://localhost:7071/runtime/webhooks/mcp

Functions:
    StartResearch: mcpToolTrigger
    GetResearchResult: mcpToolTrigger
    RunOrchestrator: orchestrationTrigger
    ...
```

> The MCP endpoint uses the Streamable HTTP transport at
> `http://localhost:7071/runtime/webhooks/mcp`.

**3. Connect from VS Code.** [`.vscode/mcp.json`](.vscode/mcp.json) registers the local server.
Open the repo in VS Code, open `.vscode/mcp.json`, and click **Start** on the `local-research-mcp`
server. Then, in a Copilot **agent mode** chat, ask it to research a topic, e.g.:

> Research "Contoso Ltd" for me.

The agent calls `start_research`. With the defaults, the workflow finishes within the wait budget and
the result comes back **inline** (status `completed`) — no polling needed.

### Try the poll path

Make the workflow outlive the wait budget by shrinking the budget and lengthening each source. Stop
the host (Ctrl+C) and restart it with:

```bash
cd src
ResearchWaitBudgetSeconds=2 ResearchSourceDelaySeconds=5 dotnet run
```

Now ask the agent to research a topic again. This time `start_research` hits its 2s budget before the
workflow finishes, so it returns status `running` with a `workflow_id` and a `next` instruction. The
agent then calls `get_research_result` with that id — returning `running` until the work completes,
then `completed` with the report.

## Configuration

| Setting | Default | Purpose |
|---------|---------|---------|
| `ResearchWaitBudgetSeconds` | `20` | How long `start_research` blocks waiting for the workflow before returning a poll handle. Keep it **under the client's tool-call timeout**, not the Functions timeout. |
| `ResearchSourceDelaySeconds` | `3` | Simulated per-source latency. The sources run sequentially, so the total workflow time is roughly this value times the number of sources. Raise it (or lower the budget) to demonstrate the poll path. |

Both are read from app settings / environment (see [`src/local.settings.json`](src/local.settings.json)).

## How the result is shaped

`status` is a **behavioral** signal that tells the agent what to do next; descriptive detail lives in
sibling fields so nothing is lost:

| `status` | Meaning | Agent's next move |
|----------|---------|-------------------|
| `completed` | Done; `result` holds the report. | Use the result. |
| `running` | Still in flight (budget expired). | Wait `poll_after_seconds`, call `get_research_result`. |
| `failed` | Terminal: errored or terminated. `reason` + `error` give detail. | Stop polling; surface the error; optionally start over. |
| `not_found` | No workflow for that id (bad/expired id). | Don't poll; start a new workflow. |

`Failed` and `Terminated` Durable states both map to `status: "failed"` because they drive the
**same** agent action; the precise cause is preserved in `reason` (`error` / `terminated`).

## Q&A

**Q: Will agents call `start` then `poll` in the right order?**
Make `workflow_id` a **required** parameter of the poll tool (the schema enforces ordering), put
`next`/`poll_after_seconds` instructions **in the result payload**, and make the poll tool
**self-correcting** via its `status` field. The budgeted wait then removes the second tool entirely
for fast workflows.

**Q: Why is the wait budget ~20s — what bounds it?**
The **client** tool-call timeout, not the Functions host timeout. The host timeout on Flex/Premium is
generous (minutes), but the client may give up in ~30s, and that's non-standard and varies per
client. So default the budget **conservatively** (~20s, as an app setting) to stay under the most
aggressive clients, and rely on the poll fallback for anything longer. `notifications/progress` could
extend the window on clients that honor it, but it's optional and client-dependent, so it's left out
here for clarity.

**Q: What happens if the orchestration *fails* — and why isn't `status` split into failed/terminated?**
`status` exists to **direct the agent's behavior**, not to mirror Durable's enum. `Failed` and
`Terminated` both lead to the same next action — stop polling, surface what happened, likely start
fresh — so they share `status: "failed"`. To avoid losing information, the precise terminal state is
preserved in a separate `reason` field plus a human-readable `error`. A terminated workflow isn't
really an "error", so it isn't *labeled* one at the headline.

## A gotcha worth knowing

The MCP tool-argument binding returns a **dashed GUID** to `get_research_result`. Durable's *default*
instance id is the dash-less form, so the default wouldn't match on lookup. This sample creates the
orchestration with `Guid.NewGuid().ToString()` (dashed) so the ids match. See the comment in
[`src/ResearchTools.cs`](src/ResearchTools.cs).

## Contributing

This project welcomes contributions and suggestions. See the standard
[Microsoft CLA / Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/) notes.

## License

[MIT](LICENSE)
