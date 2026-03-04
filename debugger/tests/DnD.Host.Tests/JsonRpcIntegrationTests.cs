namespace DnD.Host.Tests;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnD.Protocol;
using StreamJsonRpc;

public class JsonRpcIntegrationTests : IAsyncLifetime
{
    private Process? _hostProcess;
    private JsonRpc? _rpc;
    private readonly TaskCompletionSource<ExitedNotification> _exitedTcs = new();

    public async Task InitializeAsync()
    {
        var hostProject = FindPath("src/DnD.Host/DnD.Host.csproj");

        _hostProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{hostProject}\" --no-build -- --stub",
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

        // Register notification handlers before StartListening.
        _rpc.AddLocalRpcMethod("exited", (int exitCode) =>
        {
            _exitedTcs.TrySetResult(new ExitedNotification(ExitCode: exitCode));
        });

        _rpc.StartListening();

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _rpc?.Dispose();
        if (_hostProcess is { HasExited: false })
        {
            _hostProcess.Kill();
            await _hostProcess.WaitForExitAsync();
        }
        _hostProcess?.Dispose();
    }

    // === Process control ===

    [Fact]
    public async Task Launch_ReturnsProcessId()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: "test.exe"));

        Assert.Equal(12345, result.ProcessId);
    }

    [Fact]
    public async Task Launch_WithAllOptionalArgs_ReturnsProcessId()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(
                Program: "test.exe",
                Args: ["--verbose", "--debug"],
                Cwd: @"C:\projects",
                Env: new Dictionary<string, string> { ["PATH"] = "/usr/bin" },
                StopAtEntry: true));

        Assert.Equal(12345, result.ProcessId);
    }

    [Fact]
    public async Task Attach_EchoesProcessId()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<AttachResponse>(
            "attach", new AttachRequest(ProcessId: 9999));

        Assert.Equal(9999, result.ProcessId);
    }

    [Fact]
    public async Task Attach_ZeroProcessId()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<AttachResponse>(
            "attach", new AttachRequest(ProcessId: 0));

        Assert.Equal(0, result.ProcessId);
    }

    [Fact]
    public async Task Detach_Succeeds()
    {
        await _rpc!.InvokeAsync("detach");
    }

    [Fact]
    public async Task Terminate_SendsExitedNotification()
    {
        await _rpc!.InvokeAsync("terminate");

        var exited = await _exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exited.ExitCode);
    }

    // === Execution control ===

    [Fact]
    public async Task Continue_WithThreadId_Succeeds()
    {
        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "continue", new ContinueRequest(ThreadId: 1));
    }

    [Fact]
    public async Task Continue_WithoutThreadId_Succeeds()
    {
        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "continue", new ContinueRequest());
    }

    [Fact]
    public async Task StepIn_Succeeds()
    {
        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "stepIn", new StepInRequest(ThreadId: 1));
    }

    [Fact]
    public async Task StepOver_Succeeds()
    {
        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "stepOver", new StepOverRequest(ThreadId: 1));
    }

    [Fact]
    public async Task StepOut_Succeeds()
    {
        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "stepOut", new StepOutRequest(ThreadId: 1));
    }

    // === Breakpoints ===

    [Fact]
    public async Task SetBreakpoint_ReturnsVerifiedBreakpoint()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "Program.cs", Line: 10));

        Assert.True(result.Breakpoint.Verified);
        Assert.Equal("Program.cs", result.Breakpoint.File);
        Assert.Equal(10, result.Breakpoint.Line);
    }

    [Fact]
    public async Task SetBreakpoint_IdsIncrement()
    {
        var r1 = await _rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "a.cs", Line: 1));
        var r2 = await _rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "b.cs", Line: 2));

        Assert.Equal(r1.Breakpoint.Id + 1, r2.Breakpoint.Id);
    }

    [Fact]
    public async Task RemoveBreakpoint_Succeeds()
    {
        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "removeBreakpoint", new RemoveBreakpointRequest(BreakpointId: 1));
    }

    [Fact]
    public async Task GetBreakpoints_ReturnsEmptyList()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<GetBreakpointsResponse>(
            "getBreakpoints", new { });

        Assert.Empty(result.Breakpoints);
    }

    // === Inspection ===

    [Fact]
    public async Task GetStackTrace_ReturnsFrameWithAllFields()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest());

        var frame = Assert.Single(result.StackFrames);
        Assert.Equal(0, frame.Id);
        Assert.Equal("Main", frame.Name);
        Assert.Equal("Program.cs", frame.File);
        Assert.Equal(10, frame.Line);
    }

    [Fact]
    public async Task GetVariables_ReturnsStubVariable()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<GetVariablesResponse>(
            "getVariables", new GetVariablesRequest(VariablesReference: 0));

        var variable = Assert.Single(result.Variables);
        Assert.Equal("x", variable.Name);
        Assert.Equal("42", variable.Value);
        Assert.Equal("int", variable.Type);
        Assert.Equal(0, variable.VariablesReference);
    }

    [Fact]
    public async Task Evaluate_ReturnsStubResult()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "x + 1"));

        Assert.Equal("stub:x + 1", result.Result);
        Assert.Equal("string", result.Type);
        Assert.Equal(0, result.VariablesReference);
    }

    [Fact]
    public async Task Evaluate_EmptyExpression()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: ""));

        Assert.Equal("stub:", result.Result);
    }

    // === Edge cases: Unicode ===

    [Fact]
    public async Task SetBreakpoint_UnicodeFilePath_PreservedThroughRpc()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "ソース/テスト.cs", Line: 5));

        Assert.Equal("ソース/テスト.cs", result.Breakpoint.File);
    }

    [Fact]
    public async Task Evaluate_UnicodeExpression_PreservedThroughRpc()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "変数名 + 1"));

        Assert.Equal("stub:変数名 + 1", result.Result);
    }

    [Fact]
    public async Task Evaluate_SpecialChars_PreservedThroughRpc()
    {
        var expr = "obj.Name == \"hello\\nworld\" && x > 0";
        var result = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: expr));

        Assert.Equal($"stub:{expr}", result.Result);
    }

    // === Edge cases: boundary values ===

    [Fact]
    public async Task Attach_NegativeProcessId_EchoedThroughRpc()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<AttachResponse>(
            "attach", new AttachRequest(ProcessId: -1));

        Assert.Equal(-1, result.ProcessId);
    }

    // === Error handling ===

    [Fact]
    public async Task UnknownMethod_ThrowsRemoteMethodNotFoundException()
    {
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(
            () => _rpc!.InvokeAsync("nonExistentMethod"));
    }

    [Fact]
    public async Task PascalCaseMethodName_ThrowsRemoteMethodNotFoundException()
    {
        // Server maps PascalCase -> camelCase, so "Launch" should not be found
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(
            () => _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
                "Launch", new LaunchRequest(Program: "test.exe")));
    }

    // === Multi-call sequences ===

    [Fact]
    public async Task FullDebugSession_LaunchSetBreakpointContinueTerminate()
    {
        // Launch
        var launch = await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: "app.exe"));
        Assert.Equal(12345, launch.ProcessId);

        // Set breakpoint
        var bp = await _rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "Program.cs", Line: 5));
        Assert.True(bp.Breakpoint.Verified);

        // Continue
        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "continue", new ContinueRequest(ThreadId: 1));

        // Inspect stack
        var stack = await _rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest());
        Assert.Single(stack.StackFrames);

        // Evaluate
        var eval = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "myVar"));
        Assert.Equal("stub:myVar", eval.Result);

        // Terminate
        await _rpc!.InvokeAsync("terminate");
        var exited = await _exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exited.ExitCode);
    }

    [Fact]
    public async Task StepSequence_StepInOverOut()
    {
        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "stepIn", new StepInRequest(ThreadId: 1));
        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "stepOver", new StepOverRequest(ThreadId: 1));
        await _rpc!.InvokeWithParameterObjectAsync<object>(
            "stepOut", new StepOutRequest(ThreadId: 1));
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
