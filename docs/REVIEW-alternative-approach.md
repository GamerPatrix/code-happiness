# Review: the "Python backend + Unity frontend" plan (from another agent)

You asked me to take this lightly. I checked its concrete claims instead of eyeballing them.
**Verdict: directionally sound, and it got two things right that I should adopt — but it has three
errors that would cost you real time, and one security hole.**

## What it got right (adopt these)

| Claim | Verified | Note |
|---|---|---|
| **Decouple the brain from Unity** | ✅ | This is the same core call as my plan. It's the right one. |
| **`kirurobo/UniWindowController`** for transparent/click-through | ✅ **real and excellent** — 746★, MIT, updated 2026-09-21, UPM-installable, Win+macOS | **Adopt it.** This de-risks my `M0` spike substantially and I've added it to the plan. |
| **`hecomi/uLipSync`** for lip-sync | ✅ real — 1,671★, Burst/Job-System, VRM + WebGL topics, updated 2026-09-21 | Good pick. Caveat below. |
| **`rany2/edge-tts`** | ✅ real — 12,006★, updated **today** | Good *prototype* voice. Caveat below. |
| Send `{"action":"typing"}` cues → play an animation | ✅ | Exactly my `anim{cue}` message. Nice to see convergence. |
| Android = thin client, brain stays on PC | ✅ | Same as my architecture. |

## Errors that will cost you time

**1. Port 8000 is already taken — by your own AI box.**
It says *"The Python backend operates strictly on port 8000"* and *"Unity connects to
`ws://localhost:8000`"*. Your LLM endpoint is `http://192.168.1.5:8000/v1`. On the PC itself 8000 is
free, so it would *appear* to work — until you ever co-host the model locally, or run the sidecar on
the box, and you get a confusing bind collision. **Pick a distinct port (e.g. `8790`) and make it
config-driven, never hardcoded.** Also `localhost` breaks the Android client immediately — it must be
a configurable host from day one, which is what my `ITransport` does.

**2. Python isn't installed on this machine.**
`python --version` → *"Python was not found; run without arguments to install from the Microsoft
Store"*, and the `py` launcher is missing. So `python -m venv venv` / `pip install ...` fails on
step one. Also note its `source venv/bin/activate` is bash syntax — on Windows it's
`venv\Scripts\Activate.ps1`. And `sounddevice` needs PortAudio wheels; that's a classic Windows
install trap.
→ **This is the real fork in the two plans.** You already have **.NET SDK 10.0.401** installed and
zero Python. My plan says .NET; theirs says Python. Python *is* the better ecosystem for
whisper/Piper/ONNX, so the honest answer is **hybrid**: .NET for the agent host + tools (native
Windows APIs, UI-Automation, Win32 overlay interop, one language with Unity's C#), and **Python only
inside the voice sidecar** where its ML libraries are genuinely stronger. Don't write the tool layer
in Python — you'd be fighting Windows APIs from the wrong language.

**3. `edge-tts` is not offline, and it has no viseme data.**
It works by calling **Microsoft's online** Edge read-aloud service. So it (a) needs internet,
(b) sends your text to Microsoft, (c) is an **unofficial, unlicensed** use of an internal endpoint —
fine to prototype, fragile as a foundation, and a non-starter if you ever ship it. It also gives you
no phoneme timings, so lip-sync has to be inferred from the audio.
→ Use it for a **day-one demo** (it's genuinely great value: free, good voices). Architect behind
`ITTSProvider` so **Piper** (offline, phoneme timings → free lip-sync) is the real default.

## The security hole

Its tool list is `read_file`, `write_file`, `run_terminal_command`, `get_system_stats` — with
`run_terminal_command` gated only by *"a safety confirmation prompt or restricted directory scope"*.
A shell command has **no directory scope**; `run_terminal_command("rd /s C:\Users")` is exactly the
thing that needs a hard human gate, an allowlist, an append-only audit log, a timeout, and an output
cap. Its system-prompt line (*"Wait for my explicit approval before executing bash/PowerShell that
modifies system state"*) is a **politeness instruction to the model, not a security control** — a
model can be talked out of it, and prompt-injection from a web page it fetched can do it for you.
**The gate must live in the C#/.NET host outside the model's reach.** See §5 of the main plan.

## Two smaller corrections

- **`uLipSync` maps A/E/I/O/U, not visemes.** It's MFCC-based audio analysis. It's a fine *fallback*
  (and great for mic-driven VTuber use), but for TTS you already *have* phoneme timings from Piper —
  using them is more accurate and costs zero extra latency. Layering: **phoneme→viseme primary,
  uLipSync/Rhubarb secondary.**
- **AR Foundation passthrough for Android** is a cute idea but it's a different product (an AR app),
  with camera-permission and perf costs, and it doesn't match "phone is just a client that shows the
  avatar." Use a plain background (or a screenshot of your desktop) instead.

## What I changed in the main plan because of it

Adopted `UniWindowController` (big win — it's the M0 risk made mostly-solved), adopted `uLipSync` as
the secondary lip-sync path, adopted `edge-tts` as the prototype voice, and adopted its
`{"action":"..."}` cue idea under my `anim{cue}` name. Rejected: Python-for-everything, port 8000,
hardcoded `localhost`, and the soft confirmation gate.
