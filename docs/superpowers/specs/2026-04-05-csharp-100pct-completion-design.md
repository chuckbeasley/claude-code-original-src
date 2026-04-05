# C# Reimplementation — 100% Completion Design

**Date:** 2026-04-05
**Status:** Approved
**Approach:** Phased Sequential (A)

## Context

The C# reimplementation of Claude Code is ~95% complete. Four subsystems remain at 0–70%:

| Subsystem | Pre-work Status | Target |
|-----------|----------------|--------|
| Feature Flags | 0% | 100% |
| Plugin Commands | 70% (tools only) | 100% |
| Voice Input | 0% (stub) | 100% |
| KAIROS / Buddy Modes | 0% | 100% |

Delivery order: **Phase 1** Feature Flags → **Phase 2** Plugin Commands + Voice (parallel) → **Phase 3** KAIROS/Buddy.

---

## Phase 1: Feature Flags

### Goal

A runtime feature-flag system that mirrors the TypeScript `bun:bundle` conditional tool registration, using environment variables (override) and `settings.json` (persistent default).

### Components

**`ClaudeCode.Configuration.FeatureFlags`** (new static class)

- Loaded once at startup via `FeatureFlags.Load(GlobalConfig config)`
- `bool IsEnabled(string flag)` — public query method
- Resolution order: env var > settings.json > hardcoded default

**`GlobalConfig`** extension

```csharp
// New property added to existing GlobalConfig record
public Dictionary<string, bool>? Features { get; init; }
```

**Env var convention:** `CLAUDE_FEATURE_<UPPERCASE_FLAG_NAME>=1|true|0|false`
Non-empty, non-zero, non-"false" strings are treated as `true`.

**Flag registry:**

| Flag | Default | Controlled tools / behaviour |
|------|---------|------------------------------|
| `cron` | `false` | CronCreateTool, CronDeleteTool, CronListTool |
| `sleep` | `false` | SleepTool |
| `coordinator` | `false` | TeamCreateTool, TeamDeleteTool |
| `agent-triggers` | `false` | CronTools + RemoteTriggerTool |
| `voice` | `false` | VoiceInputService auto-start |
| `kairos` | `false` | `/assistant`, `/buddy` commands |
| `bridge` | `false` | BridgeServer auto-start on REPL launch |
| `proactive` | `false` | SleepTool + RemoteTriggerTool |

**`ToolRegistry` integration**

Each gated tool class is annotated with `[FeatureFlag("cron")]`. `ToolRegistry` skips registration for tools whose declared flag returns `false` from `FeatureFlags.IsEnabled()`. Tools with no attribute are always registered.

**`/config features` subcommand**

Prints a table: flag name | effective value | source (env / settings / default).

### Data Flow

```
Program.cs
  → ConfigProvider.Load()          // loads settings.json
  → FeatureFlags.Load(config)      // merges env vars; freezes flag table
  → DI container setup
      → ToolRegistry filters via FeatureFlags.IsEnabled()
      → BridgeServer auto-started if "bridge" flag is on
```

### Error Handling

- Unknown flag names in settings.json are ignored (forward compatibility).
- Malformed env var values (not parseable as bool) default to `true` if non-empty.
- `FeatureFlags.Load` is safe to call multiple times (idempotent; last-write wins).

### Testing

`FeatureFlagsTests` in `ClaudeCode.Core.Tests`:
- Env var overrides settings.json value
- settings.json default respected when no env var present
- Hardcoded default respected when neither source sets the flag
- Malformed env var treated as `true`
- `/config features` output reflects correct source label

---

## Phase 2a: Plugin Commands

### Goal

Plugins can register slash commands (not just tools), appearing in `/help` and dispatchable from the REPL.

### Manifest Extension

```json
{
  "name": "my-plugin",
  "version": "1.0.0",
  "entryPoint": "tool.dll",
  "commands": [
    {
      "name": "deploy",
      "description": "Deploy to staging or production",
      "script": "deploy.sh",
      "usage": "/deploy [env]"
    }
  ]
}
```

