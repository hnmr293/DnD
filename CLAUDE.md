# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

DnD is a .NET debugger engine + MCP server that enables LLMs (Claude etc.) to debug C# programs via MCP (Model Context Protocol). Windows only.

## Architecture

```
Claude (LLM) → MCP (stdio) → TypeScript MCP Server → JSON-RPC (stdio) → C# Debugger Engine (ICorDebug COM API) → .NET Process
```

Two main components:
- **C# Debugger Engine** (`debugger/`): Uses ICorDebug COM API directly to control .NET processes (both .NET Framework and .NET Core). Solution file: `debugger/DnD.slnx`
  - `DnD.Core` — ICorDebug wrapper, process control, breakpoints, stack/variable inspection, expression evaluation
  - `DnD.Protocol` — JSON-RPC message type definitions and serialization
  - `DnD.Host` — Console app serving as stdio JSON-RPC 2.0 server
  - `DnD.Core.Tests` — Unit tests
- **TypeScript MCP Server** (`mcp/`): Receives MCP requests from Claude, spawns C# Host as child process, relays via JSON-RPC
  - `src/index.ts` — MCP server entry point
  - `src/tools/` — MCP tool definitions (launch, setBreakpoint, stepOver, etc.)
  - `src/debugger-client.ts` — JSON-RPC client to C# Host
  - `src/types/` — TypeScript protocol type definitions
- **Shared Protocol** (`protocol/schema/`): JSON Schema definitions used to generate types for both C# and TypeScript

## Build & Test Commands

All commands must be run from the repository root.

### C# debugger (.NET 8+)
```bash
dotnet build debugger/DnD.slnx
dotnet test debugger/tests/DnD.Core.Tests           # Unit tests (fast, ~1s)
dotnet test debugger/tests/DnD.Host.Tests           # E2E tests (slow, ~3min, spawns real debugger processes)
```

### TypeScript MCP server
```bash
cd mcp && npm install
cd mcp && npm run build      # tsc only
cd mcp && npm test           # vitest (stub mode, fast)
```

### Full build (C# Host publish + TypeScript)
```bash
cd mcp && npm run build:all  # dotnet publish DnD.Host → mcp/bin/ then tsc
```
When `npm link` is active, `npm run build:all` deploys changes to the MCP server.

### Notes
- Run `npm` commands inside `mcp/` — there is no `package.json` at the repo root
- `dotnet build/test` can be run from the repo root using relative paths
- **Important**: `cd mcp && npm ...` changes the working directory. Subsequent `dotnet` commands must use absolute paths (e.g., `dotnet test /path/to/project/debugger/tests/DnD.Host.Tests`) or explicitly `cd` back to the repo root.

## E2E Testing

See `docs/mcp-integration-test-plan.md` for the MCP integration test plan. Covers 19 scenarios (launch, breakpoints, stepping, variables, evaluation, exceptions, conditional BPs, hit counts, waitForStop, etc.) executed manually via MCP tools.

## StreamJsonRpc Notes

- Server uses `HeaderDelimitedMessageHandler` (Content-Length framing) + `SystemTextJsonFormatter` with CamelCase
- RPC target methods use `[JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]` for named param support
- Notifications sent via `NotifyWithParameterObjectAsync` — client handlers must match individual property names, not a wrapper object

## Key Design Decisions

- **ICorDebug API direct usage** (not wrapping an external debugger like netcoredbg via DAP) for maximum flexibility across .NET Framework + .NET Core
- **JSON-RPC 2.0 over stdio** between TypeScript and C# — matches MCP's own stdio transport, avoids port management
- **camelCase method names** in JSON-RPC, following DAP conventions (e.g., `launch`, `setBreakpoint`, `stepIn`, `getStackTrace`, `evaluate`)
- Events flow from C# → TypeScript as JSON-RPC notifications: `stopped`, `exited`, `output`
