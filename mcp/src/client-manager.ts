import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { DebuggerClient } from "./debugger-client.js";

const __dirname = dirname(fileURLToPath(import.meta.url));

function resolveHostPath(): string {
  // Environment variable takes priority
  if (process.env.DND_HOST_PATH) {
    return process.env.DND_HOST_PATH;
  }
  // Default: relative path from mcp/dist/ to debugger/src/DnD.Host/bin/Debug/net8.0-windows/DnD.Host.dll
  return resolve(__dirname, "../../debugger/src/DnD.Host/bin/Debug/net8.0-windows/DnD.Host.dll");
}

export class ClientManager {
  private client: DebuggerClient | null = null;
  private hostPath: string;
  private stubMode: boolean;

  constructor(options?: { hostPath?: string; stub?: boolean }) {
    this.hostPath = options?.hostPath ?? resolveHostPath();
    this.stubMode = options?.stub ?? false;
  }

  async ensureClient(): Promise<DebuggerClient> {
    if (this.client?.isConnected) {
      return this.client;
    }

    const args = this.stubMode ? ["--stub"] : [];
    this.client = new DebuggerClient(this.hostPath, args);
    await this.client.start();
    return this.client;
  }

  getClient(): DebuggerClient {
    if (!this.client?.isConnected) {
      throw new Error("No active debugger connection. Call launch or attach first.");
    }
    return this.client;
  }

  async dispose(): Promise<void> {
    if (this.client) {
      await this.client.dispose();
      this.client = null;
    }
  }
}
