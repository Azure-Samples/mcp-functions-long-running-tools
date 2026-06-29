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
isolated) that shows how to run **long-running MCP tools** (tool calls that take longer than an MCP
client is willing to wait) by backing them with [Durable Functions](https://learn.microsoft.com/azure/azure-functions/durable/durable-functions-overview)
and a **budgeted start + poll** pattern. Durable Functions lets you write stateful, long-running workflows as ordinary code, orchestrating multiple function calls while the platform handles checkpointing, scaling, and recovery.

> **Status: workaround.** This pattern is a pragmatic bridge until the MCP **Task extension**
> ([SEP-2663](https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2663)) is supported
> by the Azure Functions MCP trigger. Once tasks are native and supported in the Functions extension, the protocol handles async itself (the
> server returns `resultType: "task"` and the client polls `tasks/get` via the SDK), and this
> two-tool pattern becomes unnecessary.

## The problem

An MCP `tools/call` is request/response. If a tool kicks off work that takes minutes, the **client's
request timeout** fires long before the work finishes, and the agent sees a failed tool call, even
though the work may still be running. Client tool-call timeouts are **not standardized** by the MCP
spec; in practice they're often in the ~30–60s range and vary per client. So a single tool call must
not block for the full duration of a long workflow.

## The approach: budgeted single call + poll fallback

Two MCP tools are exposed:

1. **`start_mining`** - starts a Durable orchestration (which mines a short chain of proof-of-work blocks), then **awaits completion up to a short budget** (~20s, configurable).
   - If the workflow finishes **within budget**, the **result is returned inline**. The second tool is never needed. This is the common case and it removes any "did the agent remember to poll?" risk.
   - If the budget expires, a **handle** (`workflow_id`) is returned plus an explicit instruction to poll. The orchestration keeps running in Durable storage regardless of the client connection.

2. **`get_mining_result`** - takes the `workflow_id` (a **required** parameter) and returns the current state: `completed` (with result), `failed` (with error), `running` (poll again), or `not_found` (unknown/expired id).

Ordering is made robust by design: `workflow_id` is a **required** parameter of the poll tool (so the agent can't poll without first starting), the "running" response carries `poll_after_seconds` and a `next` instruction, and the budgeted wait means fast workflows never hit the second tool at all.

> **Known weakness.** Even so, the poll path still relies on the **LLM correctly remembering, and
> not hallucinating, the `workflow_id`** it was handed. If the model garbles or invents an id, the
> poll lands on the wrong instance or none at all (which is why `get_mining_result` returns
> `not_found` rather than guessing). The budgeted wait mitigates this by resolving most calls without
> a second hop, but it's the core reason the MCP Task extension, where the SDK (not the model)
> carries the handle, is the better long-term answer.

## The example workflow: a blockchain-style miner

The long-running work in this sample is a small **miner**, the same idea behind blockchain
**proof-of-work**, but with nothing crypto to learn. Here's the mechanic in plain terms: to "mine" a
block, the system runs an input through a one-way math function (SHA-256) and checks whether the
result matches a required pattern, i.e. starting with at least `difficulty` zeros. There's no
shortcut, so the miner just keeps trying different inputs (`0, 1, 2, …`) until one happens to produce
a result that fits. Lots of trial and error, which naturally takes time — a good stand-in for any
real long-running job.

The miner builds a short *chain* of blocks, where each block's input includes the previous block's
answer. Because every step depends on the one before it, this is a natural example of Durable's
[**function-chaining pattern**](https://learn.microsoft.com/azure/durable-task/common/durable-task-sequence?tabs=csharp&pivots=durable-functions).

The `difficulty` knob controls the runtime: each extra required zero roughly doubles the expected
number of attempts, so higher difficulty takes longer. That single knob is what lets the sample
demonstrate both the quick *inline* path and the slow *poll* path.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local)
- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
  for deploying to Azure.
- [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) storage emulator
  (the local Durable Functions backend). Install with `npm install -g azurite`.
- [VS Code](https://code.visualstudio.com/) with [GitHub Copilot](https://code.visualstudio.com/docs/copilot/overview)
  (agent mode) to call the tools as an MCP client.

## Run it locally

**1. Start Azurite** (in its own terminal):

```bash
azurite --silent --location ./.azurite
```

> If the host logs an Azurite "API version ... is not supported" error, your Azurite is older than
> the storage API the Functions runtime requests. Upgrade Azurite (`npm install -g azurite@latest`),
> or start it with `--skipApiVersionCheck`.

**2. Start the Functions host** (from the `src` folder):

```bash
cd src
func start
```

> You'll see an advisory warning that `func start` runs against a .NET isolated project; it's safe to
> ignore here. The host still builds the project and loads the MCP and Durable extensions. In **VS
> Code**, you can instead press **F5** to start the host as well.

You should see both MCP tools register and the endpoint print:

```
MCP server endpoint: http://localhost:7071/runtime/webhooks/mcp

Functions:
    StartMining: mcpToolTrigger
    GetMiningResult: mcpToolTrigger
    RunOrchestrator: orchestrationTrigger
    ...
```

> The MCP endpoint uses the Streamable HTTP transport at
> `http://localhost:7071/runtime/webhooks/mcp`.

**3. Connect from VS Code.** [`.vscode/mcp.json`](.vscode/mcp.json) registers the local server.
Open the repo in VS Code, open `.vscode/mcp.json`, and click **Start** on the `local-mining-mcp`
server. Then, in a Copilot **agent mode** chat, ask it to mine, e.g.:

> Mine some blocks for me.

The agent calls `start_mining`. With the default difficulty, the work outlives the wait budget, so the
tool returns status `running` with a `workflow_id` and a `next` instruction. The agent then calls
`get_mining_result` with that id, returning `running` until mining completes, then `completed` with the
mined chain. This exercises the **poll path** out of the box (so it's demonstrated even if you deploy
without testing locally first).

### Try the inline path

Ask the agent to mine at a **lower difficulty** so the work finishes within the wait budget:

> Mine some blocks at difficulty 18.

Now `start_mining` finishes before the budget expires and the result comes back **inline** (status
`completed`), with no polling needed.

> Mining time varies by machine and build. If difficulty 18
> still outlasts the budget, lower it by 1–2; the default difficulty is tuned to reliably trigger
> polling.

## Deploy to Azure

The repo includes [`azure.yaml`](azure.yaml) and Bicep under [`infra/`](infra), so it deploys with the
[Azure Developer CLI](https://learn.microsoft.com/azure/developer/azure-developer-cli/). Run this from
the **repository root** (the folder containing `azure.yaml`):

```bash
azd up
```

`azd` prompts for an environment name, subscription, and region, as well as the **mining difficulty**
and **wait budget (seconds)** for the deployed app.

Durable Functions requires a storage backend to checkpoint execution progress. For local
testing, the app uses Azure Storage. When deployed to Azure, it uses the [Durable Task Scheduler (DTS)](https://learn.microsoft.com/azure/durable-task/scheduler/durable-task-scheduler), which is the recommended backend.

During packaging, `azd` hooks automatically swap in the DTS [`src/host.dts.json`](src/host.dts.json)
so the deployed app uses the Durable Task Scheduler backend, then restore the local Azure Storage
`host.json`. You don't edit any files to deploy.

When it finishes, the MCP endpoint is at
`https://<function-app>.azurewebsites.net/runtime/webhooks/mcp`. The endpoint is **key-protected**, so
clients must send the MCP extension's system key in the `x-functions-key` header. Retrieve it with:

```bash
az functionapp keys list -g <resource-group> -n <function-app> \
  --query "systemKeys.mcp_extension" -o tsv
```

Tear everything down with `azd down`.

## Configuration

| Setting | Default | Purpose |
|---------|---------|---------|
| `WaitBudgetSeconds` | `20` | How long `start_mining` blocks waiting for the workflow before returning a poll handle. Keep it **under the client's tool-call timeout**, not the Functions timeout. |
| `MiningDifficulty` | `24` | Default mining difficulty (leading zero bits) when the tool's `difficulty` argument is omitted. Tuned so the default run outlasts `WaitBudgetSeconds` and exercises the poll path. Higher = longer. |

Both are read from app settings / environment. **Locally**, set them in
[`src/local.settings.json`](src/local.settings.json) (defaults `24` / `20`). **In Azure**, `azd up`
prompts for them and sets them as Function App settings (see [`infra/main.bicep`](infra/main.bicep)).
The `difficulty` tool argument overrides `MiningDifficulty` per request.

## How the result is shaped

`status` is a **behavioral** signal that tells the agent what to do next; descriptive detail lives in
sibling fields so nothing is lost:

| `status` | Meaning | Agent's next move |
|----------|---------|-------------------|
| `completed` | Done; `result` holds the mined chain. | Use the result. |
| `running` | Still in flight (budget expired). | Wait `poll_after_seconds`, call `get_mining_result`. |
| `failed` | Terminal: errored or terminated. `reason` + `error` give detail. | Stop polling; surface the error; optionally start over. |
| `not_found` | No workflow for that id (bad/expired id). | Don't poll; start a new workflow. |

`Failed` and `Terminated` Durable states both map to `status: "failed"` because they drive the
**same** agent action; the precise cause is preserved in `reason` (`error` / `terminated`).

## Durable backend: Azure Storage locally, DTS in Azure

This sample uses two Durable Functions backends so local development stays lightweight while Azure
gets a managed, scalable backend:

- **Locally**, it uses the **Azure Storage** backend, served by the [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite)
  emulator. This is the default in [`src/host.json`](src/host.json), so all you need locally is Azurite.
- **In Azure**, it uses the [**Durable Task Scheduler (DTS)**](https://learn.microsoft.com/azure/durable-task/scheduler/durable-task-scheduler),
  a managed backend that `azd up` provisions and connects via managed identity.

The DTS configuration lives in [`src/host.dts.json`](src/host.dts.json). You don't switch backends by
hand: `azd` package [hooks](azure.yaml) swap `host.dts.json` in for the deployment package and restore
the local `host.json` afterward, so your working tree always runs on Azure Storage. The orchestration
and MCP tool code are identical for both backends.

## Q&A

**Q: Will agents call `start` then `poll` in the right order?**
Make `workflow_id` a **required** parameter of the poll tool (the schema enforces ordering), put
`next`/`poll_after_seconds` instructions **in the result payload**, and make the poll tool
**self-correcting** via its `status` field. The budgeted wait then removes the second tool entirely
for fast workflows.

**Q: Why is the wait budget ~20s, and what bounds it?**
The **client** tool-call timeout, not the Functions host timeout. The host timeout on Flex/Premium is
generous (minutes), but the client may give up in ~30s, and that's non-standard and varies per
client. So default the budget **conservatively** (~20s, as an app setting) to stay under the most
aggressive clients, and rely on the poll fallback for anything longer. `notifications/progress` could
extend the window on clients that honor it, but it's optional and client-dependent, so it's left out
here for clarity.

**Q: While a long-running task is in flight, can the agent (or other agents) still call other tools?**
It's the **agent's turn that's tied up, not the server**. Any synchronous `tools/call` blocks the
agent until it returns; that's true for every tool, long or short. The long-running case just (a) can
consume the whole per-call budget before returning, and (b) puts the agent into a **poll loop**
(wait `poll_after_seconds`, call `get_mining_result`, repeat), which in practice is a dedicated loop
that monopolizes the agent's attention, so it *feels* blocking end to end. (The agent isn't strictly
*forced* to poll back to back; `poll_after_seconds` is a hint, and a capable client could interleave
other work between polls, but most agents serialize.) The **server is never blocked**: the budgeted
wait is async (it doesn't hold a thread), the orchestration runs in the background keyed by
`workflow_id`, and the Functions host serves requests concurrently and scales out. So a **different
agent on its own session can call the server's tools at the same time**, and many long-running
workflows can run in parallel. This is the limitation the native MCP Task extension removes: the SDK
polls out of band, freeing the model from the loop.

## Contributing

This project welcomes contributions and suggestions. See the standard
[Microsoft CLA / Open Source Code of Conduct](https://opensource.microsoft.com/codeofconduct/) notes.

## License

[MIT](LICENSE)
