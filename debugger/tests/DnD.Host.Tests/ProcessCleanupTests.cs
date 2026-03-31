namespace DnD.Host.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnD.Core;
using DnD.Core.Runtime;
using DnD.Core.Symbols;
using DnD.Protocol;
using StreamJsonRpc;

/// <summary>
/// Regression tests for GitHub Issue #15:
/// Intermittent COM ERROR_GEN_FAILURE (0x8007001F) in integration tests
/// due to incomplete process cleanup during teardown.
///
/// Three root cause bugs:
/// 1. TerminateProcessAsync() doesn't wait for debuggee exit before CorDebug.Terminate()
/// 2. EndSession() is not thread-safe (double-dispose race between callback and RPC threads)
/// 3. DebugTestBase.DisposeAsync() kills host before COM cleanup completes
/// </summary>
[Collection("DebugSession")]
[Trait("Category", "RegressionBug15")]
public class ProcessCleanupTests
{
    /// <summary>
    /// Directly reproduces Bug #2: EndSession() is not thread-safe.
    ///
    /// In production, two threads can call EndSession concurrently:
    /// - Callback thread: OnProcessExited → state.OnProcessExited → ctx.EndSession()
    /// - RPC thread: TerminateAsync → TerminateImplAsync → ctx.EndSession()
    ///
    /// EndSession uses "var s = _session; Volatile.Write(ref _session, null); if (s != null) s.Dispose()"
    /// which is NOT atomic. Both threads can read _session as non-null, then both call Dispose,
    /// causing double CorDebug.Terminate() on the same COM object.
    ///
    /// This test forces the race by calling EndSession from two threads simultaneously
    /// using a Barrier. The first call disposes the session (CorDebug.Terminate).
    /// The second call also tries to dispose the same session — double dispose.
    /// If this corrupts COM state, the next iteration's Launch fails.
    /// </summary>
    [Fact]
    public async Task ConcurrentEndSession_ShouldNotCorruptState()
    {
        var fixture = FindFixture("VariablesTest");
        if (!File.Exists(fixture))
            throw new FileNotFoundException($"Fixture not built: {fixture}");

        const int iterations = 20;
        var errors = new List<string>();

        for (int i = 0; i < iterations; i++)
        {
            var engine = new DebuggerEngine(new AutoProcessLauncher());
            try
            {
                var stoppedTcs = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                engine.Stopped += (_, _) => stoppedTcs.TrySetResult();

                // Launch debuggee and wait for it to be alive
                await engine.LaunchAsync(new LaunchRequest(Program: fixture));
                await stoppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

                // First, terminate the process so EndSession won't hit live COM objects
                // in unpredictable ways. TerminateProcessAsync kills the debuggee but
                // does NOT call EndSession — it returns to TerminateImplAsync which then
                // calls EndSession. We call TerminateProcessAsync directly via the interface.
                var ctx = (ISessionContext)engine;
                await ctx.TerminateProcessAsync();

                // Now force concurrent EndSession calls from two threads.
                // This reproduces the race between the callback thread and RPC thread.
                // With the buggy Volatile.Write pattern, both threads dispose the session.
                using var barrier = new Barrier(2);
                var t1 = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try { ctx.EndSession(); } catch { }
                });
                var t2 = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    try { ctx.EndSession(); } catch { }
                });
                await Task.WhenAll(t1, t2);
            }
            catch (Exception ex)
            {
                errors.Add($"Iteration {i}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { engine.Dispose(); } catch { }
            }
        }

        // If double-dispose corrupts COM state, some iterations will fail with:
        //   COMException: Error HRESULT 0x8007001F (ERROR_GEN_FAILURE)
        // on the Launch call in the next iteration.
        Assert.True(errors.Count == 0,
            $"Failed {errors.Count}/{iterations} iterations:\n" +
            string.Join("\n", errors));
    }

    /// <summary>
    /// Reproduces the race between TerminateAsync (RPC thread) and engine.Dispose
    /// (test thread). Both paths try to dispose the session:
    /// - TerminateAsync → TerminateImplAsync → EndSession → session.Dispose
    /// - engine.Dispose → _session.Dispose
    ///
    /// By firing both concurrently, we maximize the chance of double-dispose.
    /// </summary>
    [Fact]
    public async Task ConcurrentTerminateAndDispose_ShouldNotCorruptState()
    {
        var fixture = FindFixture("VariablesTest");
        if (!File.Exists(fixture))
            throw new FileNotFoundException($"Fixture not built: {fixture}");

        const int iterations = 30;
        var errors = new List<string>();

        for (int i = 0; i < iterations; i++)
        {
            var engine = new DebuggerEngine(new AutoProcessLauncher());
            try
            {
                var stoppedTcs = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                engine.Stopped += (_, _) => stoppedTcs.TrySetResult();

                await engine.LaunchAsync(new LaunchRequest(Program: fixture));
                await stoppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

                // Fire TerminateAsync and Dispose concurrently.
                // TerminateAsync → TerminateProcessAsync (blocks on COM calls) → EndSession
                // Dispose → Process.Stop + Process.Terminate + _session.Dispose
                // Both paths read _session as non-null → both try to dispose.
                var terminateTask = Task.Run(async () =>
                {
                    try { await engine.TerminateAsync(); } catch { }
                });
                try { engine.Dispose(); } catch { }

                await terminateTask;
            }
            catch (Exception ex)
            {
                errors.Add($"Iteration {i}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(errors.Count == 0,
            $"Failed {errors.Count}/{iterations} iterations:\n" +
            string.Join("\n", errors));
    }

    /// <summary>
    /// Verifies that after TerminateAsync(), the debuggee process has actually exited.
    /// This tests the invariant violated by Bug #1: TerminateProcessAsync() calls
    /// process.Terminate(0) but doesn't wait for the process to actually exit
    /// before EndSession() calls CorDebug.Terminate().
    ///
    /// The ICorDebug API contract states: "Do not call ICorDebug::Terminate before
    /// all debugged processes have exited."
    /// </summary>
    [Fact]
    public async Task TerminateAsync_DebuggeeProcessShouldBeDeadAfterReturn()
    {
        var fixture = FindFixture("VariablesTest");
        var engine = new DebuggerEngine(new AutoProcessLauncher());
        try
        {
            var stoppedTcs = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            engine.Stopped += (_, _) => stoppedTcs.TrySetResult();

            var result = await engine.LaunchAsync(
                new LaunchRequest(Program: fixture));
            var pid = result.ProcessId;

            await stoppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(IsProcessAlive(pid), "Debuggee should be alive before terminate");

            await engine.TerminateAsync();

            Assert.False(IsProcessAlive(pid),
                $"Debuggee process (PID {pid}) should be dead after TerminateAsync, " +
                "but it is still alive. This indicates TerminateProcessAsync() " +
                "does not wait for the debuggee to actually exit.");
        }
        finally
        {
            engine.Dispose();
        }
    }

    /// <summary>
    /// Reproduces the race using the SlowExitTest fixture, which keeps the
    /// process alive for 10 seconds after Debugger.Break() and delays CLR
    /// shutdown via a 2-second ProcessExit handler.
    ///
    /// Sequence:
    /// 1. Launch SlowExitTest → stopped at Debugger.Break()
    /// 2. Continue → debuggee resumes, starts Thread.Sleep(10000)
    /// 3. Immediately call engine.Dispose on the test thread
    ///    - engine.Dispose reads _currentState = Running (process is alive)
    ///    - Process.Stop(1000) halts the sleeping process
    ///    - Process.Terminate(0) hard-kills → triggers ExitProcess callback
    ///    - _session.Dispose() → CorDebug.Terminate()
    ///    - _session = null
    /// 4. Meanwhile, on the callback thread:
    ///    - ExitProcess fires (from step 3's Terminate)
    ///    - OnProcessExited → EndSession → reads _session
    ///    - If _session is still non-null (before step 3's null write),
    ///      session.Dispose() → second CorDebug.Terminate() → double dispose
    ///
    /// The 10-second sleep ensures the process is always alive at step 3.
    /// The ProcessExit delay further widens the window for natural exit paths.
    /// </summary>
    [Fact]
    public async Task SlowExitDispose_OnProcessExitedRacesWithDispose()
    {
        var fixture = FindFixture("SlowExitTest");
        if (!File.Exists(fixture))
            throw new FileNotFoundException($"Fixture not built: {fixture}");

        const int iterations = 30;
        var errors = new List<string>();

        for (int i = 0; i < iterations; i++)
        {
            var engine = new DebuggerEngine(new AutoProcessLauncher());
            try
            {
                var stoppedTcs = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                engine.Stopped += (_, _) => stoppedTcs.TrySetResult();

                // Launch — if previous iteration corrupted COM state, this fails
                await engine.LaunchAsync(new LaunchRequest(Program: fixture));
                await stoppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

                // Continue — debuggee starts Thread.Sleep(10000), stays alive
                await engine.ContinueAsync(new ContinueRequest());

                // Immediately dispose while process is running.
                // engine.Dispose calls Process.Terminate(0) which triggers
                // the ExitProcess callback on the callback thread while
                // the test thread is executing _session.Dispose().
            }
            catch (Exception ex)
            {
                errors.Add($"Iteration {i}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { engine.Dispose(); } catch { }
            }
        }

        Assert.True(errors.Count == 0,
            $"Failed {errors.Count}/{iterations} iterations:\n" +
            string.Join("\n", errors));
    }

    /// <summary>
    /// Variant: Continue the SlowExitTest, then fire TerminateAsync and
    /// Dispose concurrently. This creates three-way contention:
    /// - TerminateAsync thread: TerminateProcessAsync → EndSession
    /// - Dispose thread: Process.Stop → Process.Terminate → _session.Dispose
    /// - Callback thread: ExitProcess → OnProcessExited → EndSession
    /// </summary>
    [Fact]
    public async Task SlowExitConcurrentTerminateAndDispose_ShouldNotCorruptState()
    {
        var fixture = FindFixture("SlowExitTest");
        if (!File.Exists(fixture))
            throw new FileNotFoundException($"Fixture not built: {fixture}");

        const int iterations = 30;
        var errors = new List<string>();

        for (int i = 0; i < iterations; i++)
        {
            var engine = new DebuggerEngine(new AutoProcessLauncher());
            try
            {
                var stoppedTcs = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                engine.Stopped += (_, _) => stoppedTcs.TrySetResult();

                await engine.LaunchAsync(new LaunchRequest(Program: fixture));
                await stoppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

                // Continue — process stays alive (sleeping for 10 seconds)
                await engine.ContinueAsync(new ContinueRequest());

                // Fire TerminateAsync and Dispose concurrently while process is alive
                var terminateTask = Task.Run(async () =>
                {
                    try { await engine.TerminateAsync(); } catch { }
                });
                try { engine.Dispose(); } catch { }
                await terminateTask;
            }
            catch (Exception ex)
            {
                errors.Add($"Iteration {i}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.True(errors.Count == 0,
            $"Failed {errors.Count}/{iterations} iterations:\n" +
            string.Join("\n", errors));
    }

    /// <summary>
    /// Stress test: rapidly cycles launch/terminate to catch any residual
    /// COM state corruption from incomplete cleanup.
    /// Uses DebuggerEngine directly (no host process overhead).
    /// </summary>
    [Fact]
    public async Task RapidLaunchTerminateCycles_DirectEngine_ShouldNotCauseGenFailure()
    {
        var fixture = FindFixture("VariablesTest");
        if (!File.Exists(fixture))
            throw new FileNotFoundException($"Fixture not built: {fixture}");

        const int iterations = 30;
        var errors = new List<string>();

        for (int i = 0; i < iterations; i++)
        {
            var engine = new DebuggerEngine(new AutoProcessLauncher());
            try
            {
                var stoppedTcs = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                engine.Stopped += (_, _) => stoppedTcs.TrySetResult();

                await engine.LaunchAsync(new LaunchRequest(Program: fixture));
                await stoppedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                await engine.TerminateAsync();
            }
            catch (Exception ex)
            {
                errors.Add($"Iteration {i}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                engine.Dispose();
            }
        }

        Assert.True(errors.Count == 0,
            $"Failed {errors.Count}/{iterations} iterations:\n" +
            string.Join("\n", errors));
    }

    /// <summary>
    /// Integration test via DnD.Host process with aggressive teardown,
    /// matching the real DebugTestBase.DisposeAsync() behavior.
    /// </summary>
    [Fact]
    public async Task RapidLaunchTerminateCycles_ViaHost_ShouldNotCauseGenFailure()
    {
        var fixture = FindFixture("VariablesTest");
        if (!File.Exists(fixture))
            throw new FileNotFoundException($"Fixture not built: {fixture}");

        const int iterations = 20;
        var errors = new List<string>();

        for (int i = 0; i < iterations; i++)
        {
            Process? host = null;
            JsonRpc? rpc = null;
            BlockingCollection<StoppedNotification>? stoppedQueue = null;
            try
            {
                (host, rpc, stoppedQueue, _) = StartHost();

                var result = await rpc.InvokeWithParameterObjectAsync<LaunchResponse>(
                    "launch", new LaunchRequest(Program: fixture));
                Assert.True(result.ProcessId > 0, $"Iteration {i}: invalid PID");

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                stoppedQueue.Take(cts.Token);

                await rpc.InvokeAsync("terminate");

                rpc.Dispose();
                rpc = null;

                if (!host.HasExited)
                {
                    host.Kill();
                    await host.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Iteration {i}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { rpc?.Dispose(); } catch { }
                try
                {
                    if (host is { HasExited: false })
                    {
                        host.Kill();
                        await host.WaitForExitAsync();
                    }
                }
                catch { }
                host?.Dispose();
                stoppedQueue?.Dispose();
            }
        }

        Assert.True(errors.Count == 0,
            $"Failed {errors.Count}/{iterations} iterations:\n" +
            string.Join("\n", errors));
    }

    /// <summary>
    /// Regression test for Bug #2: calls the ACTUAL EndSession() method
    /// from two threads concurrently and detects double-dispose through
    /// a tracking ISymbolReader injected into DebugSession.Modules.
    ///
    /// Mechanism:
    /// 1. Create a DebugSession with null COM objects and a tracking reader in Modules
    /// 2. Inject it into DebuggerEngine._session via reflection
    /// 3. Two threads call ctx.EndSession() concurrently
    /// 4. DebugSession.Dispose() iterates Modules and calls reader.Dispose()
    /// 5. If both threads enter EndSession's if-block (double-dispose),
    ///    the tracking reader's DisposeCount exceeds 1
    ///
    /// With the buggy Volatile.Write pattern, both threads read _session
    /// as non-null, both call session.Dispose(), and the tracking reader
    /// is disposed twice. With Interlocked.Exchange, only one thread
    /// gets the non-null session, so the reader is disposed exactly once.
    /// </summary>
    [Fact]
    public void EndSession_ConcurrentCalls_DisposesSessionExactlyOnce()
    {
        if (Environment.ProcessorCount < 2)
            return;

        const int iterations = 2_000;
        var engine = new DebuggerEngine(new AutoProcessLauncher());
        var ctx = (ISessionContext)engine;
        var sessionField = typeof(DebuggerEngine).GetField("_session",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        var trackers = new DisposeTrackingReader[iterations];

        // 3 participants: main thread (setup) + 2 worker threads
        using var barrier = new Barrier(3);

        void Worker()
        {
            for (int i = 0; i < iterations; i++)
            {
                barrier.SignalAndWait(); // wait for setup
                try { ctx.EndSession(); } catch { }
                barrier.SignalAndWait(); // signal done
            }
        }

        var t1 = new Thread(Worker) { IsBackground = true };
        var t2 = new Thread(Worker) { IsBackground = true };
        t1.Start();
        t2.Start();

        for (int i = 0; i < iterations; i++)
        {
            // Inject a dummy session with a tracking reader via reflection
            var session = new DebugSession(null!, null!, null!);
            trackers[i] = new DisposeTrackingReader();
            session.Modules["probe"] = (null!, trackers[i]);
            sessionField.SetValue(engine, session);

            barrier.SignalAndWait(); // release workers
            barrier.SignalAndWait(); // wait for workers to finish
        }

        t1.Join();
        t2.Join();

        int doubleDisposeCount = trackers.Count(t => t.DisposeCount > 1);

        // If EndSession uses non-atomic Volatile.Write, both threads can
        // read _session as non-null and both call session.Dispose(), causing
        // the tracking reader to be disposed more than once.
        // With Interlocked.Exchange, only one thread gets the session.
        Assert.Equal(0, doubleDisposeCount);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// ISymbolReader implementation that tracks Dispose() call count.
    /// Injected into DebugSession.Modules to detect double-dispose
    /// when EndSession() is called concurrently.
    /// </summary>
    private sealed class DisposeTrackingReader : ISymbolReader
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
        public SequencePointInfo? ResolveBreakpoint(string filePath, int line) => null;
        public SequencePointInfo? ResolveSourceLocation(int methodToken, int ilOffset) => null;
        public IReadOnlyList<SequencePointInfo> GetSequencePoints(int methodToken) => [];
        public IReadOnlyList<DnD.Core.Symbols.LocalVariableInfo> GetLocalVariables(int methodToken, int ilOffset) => [];
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static (Process host, JsonRpc rpc,
        BlockingCollection<StoppedNotification> stoppedQueue,
        TaskCompletionSource<ExitedNotification> exitedTcs) StartHost()
    {
        var hostProject = FindPath("src/DnD.Host/DnD.Host.csproj");

        var host = new Process
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

        host.Start();

        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        var handler = new HeaderDelimitedMessageHandler(
            sendingStream: host.StandardInput.BaseStream,
            receivingStream: host.StandardOutput.BaseStream,
            formatter: formatter);

        var rpc = new JsonRpc(handler);
        var stoppedQueue = new BlockingCollection<StoppedNotification>();
        var exitedTcs = new TaskCompletionSource<ExitedNotification>();

        rpc.AddLocalRpcMethod("stopped",
            (StopReason reason, int threadId, string? description, int? breakpointId) =>
            {
                stoppedQueue.Add(new StoppedNotification(reason, threadId, description, breakpointId));
            });

        rpc.AddLocalRpcMethod("exited", (int exitCode) =>
        {
            exitedTcs.TrySetResult(new ExitedNotification(ExitCode: exitCode));
        });

        rpc.StartListening();

        return (host, rpc, stoppedQueue, exitedTcs);
    }

    private static string FindFixture(string name)
    {
        var tfm = "net10.0";
        return FindPath($"tests/fixtures/{name}/bin/Debug/{tfm}/{name}.dll");
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
