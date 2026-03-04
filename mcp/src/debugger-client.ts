import { EventEmitter } from "node:events";
import { spawn, type ChildProcess } from "node:child_process";
import * as rpc from "vscode-jsonrpc/node.js";
import {
  LaunchRequest, AttachRequest, DetachRequest, TerminateRequest,
  ContinueRequest, StepInRequest, StepOverRequest, StepOutRequest,
  SetBreakpointRequest, RemoveBreakpointRequest, GetBreakpointsRequest,
  GetStackTraceRequest, GetVariablesRequest, EvaluateRequest,
  StoppedNotification, ExitedNotification, OutputNotification,
} from "./types/rpc-methods.js";
import type {
  LaunchParams, LaunchResult,
  AttachParams, AttachResult,
  ContinueParams, StepInParams, StepOverParams, StepOutParams,
  SetBreakpointParams, SetBreakpointResult,
  RemoveBreakpointParams,
  GetBreakpointsResult,
  GetStackTraceParams, GetStackTraceResult,
  GetVariablesParams, GetVariablesResult,
  EvaluateParams, EvaluateResult,
  StoppedParams, ExitedParams, OutputParams,
} from "./types/protocol.js";

export interface DebuggerClientEvents {
  stopped: [StoppedParams];
  exited: [ExitedParams];
  output: [OutputParams];
  error: [Error];
}

export class DebuggerClient extends EventEmitter<DebuggerClientEvents> {
  private process: ChildProcess | null = null;
  private connection: rpc.MessageConnection | null = null;
  private readonly hostPath: string;
  private readonly hostArgs: string[];

  constructor(hostPath: string, hostArgs: string[] = []) {
    super();
    this.hostPath = hostPath;
    this.hostArgs = hostArgs;
  }

  async start(): Promise<void> {
    this.process = spawn("dotnet", [this.hostPath, ...this.hostArgs], {
      stdio: ["pipe", "pipe", "pipe"],
    });

    this.process.stderr?.on("data", (data: Buffer) => {
      // DnD.Host writes diagnostic messages to stderr
      process.stderr.write(`[DnD.Host] ${data.toString()}`);
    });

    this.process.on("error", (err) => {
      this.emit("error", err);
    });

    this.process.on("exit", (code) => {
      this.connection = null;
      this.process = null;
    });

    const reader = new rpc.StreamMessageReader(this.process.stdout!);
    const writer = new rpc.StreamMessageWriter(this.process.stdin!);
    this.connection = rpc.createMessageConnection(reader, writer);

    // Wire notifications
    this.connection.onNotification(StoppedNotification, (params) => {
      this.emit("stopped", params);
    });
    this.connection.onNotification(ExitedNotification, (params) => {
      this.emit("exited", params);
    });
    this.connection.onNotification(OutputNotification, (params) => {
      this.emit("output", params);
    });

    this.connection.listen();
  }

  // Process control

  async launch(params: LaunchParams): Promise<LaunchResult> {
    return this.sendRequest(LaunchRequest, params);
  }

  async attach(params: AttachParams): Promise<AttachResult> {
    return this.sendRequest(AttachRequest, params);
  }

  async detach(): Promise<void> {
    return this.sendRequest(DetachRequest);
  }

  async terminate(): Promise<void> {
    return this.sendRequest(TerminateRequest);
  }

  // Execution control

  async continue(params: ContinueParams): Promise<void> {
    return this.sendRequest(ContinueRequest, params);
  }

  async stepIn(params: StepInParams): Promise<void> {
    return this.sendRequest(StepInRequest, params);
  }

  async stepOver(params: StepOverParams): Promise<void> {
    return this.sendRequest(StepOverRequest, params);
  }

  async stepOut(params: StepOutParams): Promise<void> {
    return this.sendRequest(StepOutRequest, params);
  }

  // Breakpoints

  async setBreakpoint(params: SetBreakpointParams): Promise<SetBreakpointResult> {
    return this.sendRequest(SetBreakpointRequest, params);
  }

  async removeBreakpoint(params: RemoveBreakpointParams): Promise<void> {
    return this.sendRequest(RemoveBreakpointRequest, params);
  }

  async getBreakpoints(): Promise<GetBreakpointsResult> {
    return this.sendRequest(GetBreakpointsRequest);
  }

  // Inspection

  async getStackTrace(params: GetStackTraceParams): Promise<GetStackTraceResult> {
    return this.sendRequest(GetStackTraceRequest, params);
  }

  async getVariables(params: GetVariablesParams): Promise<GetVariablesResult> {
    return this.sendRequest(GetVariablesRequest, params);
  }

  async evaluate(params: EvaluateParams): Promise<EvaluateResult> {
    return this.sendRequest(EvaluateRequest, params);
  }

  // Lifecycle

  async dispose(): Promise<void> {
    if (this.connection) {
      this.connection.dispose();
      this.connection = null;
    }
    if (this.process) {
      this.process.kill();
      this.process = null;
    }
  }

  get isConnected(): boolean {
    return this.connection !== null && this.process !== null;
  }

  // Private helpers

  private sendRequest<P, R, E>(type: rpc.RequestType<P, R, E>, params: P): Promise<R>;
  private sendRequest<R, E>(type: rpc.RequestType0<R, E>): Promise<R>;
  private sendRequest(type: any, params?: any): Promise<any> {
    if (!this.connection) {
      throw new Error("Not connected to DnD.Host");
    }
    if (params !== undefined) {
      return this.connection.sendRequest(type, params);
    }
    return this.connection.sendRequest(type);
  }
}
