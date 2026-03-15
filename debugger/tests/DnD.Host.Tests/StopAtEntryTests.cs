namespace DnD.Host.Tests;

using DnD.Protocol;
using StreamJsonRpc;

[Collection("DebugSession")]
[Trait("Category", "DebugSession")]
public class StopAtEntryTests : DebugTestBase
{
    [Fact]
    public async Task StopAtEntry_True_StopsBeforeUserCode()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, StopAtEntry: true));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Entry, stopped.Reason);
        Assert.True(stopped.ThreadId > 0);
    }

    [Fact]
    public async Task StopAtEntry_False_RunsNormally()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, StopAtEntry: false));

        // Should not get a stopped event — program should just exit
        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exited.ExitCode);

        // Verify no stopped events were queued
        Assert.True(StoppedQueue.Count == 0, "Expected no stopped events when stopAtEntry is false");
    }

    [Fact]
    public async Task StopAtEntry_Default_RunsNormally()
    {
        var program = FindFixture("HelloWorld");
        // Default: stopAtEntry omitted (defaults to false)
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exited.ExitCode);

        Assert.True(StoppedQueue.Count == 0, "Expected no stopped events when stopAtEntry defaults to false");
    }

    [Fact]
    public async Task StopAtEntry_ContinueRunsToCompletion()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, StopAtEntry: true));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Entry, stopped.Reason);

        await Rpc!.InvokeWithParameterObjectAsync<object>(
            "continue", new ContinueRequest(ThreadId: stopped.ThreadId));

        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exited.ExitCode);
    }

    [Fact]
    public async Task StopAtEntry_CanInspectStackTrace()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, StopAtEntry: true));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Entry, stopped.Reason);

        var stack = await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));

        Assert.NotEmpty(stack.StackFrames);
        Assert.NotNull(stack.StackFrames[0].File);
        Assert.NotNull(stack.StackFrames[0].Line);
    }

    [Fact]
    public async Task StopAtEntry_CanStepOver()
    {
        // Use BreakpointTest (multi-line) so stepping can advance
        var program = FindFixture("BreakpointTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, StopAtEntry: true));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Entry, stopped.Reason);

        var stack1 = await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));
        var line1 = stack1.StackFrames[0].Line;

        await Rpc!.InvokeWithParameterObjectAsync<object>(
            "stepOver", new StepOverRequest(ThreadId: stopped.ThreadId));

        var stopped2 = WaitForStopped(TimeSpan.FromSeconds(10));
        Assert.Equal(StopReason.Step, stopped2.Reason);

        var stack2 = await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped2.ThreadId));
        var line2 = stack2.StackFrames[0].Line;

        Assert.NotEqual(line1, line2);
    }

    [Fact]
    public async Task StopAtEntry_WithBreakpoint_EntryStopsFirst()
    {
        var program = FindFixture("BreakpointTest");
        var sourceFile = FindFixtureSrc("BreakpointTest", "Program.cs");

        await Rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: sourceFile, Line: 7));

        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, StopAtEntry: true));

        // First stop should be Entry
        var stopped1 = WaitForStopped();
        Assert.Equal(StopReason.Entry, stopped1.Reason);

        // Continue to breakpoint
        await Rpc!.InvokeWithParameterObjectAsync<object>(
            "continue", new ContinueRequest(ThreadId: stopped1.ThreadId));

        var stopped2 = WaitForStopped();
        Assert.Equal(StopReason.Breakpoint, stopped2.Reason);
    }

    [Fact]
    public async Task StopAtEntry_StepIn_Works()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, StopAtEntry: true));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Entry, stopped.Reason);

        await Rpc!.InvokeWithParameterObjectAsync<object>(
            "stepIn", new StepInRequest(ThreadId: stopped.ThreadId));

        var stopped2 = WaitForStopped(TimeSpan.FromSeconds(10));
        Assert.Equal(StopReason.Step, stopped2.Reason);
    }

    [Fact]
    public async Task StopAtEntry_TerminateWhileStopped()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, StopAtEntry: true));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Entry, stopped.Reason);

        await Rpc!.InvokeAsync("terminate");
        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exited.ExitCode);
    }

    [Fact]
    public async Task StopAtEntry_DescriptionNotNull()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, StopAtEntry: true));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Entry, stopped.Reason);
        Assert.NotNull(stopped.Description);
    }

    [Fact]
    public async Task StopAtEntry_ThreadIdConsistent()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, StopAtEntry: true));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Entry, stopped.Reason);

        // Use the thread ID from the stopped event to get a stack trace
        var stack = await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));

        Assert.NotEmpty(stack.StackFrames);
    }
}
