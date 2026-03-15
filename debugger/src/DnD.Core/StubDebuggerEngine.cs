namespace DnD.Core;

using DnD.Protocol;

/// <summary>
/// Stub implementation returning hardcoded responses.
/// Used for Phase 1 integration testing of the JSON-RPC transport layer.
/// </summary>
public class StubDebuggerEngine : IDebuggerEngine
{
    private int _nextBreakpointId = 1;

    public event EventHandler<StoppedEventArgs>? Stopped;
    public event EventHandler<ExitedEventArgs>? Exited;
    // Reserved for future use
#pragma warning disable CS0067
    public event EventHandler<OutputEventArgs>? Output;
#pragma warning restore CS0067

    public Task<LaunchResponse> LaunchAsync(LaunchRequest request)
        => Task.FromResult(new LaunchResponse(ProcessId: 12345));

    public Task<AttachResponse> AttachAsync(AttachRequest request)
        => Task.FromResult(new AttachResponse(ProcessId: request.ProcessId));

    public Task DetachAsync() => Task.CompletedTask;

    public Task TerminateAsync()
    {
        Exited?.Invoke(this, new ExitedEventArgs(new ExitedNotification(ExitCode: 0)));
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        FireStoppedAfterDelay(StopReason.Pause);
        return Task.CompletedTask;
    }

    public Task ContinueAsync(ContinueRequest request)
    {
        FireStoppedAfterDelay(StopReason.Breakpoint);
        return Task.CompletedTask;
    }

    public Task StepInAsync(StepInRequest request)
    {
        FireStoppedAfterDelay(StopReason.Step);
        return Task.CompletedTask;
    }

    public Task StepOverAsync(StepOverRequest request)
    {
        FireStoppedAfterDelay(StopReason.Step);
        return Task.CompletedTask;
    }

    public Task StepOutAsync(StepOutRequest request)
    {
        FireStoppedAfterDelay(StopReason.Step);
        return Task.CompletedTask;
    }

    private void FireStoppedAfterDelay(StopReason reason)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(50);
            Stopped?.Invoke(this, new StoppedEventArgs(
                new StoppedNotification(reason, 1, "Stub stopped")));
        });
    }

    public Task<SetBreakpointResponse> SetBreakpointAsync(SetBreakpointRequest request)
        => Task.FromResult(new SetBreakpointResponse(
            new Breakpoint(_nextBreakpointId++, request.File, request.Line, Verified: true,
                Condition: request.Condition, HitCount: request.HitCount)));

    public Task<SetExceptionBreakpointsResponse> SetExceptionBreakpointsAsync(
        SetExceptionBreakpointsRequest request)
        => Task.FromResult(new SetExceptionBreakpointsResponse(
            request.Thrown, request.Uncaught, request.Types));

    public Task RemoveBreakpointAsync(RemoveBreakpointRequest request)
        => Task.CompletedTask;

    public Task<GetBreakpointsResponse> GetBreakpointsAsync()
        => Task.FromResult(new GetBreakpointsResponse(Breakpoints: []));

    public Task<GetStackTraceResponse> GetStackTraceAsync(GetStackTraceRequest request)
        => Task.FromResult(new GetStackTraceResponse(StackFrames: [
            new StackFrame(Id: 0, Name: "Main", File: "Program.cs", Line: 10)
        ]));

    public Task<GetVariablesResponse> GetVariablesAsync(GetVariablesRequest request)
        => Task.FromResult(new GetVariablesResponse(Variables: [
            new Variable(Name: "x", Value: "42", VariablesReference: 0, Type: "int")
        ]));

    public Task<EvaluateResponse> EvaluateAsync(EvaluateRequest request)
        => Task.FromResult(new EvaluateResponse(
            Result: $"stub:{request.Expression}", VariablesReference: 0, Type: "string"));

    public Task<GetThreadsResponse> GetThreadsAsync()
        => Task.FromResult(new GetThreadsResponse(Threads: [
            new ThreadInfo(Id: 1, Current: true)
        ]));

    public Task<GetExceptionResponse> GetExceptionAsync(GetExceptionRequest request)
        => Task.FromResult(new GetExceptionResponse(
            Type: "System.Exception", Message: "Stub exception"));
}
