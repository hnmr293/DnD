namespace DnD.Host;

using DnD.Core;
using DnD.Protocol;
using StreamJsonRpc;

public class DebuggerRpcTarget
{
    private readonly IDebuggerEngine _engine;
    private readonly JsonRpc _rpc;

    public DebuggerRpcTarget(IDebuggerEngine engine, JsonRpc rpc)
    {
        _engine = engine;
        _rpc = rpc;

        _engine.Stopped += OnStopped;
        _engine.Exited += OnExited;
        _engine.Output += OnOutput;
    }

    // Process control

    [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
    public Task<LaunchResponse> Launch(LaunchRequest request)
        => _engine.LaunchAsync(request);

    [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
    public Task<AttachResponse> Attach(AttachRequest request)
        => _engine.AttachAsync(request);

    public Task Detach()
        => _engine.DetachAsync();

    public Task Terminate()
        => _engine.TerminateAsync();

    // Execution control

    [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
    public Task Continue(ContinueRequest request)
        => _engine.ContinueAsync(request);

    [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
    public Task StepIn(StepInRequest request)
        => _engine.StepInAsync(request);

    [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
    public Task StepOver(StepOverRequest request)
        => _engine.StepOverAsync(request);

    [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
    public Task StepOut(StepOutRequest request)
        => _engine.StepOutAsync(request);

    // Breakpoints

    [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
    public Task<SetBreakpointResponse> SetBreakpoint(SetBreakpointRequest request)
        => _engine.SetBreakpointAsync(request);

    [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
    public Task RemoveBreakpoint(RemoveBreakpointRequest request)
        => _engine.RemoveBreakpointAsync(request);

    public Task<GetBreakpointsResponse> GetBreakpoints()
        => _engine.GetBreakpointsAsync();

    // Inspection

    [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
    public Task<GetStackTraceResponse> GetStackTrace(GetStackTraceRequest request)
        => _engine.GetStackTraceAsync(request);

    [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
    public Task<GetVariablesResponse> GetVariables(GetVariablesRequest request)
        => _engine.GetVariablesAsync(request);

    [JsonRpcMethod(UseSingleObjectParameterDeserialization = true)]
    public Task<EvaluateResponse> Evaluate(EvaluateRequest request)
        => _engine.EvaluateAsync(request);

    // Notification relay

    private async void OnStopped(object? sender, StoppedEventArgs e)
    {
        try { await _rpc.NotifyWithParameterObjectAsync("stopped", e.Notification); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to send stopped notification: {ex.Message}"); }
    }

    private async void OnExited(object? sender, ExitedEventArgs e)
    {
        try { await _rpc.NotifyWithParameterObjectAsync("exited", e.Notification); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to send exited notification: {ex.Message}"); }
    }

    private async void OnOutput(object? sender, OutputEventArgs e)
    {
        try { await _rpc.NotifyWithParameterObjectAsync("output", e.Notification); }
        catch (Exception ex) { Console.Error.WriteLine($"Failed to send output notification: {ex.Message}"); }
    }
}
