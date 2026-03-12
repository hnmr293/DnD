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
        const mod = frame.moduleId ? ` [${frame.moduleId.split(/[/\\]/).pop()}]` : "";
        return `  #${frame.id} ${frame.name}${loc}${mod}`;
      });
      return {
        content: [{ type: "text" as const, text: `Stack trace:\n${lines.join("\n")}` }],
      };
    }
  );

  server.tool(
    "getVariables",
    "Get variables for a given scope. Use variablesReference=0 for top-frame locals. To expand an object or array, pass the (ref: N) number shown in the output as variablesReference.",
    {
      variablesReference: z.coerce.number().describe("Variables reference (0 for top-frame locals)"),
    },
    async (params) => {
      const client = clientManager.getClient();
      const result = await client.getVariables(params);
      if (result.variables.length === 0) {
        return { content: [{ type: "text" as const, text: "No variables in the current scope" }] };
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
      const refStr = result.variablesReference ? ` (ref: ${result.variablesReference})` : "";
      return {
        content: [{ type: "text" as const, text: `${result.result}${typeStr}${refStr}` }],
      };
    }
  );
}
