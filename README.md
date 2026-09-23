# AiCompanion

A 3D desktop companion — an animated FBX character that lives on your desktop (and your phone),
speaks out loud, and is driven by a real agent that can operate the PC and the internet.

**Status:** planning. Start with [docs/AI-COMPANION-PLAN.md](./docs/AI-COMPANION-PLAN.md).

## Architecture in one line

Three binaries, one codebase: a headless **.NET agent host** (the brain), a **Unity Windows overlay**
(the body, transparent + click-through over other apps), and a **Unity Android thin client** (mic + avatar).
All three share one Unity project and one protocol. The LLM is an OpenAI-compatible endpoint.

```
Android client ─┐
                ├─► AgentHost (.NET) ─► LLM endpoint (OpenAI-compatible, tool-calling)
Unity overlay ──┘        │
                  └────► Voice sidecar (STT + TTS + visemes)
```

## Layout

| Path | What |
|---|---|
| `docs/` | Plan, decisions, and a review of an alternative proposed approach |
| `src/` | .NET agent host, tools, voice providers, shared contracts |
| `client/` | The single Unity project (Windows Standalone + Android build targets) |
| `tools/sidecars/` | STT / TTS sidecar services |

## Milestones

`M0` overlay spike → `M1` talking puppet → `M2` agent host → `M3` wire together + streaming TTS →
`M4` voice in → `M5` screen awareness → `M6` Android client → `M7` actuation + safety rails → `M8` polish.

**Do `M0` first.** If transparent click-through is a fight on your setup, the whole premise changes and
you want to know that on day one.

## Notes

- The Unity project is created at `M0` (via Unity Hub or the Unity CLI) — it is not in this repo yet.
- Anything that can actuate the machine (shell, clicks, writes) is gated behind an explicit user
  confirmation. See §5 of the plan before building those tools.
