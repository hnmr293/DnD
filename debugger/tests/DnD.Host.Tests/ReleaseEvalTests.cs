namespace DnD.Host.Tests;

using DnD.Protocol;
using StreamJsonRpc;

/// <summary>
/// Tests that func-eval on Release (optimized) builds returns errors
/// without killing the debuggee process.
/// Reproduces: https://github.com/hnmr293/DnD/issues/18
///
/// Root cause: ICorDebugEval2.CallParameterizedFunction (used for generic types)
/// does NOT throw CORDBG_E_ILLEGAL_IN_OPTIMIZED_CODE on optimized code, unlike
/// ICorDebugEval.CallFunction. When process.Continue(false) is called, the CLR
/// cannot hijack the thread for eval, so the process resumes normal execution
/// and exits.
/// </summary>
[Collection("DebugSession")]
[Trait("Category", "ReleaseEval")]
public class ReleaseEvalGenericTypeTests : DebugTestBase
{
    private int _stoppedThreadId;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var program = FindReleaseFixture("EvalTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Pause, stopped.Reason);
        _stoppedThreadId = stopped.ThreadId;

        await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));
    }

    /// <summary>
    /// Reproduces Issue #18: evaluating a property on a generic type
    /// (List&lt;int&gt;.Count) on a Release build kills the debuggee process.
    ///
    /// CallParameterizedFunction does not throw on optimized code.
    /// process.Continue resumes normal execution → process exits with code 0.
    /// </summary>
    [Fact]
    public async Task Evaluate_GenericPropertyOnOptimizedCode_ReturnsErrorWithoutKillingProcess()
    {
        // Act: evaluate list.Count — triggers CallParameterizedFunction
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
                "evaluate", new EvaluateRequest(Expression: "list.Count")));

        // Assert: process must NOT have exited
        Assert.False(ExitedTcs.Task.IsCompleted,
            "Process exited — eval on optimized generic type killed the debuggee (Issue #18)");

        // Assert: session is still functional
        var stack = await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: _stoppedThreadId));
        Assert.NotEmpty(stack.StackFrames);
    }

    private string FindReleaseFixture(string name)
    {
        var tfm = TargetFramework;
        var ext = tfm.StartsWith("net4") ? ".exe" : ".dll";
        var fixturePath = FindPath($"tests/fixtures/{name}/bin/Release/{tfm}/{name}{ext}");
        if (!File.Exists(fixturePath))
            throw new FileNotFoundException(
                $"Release fixture not built. Run: dotnet build tests/fixtures/{name} -c Release\nExpected: {fixturePath}");
        return fixturePath;
    }
}

/// <summary>
/// Control test: non-generic type eval on Release builds correctly throws
/// CORDBG_E_ILLEGAL_IN_OPTIMIZED_CODE and the session stays alive.
/// </summary>
[Collection("DebugSession")]
[Trait("Category", "ReleaseEval")]
public class ReleaseEvalNonGenericTypeTests : DebugTestBase
{
    private int _stoppedThreadId;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var program = FindReleaseFixture("EvalTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Pause, stopped.Reason);
        _stoppedThreadId = stopped.ThreadId;

        await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));
    }

    [Fact]
    public async Task Evaluate_NonGenericPropertyOnOptimizedCode_ReturnsErrorWithoutKillingProcess()
    {
        // Act: evaluate obj.Name — triggers CallFunction (non-generic)
        // This correctly throws CORDBG_E_ILLEGAL_IN_OPTIMIZED_CODE before Continue
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
                "evaluate", new EvaluateRequest(Expression: "obj.Name")));

        // Assert: process must NOT have exited
        Assert.False(ExitedTcs.Task.IsCompleted,
            "Process exited — eval on optimized non-generic type killed the debuggee");

        // Assert: session is still functional
        var stack = await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: _stoppedThreadId));
        Assert.NotEmpty(stack.StackFrames);
    }

    private string FindReleaseFixture(string name)
    {
        var tfm = TargetFramework;
        var ext = tfm.StartsWith("net4") ? ".exe" : ".dll";
        var fixturePath = FindPath($"tests/fixtures/{name}/bin/Release/{tfm}/{name}{ext}");
        if (!File.Exists(fixturePath))
            throw new FileNotFoundException(
                $"Release fixture not built. Run: dotnet build tests/fixtures/{name} -c Release\nExpected: {fixturePath}");
        return fixturePath;
    }
}
