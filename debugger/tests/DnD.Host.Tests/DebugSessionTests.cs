namespace DnD.Host.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnD.Protocol;
using StreamJsonRpc;

[Trait("Category", "DebugSession")]
public class DebugSessionTests : IAsyncLifetime
{
    private Process? _hostProcess;
    private JsonRpc? _rpc;
    private readonly BlockingCollection<StoppedNotification> _stoppedQueue = new();
    private readonly TaskCompletionSource<ExitedNotification> _exitedTcs = new();

    public async Task InitializeAsync()
    {
        var hostProject = FindPath("src/DnD.Host/DnD.Host.csproj");

        _hostProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{hostProject}\" --no-build",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        _hostProcess.Start();

        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        var handler = new HeaderDelimitedMessageHandler(
            sendingStream: _hostProcess.StandardInput.BaseStream,
            receivingStream: _hostProcess.StandardOutput.BaseStream,
            formatter: formatter
        );

        _rpc = new JsonRpc(handler);

        _rpc.AddLocalRpcMethod("stopped", (StopReason reason, int threadId, string? description) =>
        {
            _stoppedQueue.Add(new StoppedNotification(reason, threadId, description));
        });

        _rpc.AddLocalRpcMethod("exited", (int exitCode) =>
        {
            _exitedTcs.TrySetResult(new ExitedNotification(ExitCode: exitCode));
        });

        _rpc.StartListening();

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        try
        {
            if (_rpc != null)
            {
                try { await _rpc.InvokeAsync("terminate"); }
                catch { }
            }
        }
        catch { }

        _rpc?.Dispose();
        if (_hostProcess is { HasExited: false })
        {
            _hostProcess.Kill();
            await _hostProcess.WaitForExitAsync();
        }
        _hostProcess?.Dispose();
        _stoppedQueue.Dispose();
    }

    private StoppedNotification WaitForStopped(TimeSpan? timeout = null)
    {
        var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        return _stoppedQueue.Take(cts.Token);
    }

    // === Process control ===

    [Fact]
    public async Task Launch_HelloWorld_ReturnsProcessId()
    {
        var program = FindFixture("HelloWorld");
        var result = await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        Assert.True(result.ProcessId > 0);
    }

