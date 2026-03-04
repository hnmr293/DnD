#!/usr/bin/env node

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { ClientManager } from "./client-manager.js";
import { registerProcessTools } from "./tools/process.js";
import { registerExecutionTools } from "./tools/execution.js";
import { registerBreakpointTools } from "./tools/breakpoints.js";
import { registerInspectionTools } from "./tools/inspection.js";

const server = new McpServer({
  name: "dnd-debugger",
  version: "0.1.0",
});

const clientManager = new ClientManager();

registerProcessTools(server, clientManager);
registerExecutionTools(server, clientManager);
registerBreakpointTools(server, clientManager);
registerInspectionTools(server, clientManager);

const transport = new StdioServerTransport();
await server.connect(transport);

// Cleanup on process exit
process.on("SIGINT", async () => {
  await clientManager.dispose();
  await server.close();
  process.exit(0);
});

process.on("SIGTERM", async () => {
  await clientManager.dispose();
  await server.close();
  process.exit(0);
});
