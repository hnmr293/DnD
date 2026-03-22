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

C# debugger (.NET 8+):
```
dotnet build debugger/DnD.slnx
dotnet test debugger/tests/DnD.Core.Tests
dotnet test debugger/tests/DnD.Host.Tests
```

TypeScript MCP server:
```
cd mcp && npm install
cd mcp && npm run build
cd mcp && npm test
```

## E2E Testing

See `docs/mcp-integration-test-plan.md` for the MCP integration test plan. Covers 18 scenarios (launch, breakpoints, stepping, variables, evaluation, exceptions, conditional BPs, hit counts, etc.) executed manually via MCP tools.

## StreamJsonRpc Notes

- Server uses `HeaderDelimitedMessageHandler` (Content-Length framing) + `SystemTextJsonFormatter` with CamelCase
- RPC target methods use `[JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]` for named param support
- Notifications sent via `NotifyWithParameterObjectAsync` — client handlers must match individual property names, not a wrapper object

## Key Design Decisions

- **ICorDebug API direct usage** (not wrapping an external debugger like netcoredbg via DAP) for maximum flexibility across .NET Framework + .NET Core
- **JSON-RPC 2.0 over stdio** between TypeScript and C# — matches MCP's own stdio transport, avoids port management
- **camelCase method names** in JSON-RPC, following DAP conventions (e.g., `launch`, `setBreakpoint`, `stepIn`, `getStackTrace`, `evaluate`)
- Events flow from C# → TypeScript as JSON-RPC notifications: `stopped`, `exited`, `output`

## Current Status

Phase 1 complete (protocol + scaffolding + stub JSON-RPC server with passing tests). Phase 2 (ICorDebug debugger core) is next. See `docs/design.md` for full protocol specification and `docs/handover.md` for decision rationale.
