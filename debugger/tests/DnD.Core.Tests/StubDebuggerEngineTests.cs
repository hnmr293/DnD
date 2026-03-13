namespace DnD.Core.Tests;

using DnD.Protocol;

public class StubDebuggerEngineTests
{
    private readonly StubDebuggerEngine _engine = new();

    // === Launch ===

    [Fact]
    public async Task Launch_ReturnsProcessId()
    {
        var result = await _engine.LaunchAsync(new LaunchRequest(Program: "test.exe"));
        Assert.Equal(12345, result.ProcessId);
    }

    [Fact]
    public async Task Launch_IgnoresOptionalArgs()
    {
        var result = await _engine.LaunchAsync(new LaunchRequest(
            Program: "other.exe",
            Args: ["--verbose"],
            Cwd: @"C:\temp",
            Env: new Dictionary<string, string> { ["KEY"] = "VAL" },
            StopAtEntry: true));
        Assert.Equal(12345, result.ProcessId);
    }

    // === Attach ===

    [Fact]
    public async Task Attach_EchoesProcessId()
    {
        var result = await _engine.AttachAsync(new AttachRequest(ProcessId: 999));
        Assert.Equal(999, result.ProcessId);
    }

    [Fact]
    public async Task Attach_ZeroProcessId_Succeeds()
    {
        var result = await _engine.AttachAsync(new AttachRequest(ProcessId: 0));
        Assert.Equal(0, result.ProcessId);
    }

    // === Detach / Terminate ===

