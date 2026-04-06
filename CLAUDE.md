# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Is

A source-only research snapshot of Anthropic's Claude Code CLI (~1,900 TypeScript files, 512K+ lines). There is no package.json, no build system, and no test runner — this is a read-only archive for architecture study and security research. You cannot build, run, or test this code.

## Tech Stack

- **Runtime**: Bun
- **Language**: TypeScript (strict)
- **Terminal UI**: React + Ink
- **CLI Parsing**: Commander.js
- **Schema Validation**: Zod v4
- **Code Search**: ripgrep
- **Protocols**: MCP SDK, LSP
- **API**: Anthropic SDK
- **Feature Flags**: GrowthBook + `bun:bundle` compile-time flags

## Architecture

### Entrypoint Flow

`src/entrypoints/cli.tsx` → `src/main.tsx` (Commander.js CLI parser + Ink renderer init) → `src/QueryEngine.ts` (core LLM loop). Startup prefetches MDM settings, keychain reads, and API preconnect in parallel before heavy module loading.

### Core Engine — `src/QueryEngine.ts`

The central orchestrator. Handles streaming LLM responses, tool-call loops, thinking mode, retry logic, token counting, and session persistence. Calls into `src/query.ts` for the actual API request pipeline.

### Tool System — `src/tools/`

~40 tools, each in its own directory (e.g., `BashTool/`, `FileEditTool/`, `AgentTool/`). Every tool defines an input schema, permission model, and execution logic. Tools are registered via `src/Tool.ts` (base types/interfaces) and merged into the active set by `src/hooks/useMergedTools.ts`. The permission layer at `src/hooks/toolPermission/` gates every invocation.

### Command System — `src/commands.ts` + `src/commands/`

~50 slash commands registered in `commands.ts` via static imports. Each command lives in its own directory under `src/commands/`. Some commands are gated by `process.env.USER_TYPE === 'ant'` (Anthropic-internal).

### Service Layer — `src/services/`

External integrations: `api/` (Anthropic API client), `mcp/` (MCP server connections), `oauth/` (auth flow), `lsp/` (Language Server Protocol), `analytics/` (GrowthBook feature flags), `compact/` (context compression), `plugins/` (plugin loader).

### UI Layer — `src/components/` + `src/screens/`

~140 React/Ink components. `src/components/App.tsx` is the root. Full-screen UIs (Doctor, REPL, Resume) live in `src/screens/`. React hooks in `src/hooks/` manage state, keybindings, IDE integration, and tool permissions.

### Multi-Agent — `src/coordinator/` + `src/tools/AgentTool/`

Sub-agents are spawned via `AgentTool`. The `coordinator/` directory handles multi-agent orchestration. `TeamCreateTool`/`TeamDeleteTool` enable team-level parallel work.

### Bridge System — `src/bridge/`

Bidirectional communication between IDE extensions (VS Code, JetBrains) and the CLI. JWT-based auth, session management, and message protocol.

### Feature Flags

Compile-time dead code elimination via `bun:bundle`:
```typescript
import { feature } from 'bun:bundle'
const voiceCommand = feature('VOICE_MODE') ? require('./commands/voice/index.js').default : null
```
Notable flags: `PROACTIVE`, `KAIROS`, `BRIDGE_MODE`, `DAEMON`, `VOICE_MODE`, `AGENT_TRIGGERS`, `MONITOR_TOOL`.

### Key Subsystems

- **Skills**: `src/skills/` — reusable workflows executed via `SkillTool`
- **Plugins**: `src/plugins/` + `src/services/plugins/` — plugin loading and management
- **Memory**: `src/memdir/` — persistent memory directory system
- **Tasks**: `src/tasks/` + `src/Task.ts` — task management
- **State**: `src/state/` — application state (includes `AppState.ts`)
- **Vim Mode**: `src/vim/` — vim keybinding support
- **Voice**: `src/voice/` — voice input processing

## Code Conventions

- Imports use `src/` absolute paths (e.g., `import { foo } from 'src/utils/bar.js'`)
- Files use `.js` extensions in imports despite being TypeScript (Bun convention)
- Biome is used for linting (see `biome-ignore` comments)
- Lodash-es is used for utilities (imported per-function: `lodash-es/memoize.js`)
- Types are imported with `import type` syntax
