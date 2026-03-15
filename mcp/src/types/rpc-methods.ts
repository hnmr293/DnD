import * as rpc from "vscode-jsonrpc/node.js";
import type {
  LaunchParams,
  LaunchResult,
  AttachParams,
  AttachResult,
  ContinueParams,
  StepInParams,
  StepOverParams,
  StepOutParams,
  SetBreakpointParams,
  SetBreakpointResult,
  RemoveBreakpointParams,
  GetBreakpointsResult,
  SetExceptionBreakpointsParams,
  SetExceptionBreakpointsResult,
  GetStackTraceParams,
  GetStackTraceResult,
  GetVariablesParams,
  GetVariablesResult,
  EvaluateParams,
  EvaluateResult,
  GetThreadsResult,
  GetExceptionParams,
  GetExceptionResult,
  StoppedParams,
  ExitedParams,
  OutputParams,
} from "./protocol.js";

// === Requests ===

export const LaunchRequest = new rpc.RequestType<
  LaunchParams,
  LaunchResult,
  void
>("launch");
export const AttachRequest = new rpc.RequestType<
  AttachParams,
  AttachResult,
  void
>("attach");
export const DetachRequest = new rpc.RequestType0<void, void>("detach");
export const TerminateRequest = new rpc.RequestType0<void, void>("terminate");

export const ContinueRequest = new rpc.RequestType<ContinueParams, void, void>(
  "continue",
);
export const PauseRequest = new rpc.RequestType0<void, void>("pause");
export const StepInRequest = new rpc.RequestType<StepInParams, void, void>(
  "stepIn",
);
export const StepOverRequest = new rpc.RequestType<StepOverParams, void, void>(
  "stepOver",
);
export const StepOutRequest = new rpc.RequestType<StepOutParams, void, void>(
  "stepOut",
);

export const SetBreakpointRequest = new rpc.RequestType<
  SetBreakpointParams,
  SetBreakpointResult,
  void
>("setBreakpoint");
export const RemoveBreakpointRequest = new rpc.RequestType<
  RemoveBreakpointParams,
  void,
  void
>("removeBreakpoint");
export const GetBreakpointsRequest = new rpc.RequestType0<
  GetBreakpointsResult,
  void
>("getBreakpoints");
export const SetExceptionBreakpointsRequest = new rpc.RequestType<
  SetExceptionBreakpointsParams,
  SetExceptionBreakpointsResult,
  void
>("setExceptionBreakpoints");

export const GetStackTraceRequest = new rpc.RequestType<
  GetStackTraceParams,
  GetStackTraceResult,
  void
>("getStackTrace");
export const GetVariablesRequest = new rpc.RequestType<
  GetVariablesParams,
  GetVariablesResult,
  void
>("getVariables");
export const EvaluateRequest = new rpc.RequestType<
  EvaluateParams,
  EvaluateResult,
  void
>("evaluate");
export const GetThreadsRequest = new rpc.RequestType0<GetThreadsResult, void>(
  "getThreads",
);
export const GetExceptionRequest = new rpc.RequestType<
  GetExceptionParams,
  GetExceptionResult,
  void
>("getException");

// === Notifications (server -> client) ===

export const StoppedNotification = new rpc.NotificationType<StoppedParams>(
  "stopped",
);
export const ExitedNotification = new rpc.NotificationType<ExitedParams>(
  "exited",
);
export const OutputNotification = new rpc.NotificationType<OutputParams>(
  "output",
);
