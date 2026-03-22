import { resolve, dirname, join } from "node:path";
import { tmpdir } from "node:os";
import {
  createWriteStream,
  existsSync,
  unlinkSync,
  type WriteStream,
} from "node:fs";
import { randomBytes } from "node:crypto";
import { fileURLToPath } from "node:url";
import { DebuggerClient } from "./debugger-client.js";
import type { StoppedParams, ExitedParams } from "./types/protocol.js";

const __dirname = dirname(fileURLToPath(import.meta.url));

function resolveHostPath(): string {
  // 1. Environment variable (development override)
  if (process.env.DND_HOST_PATH) {
    return process.env.DND_HOST_PATH;
  }
  // 2. Bundled binary (npm package)
  const bundledPath = resolve(__dirname, "../bin/DnD.Host.dll");
  if (existsSync(bundledPath)) {
    return bundledPath;
  }
  throw new Error(
    "DnD.Host.dll not found. Set DND_HOST_PATH environment variable or install the package with bundled binaries.",
  );
}

export type DebuggerState = "not-started" | "running" | "stopped" | "exited";

export interface StateSnapshot {
  state: DebuggerState;
  stopReason?: string;
  stopDescription?: string;
  threadId?: number;
  exitCode?: number;
  outputFile?: string;
}

export class ClientManager {
  private client: DebuggerClient | null = null;
  private hostPath: string;
  private stubMode: boolean;

  // Output file (D1)
  private outputFilePath: string | null = null;
  private outputStream: WriteStream | null = null;

  // State tracking (F1)
  private _state: DebuggerState = "not-started";
  private _lastStopped: StoppedParams | null = null;
  private _lastExitCode: number | null = null;

  constructor(options?: { hostPath?: string; stub?: boolean }) {
    this.hostPath = options?.hostPath ?? resolveHostPath();
    this.stubMode = options?.stub ?? false;
  }

  get state(): DebuggerState {
    return this._state;
  }

  get outputFile(): string | null {
    return this.outputFilePath;
  }

  getStateSnapshot(): StateSnapshot {
    const snap: StateSnapshot = { state: this._state };
    if (this._state === "stopped" && this._lastStopped) {
      snap.stopReason = this._lastStopped.reason;
      snap.stopDescription = this._lastStopped.description;
      snap.threadId = this._lastStopped.threadId;
    }
    if (this._state === "exited" && this._lastExitCode != null) {
      snap.exitCode = this._lastExitCode;
    }
    if (this.outputFilePath) {
      snap.outputFile = this.outputFilePath;
    }
    return snap;
  }

  async ensureClient(): Promise<DebuggerClient> {
    if (this.client?.isConnected) {
      return this.client;
    }

    // Clean up previous output file from last session
    this.cleanupOutputFile();

    const args = this.stubMode ? ["--stub"] : [];
    this.client = new DebuggerClient(this.hostPath, args);
    await this.client.start();

    // Create output file
    const id = randomBytes(4).toString("hex");
    this.outputFilePath = join(tmpdir(), `dnd-output-${id}.log`);
    this.outputStream = createWriteStream(this.outputFilePath, { flags: "a" });

    // Wire events for output file and state tracking
    this.client.on("output", (params) => {
      if (this.outputStream) {
        this.outputStream.write(params.output);
      }
    });

    this.client.on("stopped", (params: StoppedParams) => {
      this._state = "stopped";
      this._lastStopped = params;
    });

    this.client.on("exited", (params: ExitedParams) => {
      this._state = "exited";
      this._lastExitCode = params.exitCode;
    });

    this._state = "not-started";
    this._lastStopped = null;
    this._lastExitCode = null;

    return this.client;
  }

  /** Mark state as running (called after launch/attach/continue/step) */
  markRunning(): void {
    this._state = "running";
  }

  getClient(): DebuggerClient {
    if (!this.client?.isConnected) {
      throw new Error(
        "No active debugger connection. Call launch or attach first.",
      );
    }
    return this.client;
  }

  async dispose(): Promise<void> {
    // Close output stream but keep the file for LLM to read later
    if (this.outputStream) {
      this.outputStream.end();
      this.outputStream = null;
    }

    if (this.client) {
      await this.client.dispose();
      this.client = null;
    }

    // Keep outputFilePath and state so getState/Read can still access them after exit
  }

  /** Clean up output file — called when starting a new session or on shutdown */
  private cleanupOutputFile(): void {
    if (this.outputFilePath) {
      try {
        unlinkSync(this.outputFilePath);
      } catch {
        /* ignore */
      }
      this.outputFilePath = null;
    }
    this._state = "not-started";
    this._lastStopped = null;
    this._lastExitCode = null;
  }

  /** Full cleanup including output file — for MCP server shutdown */
  async shutdown(): Promise<void> {
    await this.dispose();
    this.cleanupOutputFile();
  }
}
