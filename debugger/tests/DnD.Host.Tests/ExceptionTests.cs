namespace DnD.Host.Tests;

using DnD.Protocol;
using StreamJsonRpc;

[Trait("Category", "DebugSession")]
public class ExceptionTests : DebugTestBase
{
    // =========================================================================
    // Thread ID validity
    // =========================================================================

    [Fact]
    public async Task Exception_NullRef_HasValidThreadId()
    {
        var program = FindFixture("ExceptionTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["null-ref"]));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Exception, stopped.Reason);
        Assert.True(stopped.ThreadId > 0, $"Expected valid thread ID, got {stopped.ThreadId}");
    }

    [Fact]
    public async Task Exception_Custom_HasValidThreadId()
    {
        var program = FindFixture("ExceptionTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["custom"]));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Exception, stopped.Reason);
        Assert.True(stopped.ThreadId > 0, $"Expected valid thread ID, got {stopped.ThreadId}");
    }

    [Fact]
    public async Task Exception_CanGetStackTraceAtException()
    {
        var program = FindFixture("ExceptionTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["null-ref"]));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Exception, stopped.Reason);

        var stack = await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));

        Assert.NotEmpty(stack.StackFrames);
    }

    [Fact]
    public async Task Exception_CanContinueAfterException()
    {
        var program = FindFixture("ExceptionTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["null-ref"]));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Exception, stopped.Reason);

        await Rpc!.InvokeWithParameterObjectAsync<object>(
            "continue", new ContinueRequest(ThreadId: stopped.ThreadId));

        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Process should exit (possibly with non-zero exit code)
        Assert.NotNull(exited);
    }

    [Fact]
    public async Task Exception_ThreadIdUsableForStackTrace()
    {
        var program = FindFixture("ExceptionTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["custom"]));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Exception, stopped.Reason);
        Assert.True(stopped.ThreadId > 0);

        // The thread ID should be usable for getStackTrace
        var stack = await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));

        Assert.NotEmpty(stack.StackFrames);
    }

    // =========================================================================
    // Exception description
    // =========================================================================

    [Fact]
    public async Task Exception_NullRef_DescriptionContainsType()
    {
        var program = FindFixture("ExceptionTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["null-ref"]));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Exception, stopped.Reason);
        Assert.Contains("NullReferenceException", stopped.Description);
    }

    [Fact]
    public async Task Exception_Custom_DescriptionContainsTypeAndMessage()
    {
        var program = FindFixture("ExceptionTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["custom"]));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Exception, stopped.Reason);
        Assert.Contains("InvalidOperationException", stopped.Description);
        Assert.Contains("Something went wrong", stopped.Description);
    }

    [Fact]
    public async Task Exception_NoMessage_DescriptionContainsType()
    {
        var program = FindFixture("ExceptionTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["no-message"]));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Exception, stopped.Reason);
        Assert.Contains("ApplicationException", stopped.Description);
    }

    [Fact]
    public async Task Exception_Nested_DescriptionContainsOuterType()
    {
        var program = FindFixture("ExceptionTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["nested"]));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Exception, stopped.Reason);
        Assert.Contains("AggregateException", stopped.Description);
    }

    [Fact]
    public async Task Exception_DescriptionNeverNull()
    {
        var program = FindFixture("ExceptionTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["null-ref"]));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Exception, stopped.Reason);
        Assert.NotNull(stopped.Description);
    }

    // =========================================================================
    // Full flow: thread ID, description, and exit code
    // =========================================================================

    [Fact]
    public async Task Exception_FullFlow_ThreadDescriptionExitCode()
    {
        var program = FindFixture("ExceptionTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: ["custom"]));

        // ThreadId should be valid
        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Exception, stopped.Reason);
        Assert.True(stopped.ThreadId > 0, $"Expected valid thread ID, got {stopped.ThreadId}");

        // Description should contain exception type
        Assert.Contains("InvalidOperationException", stopped.Description);
        Assert.Contains("Something went wrong", stopped.Description);

        // Continue and check exit code
        await Rpc!.InvokeWithParameterObjectAsync<object>(
            "continue", new ContinueRequest(ThreadId: stopped.ThreadId));

        // Exit code should be non-zero for unhandled exception
        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, exited.ExitCode);
    }
}
