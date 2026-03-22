# DnD

.NET debugger for LLMs. Debug C# programs through [MCP (Model Context Protocol)](https://modelcontextprotocol.io/).

Windows only. Supports .NET Framework and .NET Core/.NET 5+.

## Architecture

```
LLM → MCP (stdio) → TypeScript MCP Server → JSON-RPC (stdio) → C# Debugger Engine (ICorDebug) → .NET Process
```

## Quick Start

### Prerequisites

- Node.js >= 18
- .NET 8 SDK (for building from source)

### Install

```bash
npm install -g dnd-mcp
```

### Configure MCP

Add to your `.mcp.json`:

```json
{
  "mcpServers": {
    "dnd": {
      "type": "stdio",
      "command": "npx",
      "args": ["dnd-mcp"]
    }
  }
}
```

## MCP Tools

### Process Control

| Tool | Description |
|------|-------------|
| `launch` | Launch a .NET program under the debugger |
| `attach` | Attach to a running .NET process |
| `detach` | Detach from the debuggee |
| `terminate` | Terminate the debuggee |
| `getState` | Get current debugger state |

### Execution Control

| Tool | Description |
|------|-------------|
| `continue` | Resume execution |
| `pause` | Break into the debugger |
| `stepIn` | Step into the next method call |
| `stepOver` | Step over the current line |
| `stepOut` | Step out of the current method |

### Breakpoints

| Tool | Description |
|------|-------------|
| `setBreakpoint` | Set a breakpoint at a source location |
| `removeBreakpoint` | Remove a breakpoint by ID |
| `getBreakpoints` | List all breakpoints |
| `setExceptionBreakpoints` | Configure exception breakpoints |

### Inspection

| Tool | Description |
|------|-------------|
| `getThreads` | List all managed threads |
| `getStackTrace` | Get the call stack of a thread |
| `getVariables` | Inspect local variables, arguments, and fields |
| `evaluate` | Evaluate a C# expression in the current context |
| `getException` | Get the current exception details |

## Development

### Build from Source

```bash
# Build everything (C# debugger engine + TypeScript MCP server)
cd mcp
npm install
npm run build:all

# Run tests
dotnet test debugger/tests/DnD.Core.Tests
dotnet test debugger/tests/DnD.Host.Tests
cd mcp && npm test
```

### Use Local Build with MCP

Set `DND_HOST_PATH` to point to your local C# debugger build:

```json
{
  "mcpServers": {
    "dnd": {
      "type": "stdio",
      "command": "node",
      "args": ["<path-to-repo>/mcp/dist/index.js"],
      "env": {
        "DND_HOST_PATH": "<path-to-repo>/debugger/src/DnD.Host/bin/Debug/net8.0-windows/DnD.Host.dll"
      }
    }
  }
}
```

## License

Apache License 2.0 — see [LICENSE](LICENSE) for details.