### Components

**`PluginCommandDefinition`** (new record in `PluginLoader.cs`)

```csharp
public record PluginCommandDefinition(
    string Name,
    string Description,
    string Script,
    string? Usage);
```

**`PluginManifest`** extension

```csharp
public List<PluginCommandDefinition>? Commands { get; init; }
```

**`ScriptPluginCommand : SlashCommand`** (new class)

- Constructor takes `PluginCommandDefinition` + plugin directory path
- `Execute`: runs `<pluginDir>/<script>` with user args as a single command-line argument
- 30-second timeout (same as `ScriptPluginTool`)
- Returns stdout as the command response; non-zero exit appends stderr

**`PluginLoader.LoadCommands(string cwd)`** (new method)

- Calls `LoadAll(cwd)`, then maps each `PluginCommandDefinition` → `ScriptPluginCommand`
- Returns `IEnumerable<SlashCommand>`

**`ReplSession.BuildCommandRegistry`** (updated)

- After registering built-in commands, calls `pluginLoader.LoadCommands(cwd)`
- Each returned command is registered; name collision with a built-in → built-in wins, warning printed once

**`/reload-plugins` command** (already exists)

- Extended to also reload commands: clears and re-registers plugin commands alongside tools

**`/help` output**

- Plugin commands listed in a distinct "Plugin Commands" section after built-in commands

### Data Flow

```
REPL startup
  → BuildCommandRegistry()
      → register ~91 built-ins
      → pluginLoader.LoadCommands(cwd)
          → LoadAll() parses manifests
          → each commands[] entry → ScriptPluginCommand
      → register plugin commands (collision check)

User types /deploy staging
  → CommandDispatcher resolves to ScriptPluginCommand("deploy")
  → Runs: bash deploy.sh "staging" (or pwsh/cmd on Windows)
  → Prints stdout
```

### Error Handling

- Script not found: `Plugin command 'deploy': script 'deploy.sh' not found in <dir>`
- Non-zero exit: print stderr prefixed with `[error]`
- Timeout: `Plugin command 'deploy' timed out after 30s`
- Missing `script` field in manifest: skip command, print warning at load time

### Testing

`PluginLoaderTests` in `ClaudeCode.Tools.Tests`:
- Manifest with `commands` array populates `PluginManifest.Commands`
- `LoadCommands` returns `ScriptPluginCommand` instances
- Name collision with built-in: built-in retained, warning emitted
- Script execution: stdout returned, stderr on non-zero exit
- `/reload-plugins` re-registers both tools and commands

---

## Phase 2b: Voice Input

### Goal

`/voice` enables hands-free speech-to-text input using Windows built-in speech recognition. Recognized text is submitted as a user turn.

### Platform Constraint

Windows only. Uses `System.Speech.Recognition` (no external API, no key, built-in since .NET 4.x, available via NuGet for .NET 5+).

**Dependency** added to `ClaudeCode.Services.csproj`:

```xml
<PackageReference Include="System.Speech" Version="8.0.0"
                  Condition="'$(OS)' == 'Windows_NT'" />
```

### Components

**`VoiceInputService`** (new, `ClaudeCode.Services/Voice/VoiceInputService.cs`)

```csharp
public sealed class VoiceInputService : IDisposable
{
    public event Action<string>? TextRecognized;
    public bool IsListening { get; private set; }

    public void Start();   // loads DictationGrammar, begins async recognition
    public void Stop();    // stops engine, releases mic
    public void Dispose();
}
```

