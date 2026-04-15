import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { ClientManager } from "../client-manager.js";

export function registerInspectionTools(
  server: McpServer,
  clientManager: ClientManager,
) {
  server.tool(
    "getThreads",
    "List all threads in the debugged process. Shows thread IDs and marks the current (stopped) thread.",
    async () => {
      const client = clientManager.getClient();
      const result = await client.getThreads();
      if (result.threads.length === 0) {
        return { content: [{ type: "text" as const, text: "No threads" }] };
      }
      const lines = result.threads.map((t) => {
        const marker = t.current ? " *" : "";
        const name = t.name ? ` "${t.name}"` : "";
        return `  Thread ${t.id}${name}${marker}`;
      });
      return {
        content: [
          {
            type: "text" as const,
            text: `Threads (* = current):\n${lines.join("\n")}`,
          },
        ],
      };
    },
  );

  server.tool(
    "getException",
    "Get the current exception when stopped at an exception. Returns the exception type, message, stack trace, and inner exception if present.",
    {
      threadId: z.coerce
        .number()
        .optional()
        .describe("Thread ID (default: stopped thread)"),
    },
    async (params) => {
      const client = clientManager.getClient();
      const result = await client.getException(params);
      let text = `${result.type}`;
      if (result.message) {
        text += `: ${result.message}`;
      }
      if (result.stackTrace) {
        text += `\n\nStack trace:\n${result.stackTrace}`;
      }
      if (result.innerException) {
        text += `\n\nInner exception: ${result.innerException.type}`;
        if (result.innerException.message) {
          text += `: ${result.innerException.message}`;
        }
      }
      return {
        content: [{ type: "text" as const, text }],
      };
    },
  );

  server.tool(
    "getStackTrace",
    "Get the current call stack. The process must be stopped at a breakpoint or step.",
    {
      threadId: z.coerce
        .number()
        .optional()
        .describe("Thread ID (default: stopped thread)"),
    },
    async (params) => {
      const client = clientManager.getClient();
      const result = await client.getStackTrace(params);
      if (result.stackFrames.length === 0) {
        return {
          content: [{ type: "text" as const, text: "No stack frames" }],
        };
      }
      const lines = result.stackFrames.map((frame) => {
        const loc = frame.file ? ` at ${frame.file}:${frame.line ?? "?"}` : "";
        const mod = frame.moduleId
          ? ` [${frame.moduleId.split(/[/\\]/).pop()}]`
          : "";
        return `  #${frame.id} ${frame.name}${loc}${mod}`;
      });
      return {
        content: [
          { type: "text" as const, text: `Stack trace:\n${lines.join("\n")}` },
        ],
      };
    },
  );

  server.tool(
    "getVariables",
    "Get variables for a given stack frame. Returns local variables and arguments.",
    {
      frameId: z.coerce
        .number()
        .optional()
        .describe("Stack frame ID (default: top frame)"),
    },
    async (params) => {
      const client = clientManager.getClient();
      const result = await client.getVariables(params);
      if (result.variables.length === 0) {
        return {
          content: [
            {
              type: "text" as const,
              text: "No variables in the current scope",
            },
          ],
        };
      }
      const lines = result.variables.map(
        (v) => `  ${v.name}: ${v.type ?? ""} = ${v.value}`,
      );
      return {
        content: [
          { type: "text" as const, text: `Variables:\n${lines.join("\n")}` },
        ],
      };
    },
  );

  server.tool(
    "evaluate",
    "Evaluate an expression in the context of the current stopped frame. Supports variable names, property access, method calls, and arithmetic.",
    {
      expression: z
        .string()
        .describe("Expression to evaluate (e.g., 'x', 'obj.Name', 'a + b')"),
      frameId: z.coerce
        .number()
        .optional()
        .describe("Stack frame ID (default: top frame)"),
    },
    async (params) => {
      const client = clientManager.getClient();
      const result = await client.evaluate(params);
      const typeStr = result.type ? ` (${result.type})` : "";
      return {
        content: [
          {
            type: "text" as const,
            text: `${result.result}${typeStr}`,
          },
        ],
      };
    },
  );
}
