import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import type { ClientManager } from "../client-manager.js";

export function registerBreakpointTools(
  server: McpServer,
  clientManager: ClientManager,
) {
  server.tool(
    "setBreakpoint",
    "Set a breakpoint at a specific file and line. Can be called before or after launching. Supports optional condition (expression that must evaluate to true) and hit count (stop only after N hits).",
    {
      file: z.string().describe("Absolute path to the source file"),
      line: z.coerce.number().describe("Line number (1-based)"),
      condition: z
        .string()
        .optional()
        .describe(
          "Condition expression — breakpoint only stops when this evaluates to true (e.g., 'x > 10')",
        ),
      hitCount: z.coerce
        .number()
        .optional()
        .describe(
          "Hit count — breakpoint only stops after being hit this many times",
        ),
    },
    async (params) => {
      const client = await clientManager.ensureClient();
      const result = await client.setBreakpoint(params);
      const bp = result.breakpoint;
      const status = bp.verified
        ? "verified"
        : "pending — will activate when code is loaded";
      const extras: string[] = [];
      if (bp.condition) extras.push(`condition: ${bp.condition}`);
      if (bp.hitCount) extras.push(`hitCount: ${bp.hitCount}`);
      const suffix = extras.length > 0 ? ` [${extras.join(", ")}]` : "";
      return {
        content: [
          {
            type: "text" as const,
            text: `Breakpoint ${bp.id} set at ${bp.file}:${bp.line} (${status})${suffix}`,
          },
        ],
      };
    },
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
        content: [
          {
            type: "text" as const,
            text: `Breakpoint ${params.breakpointId} removed`,
          },
        ],
      };
    },
  );

  server.tool(
    "getBreakpoints",
    "List all currently set breakpoints.",
    async () => {
      const client = clientManager.getClient();
      const result = await client.getBreakpoints();
      if (result.breakpoints.length === 0) {
        return {
          content: [{ type: "text" as const, text: "No breakpoints set" }],
        };
      }
      const lines = result.breakpoints.map((bp) => {
        const status = bp.verified ? "verified" : "pending";
        const extras: string[] = [];
        if (bp.condition) extras.push(`condition: ${bp.condition}`);
        if (bp.hitCount) extras.push(`hitCount: ${bp.hitCount}`);
        const suffix = extras.length > 0 ? ` [${extras.join(", ")}]` : "";
        return `  #${bp.id} ${bp.file}:${bp.line} (${status})${suffix}`;
      });
      return {
        content: [
          { type: "text" as const, text: `Breakpoints:\n${lines.join("\n")}` },
        ],
      };
    },
  );

  server.tool(
    "setExceptionBreakpoints",
    "Configure when to break on exceptions. By default, only uncaught (unhandled) exceptions cause a stop. Use this to also break when exceptions are first thrown (before any catch handler runs), or to filter by exception type.",
    {
      thrown: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Break when any exception is thrown (first-chance). Default: false",
        ),
      uncaught: z
        .boolean()
        .optional()
        .default(true)
        .describe("Break on unhandled exceptions. Default: true"),
      types: z
        .array(z.string())
        .optional()
        .describe(
          "Only break on exceptions whose type name contains one of these strings (e.g., ['ArgumentException', 'InvalidOperation']). Applies to both thrown and uncaught.",
        ),
    },
    async (params) => {
      const client = await clientManager.ensureClient();
      const result = await client.setExceptionBreakpoints(params);
      const parts: string[] = [];
      if (result.thrown) parts.push("thrown (first-chance)");
      if (result.uncaught) parts.push("uncaught (unhandled)");
      if (parts.length === 0) parts.push("none");
      let text = `Exception breakpoints: ${parts.join(", ")}`;
      if (result.types?.length) {
        text += `\nType filter: ${result.types.join(", ")}`;
      }
      return {
        content: [{ type: "text" as const, text }],
      };
    },
  );
}
