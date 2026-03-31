namespace DnD.Host.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnD.Protocol;
using StreamJsonRpc;

public abstract class DebugTestBase : IAsyncLifetime
{
    private Process? _hostProcess;
    protected JsonRpc? Rpc;
    protected readonly BlockingCollection<StoppedNotification> StoppedQueue = new();
    protected readonly TaskCompletionSource<ExitedNotification> ExitedTcs = new();

    public virtual async Task InitializeAsync()
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

        Rpc = new JsonRpc(handler);

        Rpc.AddLocalRpcMethod("stopped", (StopReason reason, int threadId, string? description, int? breakpointId) =>
        {
            StoppedQueue.Add(new StoppedNotification(reason, threadId, description, breakpointId));
        });

        Rpc.AddLocalRpcMethod("exited", (int exitCode) =>
        {
            ExitedTcs.TrySetResult(new ExitedNotification(ExitCode: exitCode));
        });

        Rpc.StartListening();

        await Task.CompletedTask;
    }

    public virtual async Task DisposeAsync()
    {
        try
        {
            if (Rpc != null)
            {
                try { await Rpc.InvokeAsync("terminate"); }
                catch { }
            }
        }
        catch { }

        Rpc?.Dispose();
        if (_hostProcess is { HasExited: false })
        {
            // Wait for graceful exit (COM cleanup) before resorting to kill
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _hostProcess.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                _hostProcess.Kill();
                await _hostProcess.WaitForExitAsync();
            }
        }
        _hostProcess?.Dispose();
        StoppedQueue.Dispose();
    }

    protected virtual string TargetFramework => "net10.0";

    protected StoppedNotification WaitForStopped(TimeSpan? timeout = null)
    {
        var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(15));
        return StoppedQueue.Take(cts.Token);
    }

    protected string FindFixture(string name)
    {
        var tfm = TargetFramework;
        var ext = tfm.StartsWith("net4") ? ".exe" : ".dll";
        var fixturePath = FindPath($"tests/fixtures/{name}/bin/Debug/{tfm}/{name}{ext}");
        if (!File.Exists(fixturePath))
            throw new FileNotFoundException($"Fixture not built: {fixturePath}");
        return fixturePath;
    }

    protected static string FindFixtureSrc(string name, string file)
    {
        return FindPath($"tests/fixtures/{name}/{file}");
    }

    protected static string FindPath(string relativePath)
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
