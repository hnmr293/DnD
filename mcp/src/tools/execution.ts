import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { ClientManager } from "../client-manager.js";
import type { DebuggerClient } from "../debugger-client.js";
import type { StoppedParams, ExitedParams, StackFrame, Variable } from "../types/protocol.js";

const DEFAULT_TIMEOUT_MS = 30_000;

interface StopEvent {
  type: "stopped";
  params: StoppedParams;
}

interface ExitEvent {
  type: "exited";
  params: ExitedParams;
}

export function waitForStopOrExit(client: DebuggerClient, timeoutMs = DEFAULT_TIMEOUT_MS): Promise<StopEvent | ExitEvent> {
  return new Promise((resolve, reject) => {
    let resolved = false;
    const cleanup = () => {
      resolved = true;
      client.removeListener("stopped", onStopped);
      client.removeListener("exited", onExited);
    };

    const onStopped = (params: StoppedParams) => {
      if (resolved) return;
      cleanup();
      resolve({ type: "stopped", params });
    };
    const onExited = (params: ExitedParams) => {
      if (resolved) return;
      cleanup();
      resolve({ type: "exited", params });
    };

    client.on("stopped", onStopped);
    client.on("exited", onExited);

    setTimeout(() => {
      if (resolved) return;
      cleanup();
      reject(new Error(`Timed out waiting for stop/exit after ${timeoutMs}ms`));
    }, timeoutMs);
  });
}

interface StopContext {
  stackFrames: StackFrame[];
  variables: Variable[];
}

export async function fetchContext(client: DebuggerClient, threadId: number): Promise<StopContext> {
  try {
    const { stackFrames } = await client.getStackTrace({ threadId });
    let variables: Variable[] = [];
    if (stackFrames.length > 0) {
      const result = await client.getVariables({ variablesReference: 0 });
      variables = result.variables;
    }
    return { stackFrames, variables };
  } catch {
    return { stackFrames: [], variables: [] };
  }
}

export function formatStoppedResponse(params: StoppedParams, context: StopContext): string {
  const lines: string[] = [];
  lines.push(`Stopped: ${params.reason}${params.description ? ` — ${params.description}` : ""} (thread ${params.threadId})`);

  if (context.stackFrames.length > 0) {
    lines.push("");
    lines.push("Stack trace:");
    for (const frame of context.stackFrames) {
      const loc = frame.file ? ` at ${frame.file}:${frame.line ?? "?"}` : "";
      lines.push(`  #${frame.id} ${frame.name}${loc}`);
    }
  }

  if (context.variables.length > 0) {
    lines.push("");
    lines.push("Local variables:");
    for (const v of context.variables) {
      lines.push(`  ${v.name}: ${v.type ?? ""} = ${v.value}`);
    }
  }

  return lines.join("\n");
}

export function formatExitedResponse(params: ExitedParams): string {
  return `Process exited with code ${params.exitCode}`;
}

async function handleExecution(
  client: DebuggerClient,
  action: () => Promise<void>,
  clientManager: ClientManager,
): Promise<{ content: { type: "text"; text: string }[] }> {
  const waitPromise = waitForStopOrExit(client);
  await action();
  const event = await waitPromise;

  if (event.type === "stopped") {
    const context = await fetchContext(client, event.params.threadId);
    return {
      content: [{ type: "text" as const, text: formatStoppedResponse(event.params, context) }],
    };
  }

  // Process exited — dispose client so next launch spawns a fresh host
  await clientManager.dispose();
  return {
    content: [{ type: "text" as const, text: formatExitedResponse(event.params) }],
  };
}

export function registerExecutionTools(server: McpServer, clientManager: ClientManager) {
  server.tool(
    "continue",
    "Continue execution until the next breakpoint or program exit. Returns the stop location with stack trace and local variables.",
    {
      threadId: z.coerce.number().optional().describe("Thread ID to continue (default: all threads)"),
    },
    async (params) => {
      const client = clientManager.getClient();
      return handleExecution(client, () => client.continue(params), clientManager);
    }
  );

  server.tool(
    "stepIn",
    "Step into the next function call. Returns the stop location with stack trace and local variables.",
    {
      threadId: z.coerce.number().optional().describe("Thread ID"),
    },
    async (params) => {
      const client = clientManager.getClient();
      return handleExecution(client, () => client.stepIn(params), clientManager);
    }
  );

  server.tool(
    "stepOver",
    "Step over the current line. Returns the stop location with stack trace and local variables.",
    {
      threadId: z.coerce.number().optional().describe("Thread ID"),
    },
    async (params) => {
      const client = clientManager.getClient();
      return handleExecution(client, () => client.stepOver(params), clientManager);
    }
  );

  server.tool(
    "stepOut",
    "Step out of the current function. Returns the stop location with stack trace and local variables.",
    {
      threadId: z.coerce.number().optional().describe("Thread ID"),
    },
    async (params) => {
      const client = clientManager.getClient();
      return handleExecution(client, () => client.stepOut(params), clientManager);
    }
  );
}
