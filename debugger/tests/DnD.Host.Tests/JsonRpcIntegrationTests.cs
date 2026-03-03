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

        // Register notification handlers before StartListening.
        // Notifications arrive as named params (e.g. {"exitCode": 0}),
        // so the handler parameter name must match the JSON property name.
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

    [Fact]
    public async Task Launch_ReturnsProcessId()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: "test.exe"));

        Assert.Equal(12345, result.ProcessId);
    }

    [Fact]
    public async Task SetBreakpoint_ReturnsBreakpoint()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: "Program.cs", Line: 10));

        Assert.True(result.Breakpoint.Verified);
        Assert.Equal("Program.cs", result.Breakpoint.File);
    }

    [Fact]
    public async Task GetStackTrace_ReturnsFrames()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest());

        Assert.Single(result.StackFrames);
        Assert.Equal("Main", result.StackFrames[0].Name);
    }

    [Fact]
    public async Task Evaluate_ReturnsResult()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "x + 1"));

        Assert.Equal("stub:x + 1", result.Result);
    }

    [Fact]
    public async Task Terminate_SendsExitedNotification()
    {
        await _rpc!.InvokeAsync("terminate");

        var exited = await _exitedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, exited.ExitCode);
    }

    [Fact]
    public async Task Detach_Succeeds()
    {
        await _rpc!.InvokeAsync("detach");
    }

    [Fact]
    public async Task GetBreakpoints_ReturnsEmptyList()
    {
        var result = await _rpc!.InvokeWithParameterObjectAsync<GetBreakpointsResponse>(
            "getBreakpoints", new { });

        Assert.Empty(result.Breakpoints);
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
