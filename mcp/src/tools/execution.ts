import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { ClientManager } from "../client-manager.js";
import type { DebuggerClient } from "../debugger-client.js";
import type {
  StoppedParams,
  ExitedParams,
  StackFrame,
} from "../types/protocol.js";

const DEFAULT_TIMEOUT_MS = 30_000;

interface StopEvent {
  type: "stopped";
  params: StoppedParams;
}

interface ExitEvent {
  type: "exited";
  params: ExitedParams;
}

export function waitForStopOrExit(
  client: DebuggerClient,
  timeoutMs = DEFAULT_TIMEOUT_MS,
): Promise<StopEvent | ExitEvent> {
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

async function fetchTopFrame(
  client: DebuggerClient,
  threadId: number,
): Promise<StackFrame | null> {
  try {
    const { stackFrames } = await client.getStackTrace({ threadId });
    return stackFrames.length > 0 ? stackFrames[0] : null;
  } catch {
    return null;
  }
}

export function formatStoppedResponse(
  params: StoppedParams,
  topFrame: StackFrame | null,
): string {
  const bpId = params.breakpointId != null ? ` #${params.breakpointId}` : "";
  let text = `Stopped: ${params.reason}${bpId}${params.description ? ` — ${params.description}` : ""} (thread ${params.threadId})`;
  if (topFrame) {
    const loc = topFrame.file
      ? ` (${topFrame.file}:${topFrame.line ?? "?"})`
      : "";
    const mod = topFrame.moduleId
      ? ` [${topFrame.moduleId.split(/[/\\]/).pop()}]`
      : "";
    text += `\n  at ${topFrame.name}${loc}${mod}`;
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
  clientManager.markRunning();
  const event = await waitPromise;

  if (event.type === "stopped") {
    const topFrame = await fetchTopFrame(client, event.params.threadId);
    return {
      content: [
        {
          type: "text" as const,
          text: formatStoppedResponse(event.params, topFrame),
        },
      ],
    };
  }

  // Process exited — dispose client so next launch spawns a fresh host
  await clientManager.dispose();
  return {
    content: [
      { type: "text" as const, text: formatExitedResponse(event.params) },
    ],
  };
}

export function registerExecutionTools(
  server: McpServer,
  clientManager: ClientManager,
) {
  server.tool(
    "pause",
    "Pause (break) the running process. Use this when the process is running and you need to inspect its state.",
    async () => {
      const client = clientManager.getClient();
      // Register listener BEFORE calling pause — the stopped notification
      // arrives during the RPC response, so we must not miss it.
      const waitPromise = waitForStopOrExit(client, 5_000);
      await client.pause();
      const event = await waitPromise;
      if (event.type === "stopped") {
        const topFrame = await fetchTopFrame(client, event.params.threadId);
        return {
          content: [
            {
              type: "text" as const,
              text: formatStoppedResponse(event.params, topFrame),
            },
          ],
        };
      }
      await clientManager.dispose();
      return {
        content: [
          { type: "text" as const, text: formatExitedResponse(event.params) },
        ],
      };
    },
  );

  server.tool(
    "continue",
    "Continue execution until the next breakpoint or program exit.",
    {
      threadId: z.coerce
        .number()
        .optional()
        .describe("Thread ID to continue (default: all threads)"),
    },
    async (params) => {
      const client = clientManager.getClient();
      return handleExecution(
        client,
        () => client.continue(params),
        clientManager,
      );
    },
  );

  server.tool(
    "stepIn",
    "Step into the next function call. If the current line has no function call, behaves like stepOver (advances to the next line).",
    {
      threadId: z.coerce.number().optional().describe("Thread ID"),
    },
    async (params) => {
      const client = clientManager.getClient();
      return handleExecution(
        client,
        () => client.stepIn(params),
        clientManager,
      );
    },
  );

  server.tool(
    "stepOver",
    "Step over the current line, executing any function calls without stopping inside them.",
    {
      threadId: z.coerce.number().optional().describe("Thread ID"),
    },
    async (params) => {
      const client = clientManager.getClient();
      return handleExecution(
        client,
        () => client.stepOver(params),
        clientManager,
      );
    },
  );

  server.tool(
    "stepOut",
    "Step out of the current function, continuing until it returns to the caller.",
    {
      threadId: z.coerce.number().optional().describe("Thread ID"),
    },
    async (params) => {
      const client = clientManager.getClient();
      return handleExecution(
        client,
        () => client.stepOut(params),
        clientManager,
      );
    },
  );
}