- Depends on `IVoiceEngine` (new interface in `ClaudeCode.Services/Voice/IVoiceEngine.cs`) with `Start()`, `Stop()`, `SpeechRecognized` event, `SpeechRejected` event — wraps `SpeechRecognitionEngine`; swappable in tests
- `DefaultVoiceEngine` (concrete, Windows-only) wraps `SpeechRecognitionEngine` with `DictationGrammar` (free-form)
- Raises `TextRecognized` on `SpeechRecognized` engine event with `e.Result.Text`
- On `SpeechRecognitionRejected`: increments a counter; every 10 seconds of silence prints `[voice: listening...]` heartbeat to console
- On engine init failure: throws `VoiceUnavailableException(message)` — caught by `/voice` command

**`ReplModeFlags`** extension

```csharp
public bool VoiceEnabled { get; set; }
```

**`ReplSession`** integration

- Holds a lazily-created `VoiceInputService` instance
- When `VoiceEnabled` flips to `true`: calls `service.Start()`; subscribes `OnTextRecognized`
- When `VoiceEnabled` flips to `false`: calls `service.Stop()`
- `OnTextRecognized(string text)`: prints `Recognized: "<text>"` in dim-grey, then submits text as user turn (same code path as Enter key)
- Prompt prefix: `[MIC] ` prepended when `VoiceEnabled`

**`/voice` command** (already stub — fills in implementation)

```
/voice          → toggle on/off
/voice status   → show current state
```

- If not Windows: prints `Voice input requires Windows (System.Speech)` and returns
- If `voice` feature flag is off: prints `Enable with: CLAUDE_FEATURE_VOICE=1`
- On `VoiceUnavailableException`: prints `Voice input unavailable: <message>` (no mic, SR not installed, etc.)

### Data Flow

```
User runs /voice
  → platform guard passes
  → VoiceInputService.Start()
  → Prompt changes to "[MIC] > "

User speaks "find all TODO comments"
  → SpeechRecognitionEngine raises SpeechRecognized
  → TextRecognized event fires with "find all TODO comments"
  → ReplSession.OnTextRecognized prints dim-grey: Recognized: "find all TODO comments"
  → Submitted as user turn → QueryEngine processes normally
```

### Error Handling

- No microphone: `SpeechRecognitionEngine` throws on `Start()` → caught → `VoiceUnavailableException`
- SR not installed (non-English Windows): engine init fails → same path
- Timeout (no speech 30s): no action; heartbeat keeps user informed

### Testing

`VoiceInputServiceTests` in `ClaudeCode.Services.Tests`:
- Platform guard: non-Windows returns error message, no crash
- Feature flag off: `/voice` prints enable instruction
- `TextRecognized` event fires on recognized speech (mock engine via `IVoiceEngine` interface)
- Heartbeat printed after 10s silence
- `Stop()` after `Start()` leaves service in clean state

---

## Phase 3: KAIROS / Buddy Modes

### Goal

Two behavioral modes that modify Claude's interaction style via system prompt injection. No custom widget protocol or response parsing.

### Feature Flag Gate

Both modes require `CLAUDE_FEATURE_KAIROS=1` (or `features.kairos: true` in settings.json). If the flag is off, `/assistant` and `/buddy` print:

```
Assistant mode is disabled. Enable with: CLAUDE_FEATURE_KAIROS=1
```

---

### KAIROS — Assistant Mode

**`ReplModeFlags`** extension

```csharp
public bool KairosEnabled { get; set; }
```

**`SystemPromptBuilder.BuildKairosAddendum()`** (new method)

Returns a fixed instruction block appended to the system prompt when `KairosEnabled`:

```
--- ASSISTANT MODE ---
You are operating in assistant mode. Follow these rules every turn:
1. When the user's intent is ambiguous, ask exactly one clarifying question before acting.
2. When presenting choices, always use a numbered list (1. option  2. option ...).
3. Before executing any destructive operation (delete files, overwrite, reset, force-push),
   state what you are about to do and ask: "Shall I proceed? (yes/no)"
4. Begin every multi-step task with one sentence: "I'll do X, then Y, then Z."
--- END ASSISTANT MODE ---
```

**`/assistant` command**

- Toggles `KairosEnabled`
- Prompt prefix: `[ASSISTANT] > ` when active
- Prints confirmation: `Assistant mode ON` / `Assistant mode OFF`

