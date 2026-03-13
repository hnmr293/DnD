namespace DnD.Host.Tests;

using DnD.Protocol;
using StreamJsonRpc;

[Collection("DebugSession")]
[Trait("Category", "DebugSession")]
public class ExitCodeTests : DebugTestBase
{
    [Fact]
    public async Task ExitCode_NormalExit_Reports0()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exited.ExitCode);
    }

    [Fact]
    public async Task ExitCode_Return42_Reports42()
    {
        var program = FindFixture("ExitCodeTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["42"]));

        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(42, exited.ExitCode);
    }

    [Fact]
    public async Task ExitCode_Return1_Reports1()
    {
        var program = FindFixture("ExitCodeTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["1"]));

        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, exited.ExitCode);
    }

    [Fact]
    public async Task ExitCode_Negative1_ReportsNegative1()
    {
        var program = FindFixture("ExitCodeTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["-1"]));

        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(-1, exited.ExitCode);
    }

    [Fact]
    public async Task ExitCode_255_Reports255()
    {
        var program = FindFixture("ExitCodeTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["255"]));

        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(255, exited.ExitCode);
    }

    [Fact]
    public async Task ExitCode_UnhandledException_ReportsNonZero()
    {
        var program = FindFixture("ExceptionTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["null-ref"]));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Exception, stopped.Reason);

        await Rpc!.InvokeWithParameterObjectAsync<object>(
            "continue", new ContinueRequest(ThreadId: stopped.ThreadId));

        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, exited.ExitCode);
    }

    [Fact]
    public async Task ExitCode_Terminate_Reports0()
    {
        var program = FindFixture("VariablesTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        // Wait for the Debugger.Break() stop
        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Pause, stopped.Reason);

        await Rpc!.InvokeAsync("terminate");
        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exited.ExitCode);
    }

    [Fact]
    public async Task ExitCode_TerminateBeforeLaunch_Reports0()
    {
        await Rpc!.InvokeAsync("terminate");
        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exited.ExitCode);
    }
}
