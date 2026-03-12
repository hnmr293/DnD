import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { ClientManager } from "../client-manager.js";

export function registerBreakpointTools(server: McpServer, clientManager: ClientManager) {
  server.tool(
    "setBreakpoint",
    "Set a breakpoint at a specific file and line. Can be called before or after launching.",
    {
      file: z.string().describe("Absolute path to the source file"),
      line: z.coerce.number().describe("Line number (1-based)"),
    },
    async (params) => {
      const client = await clientManager.ensureClient();
      const result = await client.setBreakpoint(params);
      const bp = result.breakpoint;
      return {
        content: [{
          type: "text" as const,
          text: `Breakpoint ${bp.id} set at ${bp.file}:${bp.line} (verified: ${bp.verified})`,
        }],
      };
    }
  );

  server.tool(
    "removeBreakpoint",
    "Remove a previously set breakpoint by its ID.",
    {
      breakpointId: z.coerce.number().describe("Breakpoint ID to remove"),
    },
    async (params) => {
      const client = clientManager.getClient();
      await client.removeBreakpoint(params);
      return {
        content: [{ type: "text" as const, text: `Breakpoint ${params.breakpointId} removed` }],
      };
    }
  );

  server.tool(
    "getBreakpoints",
    "List all currently set breakpoints.",
    async () => {
      const client = clientManager.getClient();
      const result = await client.getBreakpoints();
      if (result.breakpoints.length === 0) {
        return { content: [{ type: "text" as const, text: "No breakpoints set" }] };
      }
      const lines = result.breakpoints.map(
        (bp) => `  #${bp.id} ${bp.file}:${bp.line} (verified: ${bp.verified})`
      );
      return {
        content: [{ type: "text" as const, text: `Breakpoints:\n${lines.join("\n")}` }],
      };
    }
  );
}