**Numbered selection shortcut**

In `ReplSession.ProcessInput()`: if `KairosEnabled` is true and the raw input matches `^\d+$` (a bare integer), prepend `"Select option "` before submission. Claude handles the selection naturally without any response parsing.

---

### Buddy Mode

**`ReplModeFlags`** extension

```csharp
public bool BuddyEnabled { get; set; }
```

**`BuddyService`** (new, `ClaudeCode.Services/AutoDream/BuddyService.cs`)

```csharp
public sealed class BuddyService
{
    public async Task<string?> GetContextNoteAsync(
        IReadOnlyList<Message> recentMessages,
        CancellationToken ct);
}
```

- Takes last 3 message pairs (6 messages max)
- Calls `AnthropicClient` with model `claude-haiku-4-5-20251001` and prompt:
  ```
  In one sentence (max 12 words), what is the user currently working on?
  ```
- 5-second timeout via `CancellationTokenSource.CreateLinkedTokenSource(ct, fiveSecondCts)`
- Returns `null` on timeout or any error
- Token cost tracked via `CostTracker`

**`ReplSession`** integration

- After each completed assistant turn (when `BuddyEnabled`): fires `BuddyService.GetContextNoteAsync()` as a background task; stores result in `_pendingBuddyNote`
- At the **start of the next user turn**, before drawing the `> ` prompt: if `_pendingBuddyNote` has a result, print it as a dim-grey footer, then clear it
- This avoids async display races: the note is always shown at a deterministic point between turns, never mid-output
- If `_pendingBuddyNote` is not yet complete when the next turn starts: silently discarded (buddy task cancelled)

**`/buddy` command**

- Toggles `BuddyEnabled`
- No prompt prefix change (silent mode)
- Prints confirmation: `Buddy mode ON` / `Buddy mode OFF`

### Error Handling

- `BuildKairosAddendum()` is append-only; existing system prompt is never modified
- Buddy API call failure: silent (returns `null`)
- Buddy result arrives late: silently discarded, no visual glitch
- Both modes independent: KAIROS on + Buddy on is valid

### Testing

`KairosModeTests` in `ClaudeCode.Services.Tests`:
- System prompt addendum appended only when `KairosEnabled`
- Bare integer input prefixed with "Select option " only in KAIROS mode
- Feature flag off: both commands print enable instruction

`BuddyServiceTests` in `ClaudeCode.Services.Tests`:
- 5s timeout: `null` returned, no exception propagated
- Successful call: note trimmed to sentence, returned correctly
- Note shown before next-turn prompt (deterministic; never mid-output): result stored in `_pendingBuddyNote`, consumed at prompt-draw time
- Still-pending result when next turn starts: task cancelled, note silently skipped
- Cost tracked in `CostTracker`

---

## Delivery Summary

| Phase | Work | Files Touched |
|-------|------|---------------|
| **1** | Feature Flags | `GlobalConfig.cs`, `FeatureFlags.cs` (new), `ToolRegistry.cs`, `BuiltInCommands.cs` (`/config features`), each gated tool class (+attribute) |
| **2a** | Plugin Commands | `PluginLoader.cs`, `PluginManifest` (extend), `ScriptPluginCommand.cs` (new), `ReplSession.cs`, `BuiltInCommands.cs` (`/reload-plugins` extend, `/help` extend) |
| **2b** | Voice Input | `VoiceInputService.cs` (new), `IVoiceEngine.cs` (new interface for testing), `ReplModeFlags.cs`, `ReplSession.cs`, `BuiltInCommands.cs` (`/voice` impl), `ClaudeCode.Services.csproj` |
| **3** | KAIROS/Buddy | `ReplModeFlags.cs`, `SystemPromptBuilder.cs`, `BuddyService.cs` (new), `ReplSession.cs`, `BuiltInCommands.cs` (`/assistant`, `/buddy` impl) |
