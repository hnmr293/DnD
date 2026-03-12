import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { ClientManager } from "../client-manager.js";
import type { DebuggerClient } from "../debugger-client.js";
import type { StoppedParams, ExitedParams, StackFrame } from "../types/protocol.js";

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

async function fetchTopFrame(client: DebuggerClient, threadId: number): Promise<StackFrame | null> {
  try {
    const { stackFrames } = await client.getStackTrace({ threadId });
    return stackFrames.length > 0 ? stackFrames[0] : null;
  } catch {
    return null;
  }
}

export function formatStoppedResponse(params: StoppedParams, topFrame: StackFrame | null): string {
  let text = `Stopped: ${params.reason}${params.description ? ` — ${params.description}` : ""} (thread ${params.threadId})`;
  if (topFrame) {
    const loc = topFrame.file ? ` (${topFrame.file}:${topFrame.line ?? "?"})` : "";
    text += `\n  at ${topFrame.name}${loc}`;
  }
  return text;
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
    const topFrame = await fetchTopFrame(client, event.params.threadId);
    return {
      content: [{ type: "text" as const, text: formatStoppedResponse(event.params, topFrame) }],
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
    "Continue execution until the next breakpoint or program exit.",
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
    "Step into the next function call.",
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
    "Step over the current line.",
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
    "Step out of the current function.",
    {
      threadId: z.coerce.number().optional().describe("Thread ID"),
    },
    async (params) => {
      const client = clientManager.getClient();
      return handleExecution(client, () => client.stepOut(params), clientManager);
    }
  );
}
