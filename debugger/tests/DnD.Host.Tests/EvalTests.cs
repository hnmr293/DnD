namespace DnD.Host.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnD.Protocol;
using StreamJsonRpc;

[Trait("Category", "Eval")]
public class EvalTests : IAsyncLifetime
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

        // Launch EvalTest and wait for Debugger.Break()
        var program = FindFixture("EvalTest");
        await _rpc.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Pause, stopped.Reason);

        // Populate frame map (required before evaluate)
        await _rpc.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));
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

    // === Eval tests ===

    [Fact]
    public async Task Evaluate_SimpleVariable()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number"));

        Assert.Equal("42", result.Result);
        Assert.Equal("int", result.Type);
    }

    [Fact]
    public async Task Evaluate_PropertyAccess()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "greeting.Length"));

        Assert.Equal("13", result.Result);
    }

    [Fact]
    public async Task Evaluate_ToString()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "obj.ToString()"));

        Assert.Equal("\"TestClass(test, 100)\"", result.Result);
    }

    [Fact]
    public async Task Evaluate_MethodWithArgs()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "obj.Add(10)"));

        Assert.Equal("110", result.Result);
    }

    [Fact]
    public async Task Evaluate_IntArithmetic()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number + 1"));

        Assert.Equal("43", result.Result);
    }

    [Fact]
    public async Task Evaluate_Comparison()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number > 40"));

        Assert.Equal("true", result.Result);
    }

    [Fact]
    public async Task Evaluate_ListCount()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "list.Count"));

        Assert.Equal("3", result.Result);
    }

    // === Helpers ===

    private static string FindFixture(string name)
    {
        var fixturePath = FindPath($"tests/fixtures/{name}/bin/Debug/net10.0/{name}.dll");
        if (!File.Exists(fixturePath))
            throw new FileNotFoundException($"Fixture not built: {fixturePath}");
        return fixturePath;
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
