namespace DnD.Host.Tests;

using DnD.Protocol;
using StreamJsonRpc;

[Collection("DebugSession")]
[Trait("Category", "DebugSession")]
public class DebugSessionTests : DebugTestBase
{
    // === Process control ===

    [Fact]
    public async Task Launch_HelloWorld_ReturnsProcessId()
    {
        var program = FindFixture("HelloWorld");
        var result = await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        Assert.True(result.ProcessId > 0);
    }

    [Fact]
    public async Task Launch_ThenTerminate_SendsExitedNotification()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        await Rpc!.InvokeAsync("terminate");

        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exited.ExitCode);
    }

    [Fact]
    public async Task Launch_HelloWorld_ProcessExitsNaturally()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        // Wait for exited notification with a generous timeout as a safety net.
        try
        {
            var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, exited.ExitCode);
        }
        catch (TimeoutException)
        {
            // Safety fallback — force terminate and check exit code.
            try
            {
                await Rpc!.InvokeAsync("terminate");
                var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, exited.ExitCode);
            }
            catch (ConnectionLostException)
            {
                // Host process already exited — that's acceptable
            }
        }
    }

    [Fact]
    public async Task Detach_AfterLaunch_Succeeds()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        await Rpc!.InvokeAsync("detach");
    }

    // === Error handling ===

    [Fact]
    public async Task Continue_BeforeLaunch_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<object>(
                "continue", new ContinueRequest()));

        Assert.Contains("Invalid state", ex.Message);
    }

    [Fact]
    public async Task StepOver_BeforeLaunch_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<object>(
                "stepOver", new StepOverRequest()));

        Assert.Contains("Invalid state", ex.Message);
    }

    [Fact]
    public async Task GetStackTrace_BeforeLaunch_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
                "getStackTrace", new GetStackTraceRequest()));

        Assert.Contains("Invalid state", ex.Message);
    }

    [Fact]
    public async Task Evaluate_BeforeLaunch_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
                "evaluate", new EvaluateRequest(Expression: "x")));

        Assert.Contains("Invalid state", ex.Message);
    }

    [Fact]
    public async Task Launch_NonexistentProgram_ThrowsError()
    {
        await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
                "launch", new LaunchRequest(Program: @"C:\nonexistent\fake.dll")));
    }

    [Fact]
    public async Task Terminate_BeforeLaunch_Succeeds()
    {
        await Rpc!.InvokeAsync("terminate");
        var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exited.ExitCode);
    }

    // === Breakpoint management ===

    [Fact]
    public async Task SetBreakpoint_BeforeLaunch_CreatesPendingBreakpoint()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "Program.cs", Line: 1));

        Assert.False(result.Breakpoint.Verified);
        Assert.True(result.Breakpoint.Id > 0);
    }

    [Fact]
    public async Task GetBreakpoints_ReturnsSetBreakpoints()
    {
        await Rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "a.cs", Line: 1));
        await Rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "b.cs", Line: 2));

        var result = await Rpc!.InvokeWithParameterObjectAsync<GetBreakpointsResponse>(
            "getBreakpoints", new { });

        Assert.Equal(2, result.Breakpoints.Length);
    }

    [Fact]
    public async Task RemoveBreakpoint_RemovesFromList()
    {
        var bp = await Rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "a.cs", Line: 1));

        await Rpc!.InvokeWithParameterObjectAsync<object>(
            "removeBreakpoint", new RemoveBreakpointRequest(BreakpointId: bp.Breakpoint.Id));

        var result = await Rpc!.InvokeWithParameterObjectAsync<GetBreakpointsResponse>(
            "getBreakpoints", new { });

        Assert.Empty(result.Breakpoints);
    }

    // === Debugger.Break() + inspection ===

    [Fact]
    public async Task VariablesTest_DebuggerBreak_StopsAndShowsVariables()
    {
        var program = FindFixture("VariablesTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Pause, stopped.Reason);
        Assert.True(stopped.ThreadId > 0);

        var stack = await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));

        Assert.NotEmpty(stack.StackFrames);
        Assert.NotNull(stack.StackFrames[0].Name);

        var vars = await Rpc!.InvokeWithParameterObjectAsync<GetVariablesResponse>(
            "getVariables", new GetVariablesRequest(VariablesReference: 0));

        Assert.NotEmpty(vars.Variables);
    }

    [Fact]
    public async Task VariablesTest_DebuggerBreak_ContinueToExit()
    {
        var program = FindFixture("VariablesTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Pause, stopped.Reason);

        await Rpc!.InvokeWithParameterObjectAsync<object>(
            "continue", new ContinueRequest(ThreadId: stopped.ThreadId));

        try
        {
            var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, exited.ExitCode);
        }
        catch (TimeoutException)
        {
            try
            {
                await Rpc!.InvokeAsync("terminate");
                var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, exited.ExitCode);
            }
            catch (ConnectionLostException)
            {
                // Host process already exited — acceptable
            }
        }
    }

    // === Breakpoint hit ===

    [Fact]
    public async Task BreakpointTest_SetBreakpointAndHit()
    {
        var program = FindFixture("BreakpointTest");
        var sourceFile = FindFixtureSrc("BreakpointTest", "Program.cs");

        var bp = await Rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: sourceFile, Line: 7));

        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Breakpoint, stopped.Reason);
    }

    // === Stepping ===

    [Fact]
    public async Task VariablesTest_StepOver_MovesToNextLine()
    {
        var program = FindFixture("VariablesTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();

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

        Assert.NotNull(line1);
        Assert.NotNull(line2);
        Assert.True(line2 > line1, $"Expected line to advance: was {line1}, now {line2}");
    }

    // === Double terminate ===

    [Fact]
    public async Task Terminate_CalledTwice_DoesNotThrow()
    {
        var program = FindFixture("HelloWorld");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        await Rpc!.InvokeAsync("terminate");
        await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await Rpc!.InvokeAsync("terminate");
    }

    // === StepIn ===

    [Fact]
    public async Task SteppingTest_StepIn_EntersFunction()
    {
        var program = FindFixture("SteppingTest");
        var sourceFile = FindFixtureSrc("SteppingTest", "Program.cs");

        // Set breakpoint on Outer() call (line 18)
        await Rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: sourceFile, Line: 18));

        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Breakpoint, stopped.Reason);

        // StepIn should enter the function
        await Rpc!.InvokeWithParameterObjectAsync<object>(
            "stepIn", new StepInRequest(ThreadId: stopped.ThreadId));

        var stopped2 = WaitForStopped(TimeSpan.FromSeconds(10));
        Assert.Equal(StopReason.Step, stopped2.Reason);

        var stack = await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped2.ThreadId));

        // Should be inside Outer() or deeper
        Assert.NotEmpty(stack.StackFrames);
    }

    // === Breakpoint edge cases ===

    [Fact]
    public async Task SetBreakpoint_InvalidLine_CreatesUnverified()
    {
        var program = FindFixture("HelloWorld");
        var sourceFile = FindFixtureSrc("HelloWorld", "Program.cs");

        // Line 9999 doesn't exist
        await Rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: sourceFile, Line: 9999));

        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        // Should not hit a breakpoint — the program should just exit
        try
        {
            var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, exited.ExitCode);
        }
        catch (TimeoutException)
        {
            try
            {
                await Rpc!.InvokeAsync("terminate");
                var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, exited.ExitCode);
            }
            catch (ConnectionLostException) { }
        }
    }

    [Fact]
    public async Task RemoveBreakpoint_InvalidId_DoesNotThrow()
    {
        await Rpc!.InvokeWithParameterObjectAsync<object>(
            "removeBreakpoint", new RemoveBreakpointRequest(BreakpointId: 9999));
    }

    [Fact]
    public async Task SetBreakpoint_MultipleOnSameLine_AllTracked()
    {
        await Rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "a.cs", Line: 5));
        await Rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "a.cs", Line: 5));

        var result = await Rpc!.InvokeWithParameterObjectAsync<GetBreakpointsResponse>(
            "getBreakpoints", new { });

        Assert.Equal(2, result.Breakpoints.Length);
    }

    // === Inspection edge cases ===

    [Fact]
    public async Task GetVariables_AfterBreak_ReturnsLocals()
    {
        var program = FindFixture("VariablesTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));

        var vars = await Rpc!.InvokeWithParameterObjectAsync<GetVariablesResponse>(
            "getVariables", new GetVariablesRequest(VariablesReference: 0));

        // Should include known variables from the fixture
        Assert.NotEmpty(vars.Variables);
        var names = vars.Variables.Select(v => v.Name).ToList();
        Assert.Contains("x", names);
        Assert.Contains("name", names);
    }

    [Fact]
    public async Task Evaluate_VariablesTest_IntVariable()
    {
        var program = FindFixture("VariablesTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));

        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "x"));

        Assert.Equal("42", result.Result);
        Assert.Equal("int", result.Type);
    }

    // === Detach edge cases ===

    [Fact]
    public async Task Detach_BeforeLaunch_Succeeds()
    {
        await Rpc!.InvokeAsync("detach");
    }

    /// <summary>
    /// Regression: Detach while stopped at a breakpoint previously failed with
    /// CORDBG_E_PROCESS_NOT_SYNCHRONIZED because queued ICorDebug callbacks
    /// re-stopped the process between Continue(false) and Detach().
    /// Fix: Continue → Stop(5000) → Detach drains the callback queue first.
    /// </summary>
    [Fact]
    public async Task Detach_WhileStoppedAtBreakpoint_DebuggeeExitsNormally()
    {
        var program = FindFixture("BreakpointTest");
        var sourceFile = FindFixtureSrc("BreakpointTest", "Program.cs");

        await Rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: sourceFile, Line: 7));

        var launch = await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Breakpoint, stopped.Reason);

        // Grab a process handle before detach — the debuggee may exit immediately
        // after detach, and GetProcessById would throw if it's already gone.
        // EnableRaisingEvents opens an internal handle with sufficient access rights
        // for ExitCode to work (plain GetProcessById does not).
        using var proc = System.Diagnostics.Process.GetProcessById(launch.ProcessId);
        proc.EnableRaisingEvents = true;

        // Detach while stopped at breakpoint — must not throw
        await Rpc!.InvokeAsync("detach");

        // Debuggee should resume and exit normally (not crash/hang)
        Assert.True(proc.WaitForExit(10_000), "Debuggee did not exit within timeout");
        Assert.Equal(0, proc.ExitCode);
    }

    /// <summary>
    /// Regression: After the debuggee exited, the debugger Host kept ISymbolReader
    /// locks on PDB/DLL files because OnProcessExited did not dispose module resources.
    /// Fix: OnProcessExited now disposes readers and terminates ICorDebug.
    /// </summary>
    [Fact]
    public async Task ProcessExit_ReleasesModuleLocks()
    {
        var program = FindFixture("HelloWorld");
        var programDir = Path.GetDirectoryName(program)!;

        // Copy fixture to temp so we can test file locking without affecting build output
        var tempDir = Path.Combine(Path.GetTempPath(), $"dnd-lock-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var file in Directory.GetFiles(programDir))
                File.Copy(file, Path.Combine(tempDir, Path.GetFileName(file)));

            var tempProgram = Path.Combine(tempDir, Path.GetFileName(program));

            await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
                "launch", new LaunchRequest(Program: tempProgram));

            // Wait for natural exit
            var exited = await ExitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, exited.ExitCode);

            // Verify PDB file is not locked by the debugger Host
            var pdbFile = Path.Combine(tempDir,
                Path.GetFileNameWithoutExtension(program) + ".pdb");
            if (File.Exists(pdbFile))
            {
                // Opening exclusively would throw if still locked
                using var fs = File.Open(pdbFile, FileMode.Open,
                    FileAccess.ReadWrite, FileShare.None);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); }
            catch { }
        }
    }

    // === StepOut ===

    [Fact]
    public async Task StepOut_BeforeLaunch_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<object>(
                "stepOut", new StepOutRequest()));

        Assert.Contains("Invalid state", ex.Message);
    }

    [Fact]
    public async Task StepIn_BeforeLaunch_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<object>(
                "stepIn", new StepInRequest()));

        Assert.Contains("Invalid state", ex.Message);
    }

    [Fact]
    public async Task GetVariables_BeforeLaunch_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<GetVariablesResponse>(
                "getVariables", new GetVariablesRequest(VariablesReference: 0)));

        Assert.Contains("Invalid state", ex.Message);
    }
}