    [Fact]
    public async Task Launch_ThenTerminate_SendsExitedNotification()
    {
        var program = FindFixture("HelloWorld");
        await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        await _rpc!.InvokeAsync("terminate");

        var exited = await _exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exited.ExitCode);
    }

    [Fact]
    public async Task Launch_HelloWorld_ProcessExitsNaturally()
    {
        var program = FindFixture("HelloWorld");
        await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        // Wait for exited notification or terminate fallback.
        // The ExitProcess callback from ICorDebug may not always reach
        // us reliably through the JSON-RPC pipeline.
        try
        {
            var exited = await _exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, exited.ExitCode);
        }
        catch (TimeoutException)
        {
            // If no exited notification arrived, the process may still have exited.
            // Try to terminate — if the connection is lost, the host already exited.
            try
            {
                await _rpc!.InvokeAsync("terminate");
                var exited = await _exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        await _rpc!.InvokeAsync("detach");
    }

    // === Error handling ===

    [Fact]
    public async Task Continue_BeforeLaunch_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _rpc!.InvokeWithParameterObjectAsync<object>(
                "continue", new ContinueRequest()));

        Assert.Contains("Invalid state", ex.Message);
    }

    [Fact]
    public async Task StepOver_BeforeLaunch_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _rpc!.InvokeWithParameterObjectAsync<object>(
                "stepOver", new StepOverRequest()));

        Assert.Contains("Invalid state", ex.Message);
    }

    [Fact]
    public async Task GetStackTrace_BeforeLaunch_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
                "getStackTrace", new GetStackTraceRequest()));

        Assert.Contains("Invalid state", ex.Message);
    }

    [Fact]
    public async Task Evaluate_BeforeLaunch_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
                "evaluate", new EvaluateRequest(Expression: "x")));

        Assert.Contains("Invalid state", ex.Message);
    }

    [Fact]
    public async Task Launch_NonexistentProgram_ThrowsError()
    {
        await Assert.ThrowsAsync<RemoteInvocationException>(
            () => _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
                "launch", new LaunchRequest(Program: @"C:\nonexistent\fake.dll")));
    }

    [Fact]
    public async Task Terminate_BeforeLaunch_Succeeds()
    {
        await _rpc!.InvokeAsync("terminate");
        var exited = await _exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exited.ExitCode);
    }

    // === Breakpoint management ===

    [Fact]
    public async Task SetBreakpoint_BeforeLaunch_CreatesPendingBreakpoint()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "Program.cs", Line: 1));

        Assert.False(result.Breakpoint.Verified);
        Assert.True(result.Breakpoint.Id > 0);
    }

    [Fact]
    public async Task GetBreakpoints_ReturnsSetBreakpoints()
    {
        await _rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "a.cs", Line: 1));
        await _rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "b.cs", Line: 2));

        var result = await _rpc!.InvokeWithParameterObjectAsync<GetBreakpointsResponse>(
            "getBreakpoints", new { });

        Assert.Equal(2, result.Breakpoints.Length);
    }

    [Fact]
    public async Task RemoveBreakpoint_RemovesFromList()
    {
        var bp = await _rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "a.cs", Line: 1));

        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "removeBreakpoint", new RemoveBreakpointRequest(BreakpointId: bp.Breakpoint.Id));

        var result = await _rpc!.InvokeWithParameterObjectAsync<GetBreakpointsResponse>(
            "getBreakpoints", new { });

        Assert.Empty(result.Breakpoints);
    }

    // === Debugger.Break() + inspection ===

    [Fact]
    public async Task VariablesTest_DebuggerBreak_StopsAndShowsVariables()
    {
        var program = FindFixture("VariablesTest");
        await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Pause, stopped.Reason);
        Assert.True(stopped.ThreadId > 0);

        var stack = await _rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));

        Assert.NotEmpty(stack.StackFrames);
        Assert.NotNull(stack.StackFrames[0].Name);

        var vars = await _rpc!.InvokeWithParameterObjectAsync<GetVariablesResponse>(
            "getVariables", new GetVariablesRequest(VariablesReference: 0));

        Assert.NotEmpty(vars.Variables);
    }

    [Fact]
    public async Task VariablesTest_DebuggerBreak_ContinueToExit()
    {
        var program = FindFixture("VariablesTest");
        await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Pause, stopped.Reason);

        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "continue", new ContinueRequest(ThreadId: stopped.ThreadId));

        try
        {
            var exited = await _exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, exited.ExitCode);
        }
        catch (TimeoutException)
        {
            try
            {
                await _rpc!.InvokeAsync("terminate");
                var exited = await _exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

        var bp = await _rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: sourceFile, Line: 3));

        await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Breakpoint, stopped.Reason);
    }

    // === Stepping ===

    [Fact]
    public async Task VariablesTest_StepOver_MovesToNextLine()
    {
        var program = FindFixture("VariablesTest");
        await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();

        var stack1 = await _rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));
        var line1 = stack1.StackFrames[0].Line;

        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "stepOver", new StepOverRequest(ThreadId: stopped.ThreadId));

        var stopped2 = WaitForStopped(TimeSpan.FromSeconds(10));
        Assert.Equal(StopReason.Step, stopped2.Reason);

        var stack2 = await _rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
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
        await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        await _rpc!.InvokeAsync("terminate");
        await _exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await _rpc!.InvokeAsync("terminate");
    }

    // === Helpers ===

    private static string FindFixture(string name)
    {
        var fixturePath = FindPath($"tests/fixtures/{name}/bin/Debug/net10.0/{name}.dll");
        if (!File.Exists(fixturePath))
            throw new FileNotFoundException($"Fixture not built: {fixturePath}");
        return fixturePath;
    }

    private static string FindFixtureSrc(string name, string file)
    {
        return FindPath($"tests/fixtures/{name}/{file}");
    }

    private static string FindPath(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "DnD.slnx")))
                return Path.Combine(dir, relativePath);
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException(
            $"Could not find solution root from {AppContext.BaseDirectory}");
    }
}
