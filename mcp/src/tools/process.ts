import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { ClientManager } from "../client-manager.js";
import type { DebuggerClient } from "../debugger-client.js";
import type { OutputParams } from "../types/protocol.js";
import {
  waitForStopOrExit,
  formatStoppedResponse,
  formatExitedResponse,
} from "./execution.js";

/** Collect console-category output events (e.g. optimization warnings) from the client. */
function collectConsoleOutput(client: DebuggerClient): {
  getWarnings: () => string[];
  dispose: () => void;
} {
  const warnings: string[] = [];
  const onOutput = (params: OutputParams) => {
    if (params.category === "console") {
      warnings.push(params.output);
    }
  };
  client.on("output", onOutput);
  return {
    getWarnings: () => warnings,
    dispose: () => client.removeListener("output", onOutput),
  };
}

function formatWarnings(warnings: string[]): string {
  if (warnings.length === 0) return "";
  return "\n" + warnings.join("\n");
}

export function registerProcessTools(
  server: McpServer,
  clientManager: ClientManager,
) {
  server.tool(
    "launch",
    "Launch a .NET program under the debugger. Must be called before any other debugging operation. The response includes the path to a log file containing all program output (stdout/stderr) — use the Read tool to view it.",
    {
      program: z
        .string()
        .describe("Path to the .NET executable or DLL to debug"),
      args: z.array(z.string()).optional().describe("Command-line arguments"),
      cwd: z.string().optional().describe("Working directory"),
      env: z
        .record(z.string(), z.string())
        .optional()
        .describe("Environment variables"),
      stopAtEntry: z.boolean().optional().describe("Stop at the entry point"),
    },
    async (params) => {
      const client = await clientManager.ensureClient();
      const collector = collectConsoleOutput(client);
      const waitPromise = waitForStopOrExit(client);
      const result = await client.launch(params);
      clientManager.markRunning();
      const event = await waitPromise;
      collector.dispose();

      const warnings = formatWarnings(collector.getWarnings());
      const hostPidLine = clientManager.hostPid
        ? `\nDebugger host PID: ${clientManager.hostPid}`
        : "";
      const outputLine = clientManager.outputFile
        ? `\nOutput file: ${clientManager.outputFile}`
        : "";

      if (event.type === "stopped") {
        const { stackFrames } = await client.getStackTrace({
          threadId: event.params.threadId,
        });
        const topFrame = stackFrames.length > 0 ? stackFrames[0] : null;
        return {
          content: [
            {
              type: "text" as const,
              text:
                formatStoppedResponse(event.params, topFrame) +
                hostPidLine +
                outputLine +
                warnings,
            },
          ],
        };
      }

      // Process ran to completion without stopping
      const exitText =
        formatExitedResponse(event.params) + hostPidLine + outputLine + warnings;
      await clientManager.dispose();
      return {
        content: [{ type: "text" as const, text: exitText }],
      };
    },
  );

  server.tool(
    "attach",
    "Attach the debugger to a running .NET process. The response includes the path to a log file containing all program output (stdout/stderr) — use the Read tool to view it.",
    {
      processId: z.coerce.number().describe("Process ID to attach to"),
    },
    async (params) => {
      const client = await clientManager.ensureClient();
      const collector = collectConsoleOutput(client);
      const result = await client.attach(params);
      clientManager.markRunning();

      // Give module-load notifications a moment to arrive
      await new Promise((resolve) => setTimeout(resolve, 500));
      collector.dispose();

      const warnings = formatWarnings(collector.getWarnings());
      const hostPidLine = clientManager.hostPid
        ? `\nDebugger host PID: ${clientManager.hostPid}`
        : "";
      const outputLine = clientManager.outputFile
        ? `\nOutput file: ${clientManager.outputFile}`
        : "";
      return {
        content: [
          {
            type: "text" as const,
            text: `Attached to process ${result.processId}${hostPidLine}${outputLine}${warnings}`,
          },
        ],
      };
    },
  );

  server.tool(
    "detach",
    "Detach the debugger from the process without killing it.",
    async () => {
      const client = clientManager.getClient();
      const hostPid = clientManager.hostPid;
      await client.detach();
      await clientManager.dispose();
      const pidInfo = hostPid ? ` (host PID was ${hostPid})` : "";
      return {
        content: [
          { type: "text" as const, text: `Detached from process${pidInfo}` },
        ],
      };
    },
  );

  server.tool("terminate", "Terminate the debugged process.", async () => {
    const client = clientManager.getClient();
    await client.terminate();
    await clientManager.dispose();
    return {
      content: [{ type: "text" as const, text: "Process terminated" }],
    };
  });

  server.tool(
    "getState",
    "Get the current debugger state: not-started, running, stopped, or exited. Includes stop reason and output file path when applicable.",
    async () => {
      const snap = clientManager.getStateSnapshot();
      let text = `State: ${snap.state}`;
      if (snap.state === "stopped") {
        text += ` (${snap.stopReason ?? "unknown"})`;
        if (snap.stopDescription) {
          text += ` — ${snap.stopDescription}`;
        }
        if (snap.threadId != null) {
          text += ` (thread ${snap.threadId})`;
        }
      }
      if (snap.state === "exited" && snap.exitCode != null) {
        text += ` (exit code ${snap.exitCode})`;
      }
      if (snap.outputFile) {
        text += `\nOutput file: ${snap.outputFile}`;
      }
      return {
        content: [{ type: "text" as const, text }],
      };
    },
  );
}