    [Fact]
    public async Task Detach_CompletesSuccessfully()
    {
        await _engine.DetachAsync();
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
    public async Task Terminate_NoSubscriber_CompletesWithoutError()
    {
        await _engine.TerminateAsync();
    }

    // === Execution control ===

    [Fact]
    public async Task Continue_CompletesSuccessfully()
    {
        await _engine.ContinueAsync(new ContinueRequest(ThreadId: 1));
    }

    [Fact]
    public async Task Continue_NullThreadId_CompletesSuccessfully()
    {
        await _engine.ContinueAsync(new ContinueRequest());
    }

    [Fact]
    public async Task StepIn_CompletesSuccessfully()
    {
        await _engine.StepInAsync(new StepInRequest(ThreadId: 1));
    }

    [Fact]
    public async Task StepOver_CompletesSuccessfully()
    {
        await _engine.StepOverAsync(new StepOverRequest(ThreadId: 1));
    }

    [Fact]
    public async Task StepOut_CompletesSuccessfully()
    {
        await _engine.StepOutAsync(new StepOutRequest(ThreadId: 1));
    }

    // === Breakpoints ===

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
    public async Task SetBreakpoint_FirstIdIsOne()
    {
        var result = await _engine.SetBreakpointAsync(
            new SetBreakpointRequest(File: "a.cs", Line: 1));
        Assert.Equal(1, result.Breakpoint.Id);
    }

    [Fact]
    public async Task SetBreakpoint_IdsAreSequential()
    {
        var r1 = await _engine.SetBreakpointAsync(new SetBreakpointRequest(File: "a.cs", Line: 1));
        var r2 = await _engine.SetBreakpointAsync(new SetBreakpointRequest(File: "b.cs", Line: 2));
        var r3 = await _engine.SetBreakpointAsync(new SetBreakpointRequest(File: "c.cs", Line: 3));
        Assert.Equal(r1.Breakpoint.Id + 1, r2.Breakpoint.Id);
        Assert.Equal(r2.Breakpoint.Id + 1, r3.Breakpoint.Id);
    }

    [Fact]
    public async Task RemoveBreakpoint_CompletesSuccessfully()
    {
        await _engine.RemoveBreakpointAsync(new RemoveBreakpointRequest(BreakpointId: 1));
    }

    [Fact]
    public async Task GetBreakpoints_ReturnsEmptyArray()
    {
        var result = await _engine.GetBreakpointsAsync();
        Assert.Empty(result.Breakpoints);
    }

    // === Inspection ===

    [Fact]
    public async Task GetStackTrace_ReturnsFrameWithAllFields()
    {
        var result = await _engine.GetStackTraceAsync(new GetStackTraceRequest());
        var frame = Assert.Single(result.StackFrames);
        Assert.Equal(0, frame.Id);
        Assert.Equal("Main", frame.Name);
        Assert.Equal("Program.cs", frame.File);
        Assert.Equal(10, frame.Line);
    }

    [Fact]
    public async Task GetVariables_ReturnsStubVariable()
    {
        var result = await _engine.GetVariablesAsync(new GetVariablesRequest(VariablesReference: 0));
        var variable = Assert.Single(result.Variables);
        Assert.Equal("x", variable.Name);
        Assert.Equal("42", variable.Value);
        Assert.Equal("int", variable.Type);
        Assert.Equal(0, variable.VariablesReference);
    }

    [Fact]
    public async Task Evaluate_ReturnsStubPrefixedExpression()
    {
        var result = await _engine.EvaluateAsync(new EvaluateRequest(Expression: "x + 1"));
        Assert.Equal("stub:x + 1", result.Result);
        Assert.Equal("string", result.Type);
        Assert.Equal(0, result.VariablesReference);
    }

    [Fact]
    public async Task Evaluate_EmptyExpression_ReturnsStubColon()
    {
        var result = await _engine.EvaluateAsync(new EvaluateRequest(Expression: ""));
        Assert.Equal("stub:", result.Result);
    }

    [Fact]
    public async Task Evaluate_SpecialChars_PreservesContent()
    {
        var expr = "obj.Name == \"hello\\nworld\" && x > 0";
        var result = await _engine.EvaluateAsync(new EvaluateRequest(Expression: expr));
        Assert.Equal($"stub:{expr}", result.Result);
    }

    // === Pause ===

    [Fact]
    public async Task Pause_FiresStoppedWithPauseReason()
    {
        StoppedNotification? received = null;
        _engine.Stopped += (_, e) => received = e.Notification;

        await _engine.PauseAsync();

        // The stub fires Stopped asynchronously with a delay
        await Task.Delay(200);
        Assert.NotNull(received);
        Assert.Equal(StopReason.Pause, received!.Reason);
    }

    [Fact]
    public async Task Pause_ThreadIdIsOne()
    {
        StoppedNotification? received = null;
        _engine.Stopped += (_, e) => received = e.Notification;

        await _engine.PauseAsync();
        await Task.Delay(200);

        Assert.Equal(1, received!.ThreadId);
    }

    [Fact]
    public async Task Pause_DescriptionContainsStubStopped()
    {
        StoppedNotification? received = null;
        _engine.Stopped += (_, e) => received = e.Notification;

        await _engine.PauseAsync();
        await Task.Delay(200);

        Assert.Equal("Stub stopped", received!.Description);
    }

    [Fact]
    public async Task Pause_NoSubscriber_CompletesWithoutError()
    {
        await _engine.PauseAsync();
        await Task.Delay(100); // Let the background task complete
    }

    // === GetThreads ===

    [Fact]
    public async Task GetThreads_ReturnsSingleThread()
    {
        var result = await _engine.GetThreadsAsync();
        Assert.Single(result.Threads);
    }

    [Fact]
    public async Task GetThreads_ThreadIdIsOne()
    {
        var result = await _engine.GetThreadsAsync();
        Assert.Equal(1, result.Threads[0].Id);
    }

    [Fact]
    public async Task GetThreads_ThreadIsMarkedCurrent()
    {
        var result = await _engine.GetThreadsAsync();
        Assert.True(result.Threads[0].Current);
    }

    [Fact]
    public async Task GetThreads_CalledTwice_ReturnsSameResult()
    {
        var r1 = await _engine.GetThreadsAsync();
        var r2 = await _engine.GetThreadsAsync();
        Assert.Equal(r1.Threads[0].Id, r2.Threads[0].Id);
        Assert.Equal(r1.Threads[0].Current, r2.Threads[0].Current);
    }

    // === GetException ===

    [Fact]
    public async Task GetException_ReturnsExceptionType()
    {
        var result = await _engine.GetExceptionAsync(new GetExceptionRequest());
        Assert.Equal("System.Exception", result.Type);
    }

    [Fact]
    public async Task GetException_ReturnsStubMessage()
    {
        var result = await _engine.GetExceptionAsync(new GetExceptionRequest());
        Assert.Equal("Stub exception", result.Message);
    }

    [Fact]
    public async Task GetException_StackTraceIsNull()
    {
        var result = await _engine.GetExceptionAsync(new GetExceptionRequest());
        Assert.Null(result.StackTrace);
    }

    [Fact]
    public async Task GetException_InnerExceptionIsNull()
    {
        var result = await _engine.GetExceptionAsync(new GetExceptionRequest());
        Assert.Null(result.InnerException);
    }

    [Fact]
    public async Task GetException_WithThreadId_IgnoresThreadId()
    {
        var result = await _engine.GetExceptionAsync(new GetExceptionRequest(ThreadId: 42));
        Assert.Equal("System.Exception", result.Type);
        Assert.Equal("Stub exception", result.Message);
    }

    [Fact]
    public async Task GetException_CalledTwice_ReturnsSameResult()
    {
        var r1 = await _engine.GetExceptionAsync(new GetExceptionRequest());
        var r2 = await _engine.GetExceptionAsync(new GetExceptionRequest());
        Assert.Equal(r1.Type, r2.Type);
        Assert.Equal(r1.Message, r2.Message);
    }

    // === Corner cases: double/repeated calls ===

    [Fact]
    public async Task Terminate_CalledTwice_FiresExitedTwice()
    {
        var count = 0;
        _engine.Exited += (_, _) => count++;

        await _engine.TerminateAsync();
        await _engine.TerminateAsync();

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task Detach_CalledTwice_Succeeds()
    {
        await _engine.DetachAsync();
        await _engine.DetachAsync();
    }

    [Fact]
    public async Task Launch_CalledTwice_BothReturnSameProcessId()
    {
        var r1 = await _engine.LaunchAsync(new LaunchRequest(Program: "a.exe"));
        var r2 = await _engine.LaunchAsync(new LaunchRequest(Program: "b.exe"));
        Assert.Equal(r1.ProcessId, r2.ProcessId);
    }

    // === Corner cases: boundary values ===

    [Fact]
    public async Task Attach_NegativeProcessId_EchoesAsIs()
    {
        var result = await _engine.AttachAsync(new AttachRequest(ProcessId: -1));
        Assert.Equal(-1, result.ProcessId);
    }

    [Fact]
    public async Task Attach_MaxIntProcessId_EchoesAsIs()
    {
        var result = await _engine.AttachAsync(new AttachRequest(ProcessId: int.MaxValue));
        Assert.Equal(int.MaxValue, result.ProcessId);
    }

    [Fact]
    public async Task SetBreakpoint_LineBoundary_ZeroLine()
    {
        var result = await _engine.SetBreakpointAsync(
            new SetBreakpointRequest(File: "a.cs", Line: 0));
        Assert.Equal(0, result.Breakpoint.Line);
    }

    [Fact]
    public async Task SetBreakpoint_EmptyFilePath()
    {
        var result = await _engine.SetBreakpointAsync(
            new SetBreakpointRequest(File: "", Line: 1));
        Assert.Equal("", result.Breakpoint.File);
        Assert.True(result.Breakpoint.Verified);
    }

    [Fact]
    public async Task SetBreakpoint_UnicodeFilePath()
    {
        var result = await _engine.SetBreakpointAsync(
            new SetBreakpointRequest(File: "ソース/テスト.cs", Line: 5));
        Assert.Equal("ソース/テスト.cs", result.Breakpoint.File);
    }

    // === Corner cases: optional parameters ===

    [Fact]
    public async Task Evaluate_WithFrameId_IgnoresFrameId()
    {
        var result = await _engine.EvaluateAsync(
            new EvaluateRequest(Expression: "x", FrameId: 99));
        Assert.Equal("stub:x", result.Result);
    }

    [Fact]
    public async Task GetStackTrace_WithThreadId_ReturnsStubRegardless()
    {
        var result = await _engine.GetStackTraceAsync(
            new GetStackTraceRequest(ThreadId: 42));
        Assert.Single(result.StackFrames);
        Assert.Equal("Main", result.StackFrames[0].Name);
    }

    [Fact]
    public async Task GetVariables_DifferentReference_ReturnsSameStub()
    {
        var r1 = await _engine.GetVariablesAsync(new GetVariablesRequest(VariablesReference: 0));
        var r2 = await _engine.GetVariablesAsync(new GetVariablesRequest(VariablesReference: 999));
        Assert.Equal(r1.Variables[0].Name, r2.Variables[0].Name);
    }

    [Fact]
    public async Task Evaluate_UnicodeExpression()
    {
        var result = await _engine.EvaluateAsync(
            new EvaluateRequest(Expression: "変数名 + 1"));
        Assert.Equal("stub:変数名 + 1", result.Result);
    }

    // === Concurrency ===

    [Fact]
    public async Task SetBreakpoint_ConcurrentCalls_AllIdsUnique()
    {
        var tasks = Enumerable.Range(0, 10)
            .Select(i => _engine.SetBreakpointAsync(
                new SetBreakpointRequest(File: $"file{i}.cs", Line: i)));

        var results = await Task.WhenAll(tasks);
        var ids = results.Select(r => r.Breakpoint.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // === Events ===

    [Fact]
    public void Stopped_CanBeSubscribed()
    {
        StoppedNotification? received = null;
        _engine.Stopped += (_, e) => received = e.Notification;
        // Stub never fires Stopped, but subscription must not throw
        Assert.Null(received);
    }

    [Fact]
    public void Output_CanBeSubscribed()
    {
        OutputNotification? received = null;
        _engine.Output += (_, e) => received = e.Notification;
        Assert.Null(received);
    }

    [Fact]
    public async Task Exited_MultipleSubscribers_AllReceiveEvent()
    {
        ExitedNotification? received1 = null;
        ExitedNotification? received2 = null;
        _engine.Exited += (_, e) => received1 = e.Notification;
        _engine.Exited += (_, e) => received2 = e.Notification;

        await _engine.TerminateAsync();

        Assert.NotNull(received1);
        Assert.NotNull(received2);
        Assert.Equal(0, received1!.ExitCode);
        Assert.Equal(0, received2!.ExitCode);
    }
}
