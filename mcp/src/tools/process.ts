import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { ClientManager } from "../client-manager.js";
import { waitForStopOrExit, formatStoppedResponse, formatExitedResponse } from "./execution.js";

export function registerProcessTools(server: McpServer, clientManager: ClientManager) {
  server.tool(
    "launch",
    "Launch a .NET program under the debugger. Must be called before any other debugging operation.",
    {
      program: z.string().describe("Path to the .NET executable or DLL to debug"),
      args: z.array(z.string()).optional().describe("Command-line arguments"),
      cwd: z.string().optional().describe("Working directory"),
      env: z.record(z.string(), z.string()).optional().describe("Environment variables"),
      stopAtEntry: z.boolean().optional().describe("Stop at the entry point"),
    },
    async (params) => {
      const client = await clientManager.ensureClient();
      const waitPromise = waitForStopOrExit(client);
      const result = await client.launch(params);
      const event = await waitPromise;

      if (event.type === "stopped") {
        const { stackFrames } = await client.getStackTrace({ threadId: event.params.threadId });
        const topFrame = stackFrames.length > 0 ? stackFrames[0] : null;
        return {
          content: [{ type: "text" as const, text: formatStoppedResponse(event.params, topFrame) }],
        };
      }

      // Process ran to completion without stopping
      await clientManager.dispose();
      return {
        content: [{ type: "text" as const, text: formatExitedResponse(event.params) }],
      };
    }
  );

  server.tool(
    "attach",
    "Attach the debugger to a running .NET process.",
    {
      processId: z.coerce.number().describe("Process ID to attach to"),
    },
    async (params) => {
      const client = await clientManager.ensureClient();
      const result = await client.attach(params);
      return {
        content: [{ type: "text" as const, text: `Attached to process ${result.processId}` }],
      };
    }
  );

  server.tool(
    "detach",
    "Detach the debugger from the process without killing it.",
    async () => {
      const client = clientManager.getClient();
      await client.detach();
      await clientManager.dispose();
      return {
        content: [{ type: "text" as const, text: "Detached from process" }],
      };
    }
  );

  server.tool(
    "terminate",
    "Terminate the debugged process.",
    async () => {
      const client = clientManager.getClient();
      await client.terminate();
      await clientManager.dispose();
      return {
        content: [{ type: "text" as const, text: "Process terminated" }],
      };
    }
  );
}
