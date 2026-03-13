namespace DnD.Core;

using DnD.Protocol;

public class StoppedEventArgs(StoppedNotification notification) : EventArgs
{
    public StoppedNotification Notification { get; } = notification;
}

public class ExitedEventArgs(ExitedNotification notification) : EventArgs
{
    public ExitedNotification Notification { get; } = notification;
}

public class OutputEventArgs(OutputNotification notification) : EventArgs
{
    public OutputNotification Notification { get; } = notification;
}

public interface IDebuggerEngine
{
    // Process control
    Task<LaunchResponse> LaunchAsync(LaunchRequest request);
    Task<AttachResponse> AttachAsync(AttachRequest request);
    Task DetachAsync();
    Task TerminateAsync();

    // Execution control
    Task ContinueAsync(ContinueRequest request);
    Task PauseAsync();
    Task StepInAsync(StepInRequest request);
    Task StepOverAsync(StepOverRequest request);
    Task StepOutAsync(StepOutRequest request);

    // Breakpoints
    Task<SetBreakpointResponse> SetBreakpointAsync(SetBreakpointRequest request);
    Task RemoveBreakpointAsync(RemoveBreakpointRequest request);
    Task<GetBreakpointsResponse> GetBreakpointsAsync();

    // Inspection
    Task<GetStackTraceResponse> GetStackTraceAsync(GetStackTraceRequest request);
    Task<GetVariablesResponse> GetVariablesAsync(GetVariablesRequest request);
    Task<EvaluateResponse> EvaluateAsync(EvaluateRequest request);
    Task<GetThreadsResponse> GetThreadsAsync();
    Task<GetExceptionResponse> GetExceptionAsync(GetExceptionRequest request);

    // Events
    event EventHandler<StoppedEventArgs>? Stopped;
    event EventHandler<ExitedEventArgs>? Exited;
    event EventHandler<OutputEventArgs>? Output;
}
