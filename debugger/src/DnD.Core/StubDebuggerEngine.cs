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
    public event EventHandler<OutputEventArgs>? Output;

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

    public Task ContinueAsync(ContinueRequest request) => Task.CompletedTask;
    public Task StepInAsync(StepInRequest request) => Task.CompletedTask;
    public Task StepOverAsync(StepOverRequest request) => Task.CompletedTask;
    public Task StepOutAsync(StepOutRequest request) => Task.CompletedTask;

    public Task<SetBreakpointResponse> SetBreakpointAsync(SetBreakpointRequest request)
        => Task.FromResult(new SetBreakpointResponse(
            new Breakpoint(_nextBreakpointId++, request.File, request.Line, Verified: true)));

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
}
