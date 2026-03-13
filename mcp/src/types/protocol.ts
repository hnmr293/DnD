// === Shared Models ===

export interface Breakpoint {
  id: number;
  file: string;
  line: number;
  verified: boolean;
}

export interface StackFrame {
  id: number;
  name: string;
  file?: string | null;
  line?: number | null;
  column?: number | null;
  moduleId?: string | null;
}

export interface Variable {
  name: string;
  value: string;
  type?: string | null;
  variablesReference: number;
}

export interface ThreadInfo {
  id: number;
  name?: string | null;
  current: boolean;
}

export interface ExceptionInfo {
  type: string;
  message?: string | null;
}

export type StopReason = "breakpoint" | "step" | "pause" | "exception" | "entry";
export type OutputCategory = "stdout" | "stderr" | "console";

// === Request Params ===

export interface LaunchParams {
  program: string;
  args?: string[];
  cwd?: string;
  env?: Record<string, string>;
  stopAtEntry?: boolean;
}

export interface AttachParams {
  processId: number;
}

export interface ContinueParams {
  threadId?: number;
}

export interface StepInParams {
  threadId?: number;
}

export interface StepOverParams {
  threadId?: number;
}

export interface StepOutParams {
  threadId?: number;
}

export interface SetBreakpointParams {
  file: string;
  line: number;
}

export interface RemoveBreakpointParams {
  breakpointId: number;
}

export interface GetStackTraceParams {
  threadId?: number;
}

export interface GetVariablesParams {
  variablesReference: number;
}

export interface EvaluateParams {
  expression: string;
  frameId?: number;
}

export interface GetExceptionParams {
  threadId?: number;
}

// === Response Results ===

export interface LaunchResult {
  processId: number;
}

export interface AttachResult {
  processId: number;
}

export interface SetBreakpointResult {
  breakpoint: Breakpoint;
}

export interface GetBreakpointsResult {
  breakpoints: Breakpoint[];
}

export interface GetStackTraceResult {
  stackFrames: StackFrame[];
}

export interface GetVariablesResult {
  variables: Variable[];
}

export interface EvaluateResult {
  result: string;
  type?: string | null;
  variablesReference: number;
}

export interface GetThreadsResult {
  threads: ThreadInfo[];
}

export interface GetExceptionResult {
  type: string;
  message?: string | null;
  stackTrace?: string | null;
  innerException?: ExceptionInfo | null;
}

// === Notification Params ===

export interface StoppedParams {
  reason: StopReason;
  threadId: number;
  description?: string;
  breakpointId?: number;
}

export interface ExitedParams {
  exitCode: number;
}

export interface OutputParams {
  category: OutputCategory;
  output: string;
}
