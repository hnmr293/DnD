namespace DnD.Core.Tests;

using DnD.Protocol;

public class StubDebuggerEngineTests
{
    private readonly StubDebuggerEngine _engine = new();

    [Fact]
    public async Task Launch_ReturnsProcessId()
    {
        var result = await _engine.LaunchAsync(new LaunchRequest(Program: "test.exe"));
        Assert.Equal(12345, result.ProcessId);
    }

    [Fact]
    public async Task SetBreakpoint_ReturnsVerifiedBreakpoint()
    {
        var result = await _engine.SetBreakpointAsync(
            new SetBreakpointRequest(File: "Program.cs", Line: 10));
        Assert.True(result.Breakpoint.Verified);
        Assert.Equal("Program.cs", result.Breakpoint.File);
        Assert.Equal(10, result.Breakpoint.Line);
    }

    [Fact]
    public async Task SetBreakpoint_IncrementsId()
    {
        var r1 = await _engine.SetBreakpointAsync(new SetBreakpointRequest(File: "a.cs", Line: 1));
        var r2 = await _engine.SetBreakpointAsync(new SetBreakpointRequest(File: "b.cs", Line: 2));
        Assert.NotEqual(r1.Breakpoint.Id, r2.Breakpoint.Id);
    }

    [Fact]
    public async Task Terminate_FiresExitedEvent()
    {
        ExitedNotification? received = null;
        _engine.Exited += (_, e) => received = e.Notification;

        await _engine.TerminateAsync();

        Assert.NotNull(received);
        Assert.Equal(0, received!.ExitCode);
    }

    [Fact]
    public async Task GetStackTrace_ReturnsStubFrame()
    {
        var result = await _engine.GetStackTraceAsync(new GetStackTraceRequest());
        Assert.Single(result.StackFrames);
        Assert.Equal("Main", result.StackFrames[0].Name);
    }

    [Fact]
    public async Task Evaluate_ReturnsStubResult()
    {
        var result = await _engine.EvaluateAsync(new EvaluateRequest(Expression: "x + 1"));
        Assert.Equal("stub:x + 1", result.Result);
    }
}
