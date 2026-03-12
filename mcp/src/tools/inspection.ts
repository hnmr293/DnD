import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { ClientManager } from "../client-manager.js";

export function registerInspectionTools(server: McpServer, clientManager: ClientManager) {
  server.tool(
    "getStackTrace",
    "Get the current call stack. The process must be stopped at a breakpoint or step.",
    {
      threadId: z.coerce.number().optional().describe("Thread ID (default: stopped thread)"),
    },
    async (params) => {
      const client = clientManager.getClient();
      const result = await client.getStackTrace(params);
      if (result.stackFrames.length === 0) {
        return { content: [{ type: "text" as const, text: "No stack frames" }] };
      }
      const lines = result.stackFrames.map((frame) => {
        const loc = frame.file ? ` at ${frame.file}:${frame.line ?? "?"}` : "";
        return `  #${frame.id} ${frame.name}${loc}`;
      });
      return {
        content: [{ type: "text" as const, text: `Stack trace:\n${lines.join("\n")}` }],
      };
    }
  );

  server.tool(
    "getVariables",
    "Get variables for a given scope. Use variablesReference=0 for top-frame locals, or a variablesReference from a previous response to expand objects.",
    {
      variablesReference: z.coerce.number().describe("Variables reference (0 for top-frame locals)"),
    },
    async (params) => {
      const client = clientManager.getClient();
      const result = await client.getVariables(params);
      if (result.variables.length === 0) {
        return { content: [{ type: "text" as const, text: "No variables" }] };
      }
      const lines = result.variables.map(
        (v) => `  ${v.name}: ${v.type ?? ""} = ${v.value}${v.variablesReference ? ` (ref: ${v.variablesReference})` : ""}`
      );
      return {
        content: [{ type: "text" as const, text: `Variables:\n${lines.join("\n")}` }],
      };
    }
  );

  server.tool(
    "evaluate",
    "Evaluate an expression in the context of the current stopped frame. Supports variable names, property access, method calls, and arithmetic.",
    {
      expression: z.string().describe("Expression to evaluate (e.g., 'x', 'obj.Name', 'a + b')"),
      frameId: z.coerce.number().optional().describe("Stack frame ID (default: top frame)"),
    },
    async (params) => {
      const client = clientManager.getClient();
      const result = await client.evaluate(params);
      const typeStr = result.type ? ` (${result.type})` : "";
      return {
        content: [{ type: "text" as const, text: `${result.result}${typeStr}` }],
      };
    }
  );
}
