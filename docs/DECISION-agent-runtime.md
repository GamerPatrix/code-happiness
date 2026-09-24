# Decision record: use the GitHub Copilot SDK as the agent runtime?

**Question you asked:** *"can't we use VS Code's GitHub Copilot tooling somehow? or the whole Copilot?"*

**Short answer: yes — and it's better than my original plan assumed.** But "Copilot" means three
different things, and only one of them is shippable. Untangle them first.

## The three things you could mean

| # | What | Verdict |
|---|---|---|
| 1 | **VS Code + the Copilot extension** (the IDE I'm running in right now) | Great for **building** the project. **Not shippable** — you can't ask a user to install VS Code to get their desktop companion. |
| 2 | **GitHub Copilot SDK / Copilot CLI** (`github/copilot-sdk`) | ✅ **This is the real option.** A shippable, embeddable agent runtime. |
| 3 | **ACP — Agent Client Protocol** (`agentclientprotocol.com`) | ✅ **Strategic option.** Makes the avatar a client of *any* agent, not just yours. |

## Option 2 — the Copilot SDK. What I verified (2026-09-23)

Repo `github/copilot-sdk`, **10,510★**, updated **today**. Their words: *"exposes the same engine
behind Copilot CLI: a production-tested agent runtime you can invoke programmatically. No need to
build your own orchestration—you define agent behavior, Copilot handles planning, tool invocation,
file edits, and more."*

**There is an official .NET SDK** — `dotnet add package GitHub.Copilot.SDK` (NuGet, .NET Standard 2.0+).
Also Python, TypeScript, Go, Java, Rust. So it fits the .NET agent host in my plan directly.

Things it gives you that my §4 had me hand-build:

| Need in my plan | Copilot SDK feature |
|---|---|
| §4.2 tool protocol / MCP | `McpServers` config built-in. Runtime tool naming `<server-key>-<tool-name>`. |
| §5 **human confirmation gate** | ⭐ `OnPermissionRequest` callback. Note `PermissionHandler.ApproveAll` is *"only valid when managed settings are disabled"* — i.e. **the permission gate is a first-class hook, and deny-by-default is the default.** This is a far better answer than the other plan's "please ask me first" prompt. |
| §4.4 memory + history | `Memory = new MemoryConfiguration { Enabled = true }`, `EnableSessionStore` (cross-session search/retrieval), `ResumeSessionAsync()`. |
| 250k context ceiling | **Infinite sessions** — background compaction at 80 %, hard-block at 95 %. Solves the long-running-companion context problem. |
| §4.5 kill switch | `session.AbortAsync()`. |
| §3.4 streaming tokens | `Streaming = true` + `DeltaContent` events. |
| §1 "phone is a remote client" | ⭐ `RuntimeConnection.ForTcp(port, connectionToken)` / `.ForUri(url, token)` — **one runtime, multiple clients, token-authenticated.** That is almost exactly my AgentHost hub. |
| §4.3 custom PC tools | `Tools = [ CopilotTool.DefineTool(...) ]` — your C# handlers get invoked by the runtime. `OverridesBuiltInTool` to replace `read_file`/`edit_file` with allowlisted versions. |
| safe default | `CopilotClientMode.Empty` → **no built-in tools at all**, only the ones you register. This is the correct posture for a desktop companion. |
| sandboxing | `WorkingDirectory`, `BaseDirectory`/`COPILOT_HOME` for restricted/writable-limited environments. |

### The three catches

1. **It's a *coding* agent runtime.** Its built-in tools are `read_file`, `edit_file`, shell, browser —
   aimed at a repository. Your companion needs *screen/UI-Automation/notifications/media/reminders*.
   Those are yours to write either way. **The SDK saves you the orchestration loop, not the product.**
2. **Model/auth.** GitHub-hosted models (`"gpt-5"`, `"claude-sonnet-4.5"`) imply a Copilot subscription.
   `SessionConfig.Provider` is documented as *"Custom API provider configuration (BYOK)"* — which is
   the field that would let you point it at `http://192.168.1.5:8000/v1`.
   ⚠ **I could not read the exact `Provider` type fields, so treat BYOK-to-your-box as UNVERIFIED.**
   See the spike below — it's the one thing that decides this.
3. **Node dependency.** The .NET package downloads a pinned `copilot-runtime` + `runtime.node` pair.
   **Node.js is not installed on this machine** (I checked — `node` is missing). Not fatal, but it's a
   real dependency your build machine and any user machine needs.

## Option 3 — ACP. The more interesting long game

`agentclientprotocol.com`: standardises editor↔agent comms, *"suitable for both local and remote
scenarios"* — local agents as subprocesses over JSON-RPC/stdio, **remote agents over HTTP or WebSocket**.
It *"re-uses the JSON representations used in MCP where possible"* and adds custom types for agentic
UX like **diffs**. There's a public agent registry, and existing clients (`vscode-acp` 386★,
`acp-ui` 482★ — a cross-platform desktop/mobile/web client).

**Why you should care:** ACP is the answer to *"what is my avatar actually talking to?"* If the avatar
speaks ACP, then Claude Code, Codex, Copilot, Gemini CLI and OpenCode all become swappable brains —
which is exactly what `vscode-acp` already proves. Note their own caveat: *"Full support for remote
agents is a work in progress."*

## Recommendation

**Adopt the Copilot SDK as the default runtime, but keep the seam.** Do not delete `ILLMProvider` /
your own loop — you need something to fall back to if BYOK-to-your-box doesn't work, and you need a
path that isn't GitHub-dependent.

```
AgentHost
  └─ IAgentRuntime            ← the seam that matters
       ├─ CopilotSdkRuntime   ← default: planning, tools, MCP, permissions, memory, compaction
       └─ DirectLlmRuntime    ← fallback: your own loop over the OpenAI-compatible endpoint
```

This is a **strict improvement** on my original plan: the seam I already wanted (`ILLMProvider`) gets
promoted one level up to `IAgentRuntime`, and you get ~2 weeks of orchestration code for free.

## The spike that decides it (≤ 1 hour, do before M2)

1. Install Node.js (winget), then `dotnet add package GitHub.Copilot.SDK`.
2. Run the bundled `dotnet/samples/Chat.cs`.
3. **Try `SessionConfig.Provider` pointed at `http://192.168.1.5:8000/v1` with your `qwen3.8-flash-next`.**
   - ✅ works → Copilot SDK is your runtime. Free local brain, real agent loop.
   - ❌ GitHub-only → still usable, but you're paying a Copilot subscription and routing your file
     tree through GitHub. Then `DirectLlmRuntime` becomes the default and the SDK becomes optional.
4. Confirm `CopilotClientMode.Empty` + `OnPermissionRequest` actually blocks an attempted shell call.
   **If that doesn't hold, the safety model in §5 must be rebuilt in your own tool layer.**

## Also worth stealing

`microsoft/conductor` (456★) — CLI for multi-agent workflows on the Copilot SDK. And
`microsoft/Build26-BRK206-...your-agent-anywhere-multiclient-multidevice...` — a Build 2026 talk repo
on *"embed an agent in an app and access it across phones, machines, and cloud"*, which is literally
your PC-plus-phone requirement. Read it before writing the transport layer.
