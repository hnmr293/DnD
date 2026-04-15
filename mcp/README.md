# dnd-mcp

MCP server for the DnD .NET debugger. Enables LLMs (Claude, etc.) to debug C# programs via [Model Context Protocol](https://modelcontextprotocol.io/).

**Windows only** — uses the ICorDebug COM API to control .NET processes directly.

## Features

- Launch and attach to .NET processes (.NET Framework and .NET Core/.NET 5+)
- Set breakpoints (with conditions and hit counts)
- Step through code (stepOver, stepIn, stepOut)
- Inspect variables and stack traces
- Evaluate C# expressions using Roslyn (supports `new`, `typeof`, LINQ, lambdas, generics, private member access)
- Exception breakpoints with type filtering
- Program output capture to file

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- Node.js 18+

## Installation

```bash
npm install -g dnd-mcp
```

## Configuration

Add to your MCP client configuration (e.g., Claude Code `.mcp.json`):

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

## Available Tools

| Tool | Description |
|------|-------------|
| `launch` | Launch a .NET program under the debugger |
| `attach` | Attach to a running .NET process |
| `terminate` | Terminate the debugged process |
| `continue` | Continue execution |
| `pause` | Pause the running process |
| `stepOver` | Step over the current line |
| `stepIn` | Step into a function call |
| `stepOut` | Step out of the current function |
| `setBreakpoint` | Set a breakpoint (supports conditions and hit counts) |
| `removeBreakpoint` | Remove a breakpoint |
| `getBreakpoints` | List all breakpoints |
| `setExceptionBreakpoints` | Configure exception break behavior |
| `getStackTrace` | Get the current call stack |
| `getVariables` | Get variables for a stack frame |
| `evaluate` | Evaluate a C# expression |
| `getThreads` | List all threads |
| `getException` | Get current exception details |
| `getState` | Get debugger state |
| `waitForStop` | Wait for the process to stop or exit |

## Architecture

```
LLM (Claude) --> MCP (stdio) --> TypeScript MCP Server --> JSON-RPC (stdio) --> C# Debugger Engine (ICorDebug) --> .NET Process
```

The npm package includes both the TypeScript MCP server and a pre-built C# debugger host (`bin/`).

## License

Apache-2.0
