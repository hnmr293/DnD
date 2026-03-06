namespace DnD.Core.Runtime;

using System.IO.Pipes;
using System.Runtime.InteropServices;
using ClrDebug;

public class CoreProcessLauncher : IProcessLauncher
{
    private readonly DbgShim _dbgShim;

    public CoreProcessLauncher()
    {
        var dbgShimPath = FindDbgShimPath();
        _dbgShim = new DbgShim(NativeLibrary.Load(dbgShimPath));
    }

    public CoreProcessLauncher(DbgShim dbgShim)
    {
        _dbgShim = dbgShim;
    }

    public LaunchResult Launch(
        string program, string[]? args, string? cwd,
        Dictionary<string, string>? env,
        CorDebugManagedCallback callback)
    {
        var commandLine = BuildCommandLine(program, args);

        // DbgShim's CreateProcessForLaunch calls Win32 CreateProcess which inherits
        // the parent's std handles. When DnD.Host uses stdin/stdout for JSON-RPC,
        // the debuggee's Console.WriteLine would corrupt the protocol stream.
        // Redirect the child's stdout/stderr to anonymous pipes we can read from.
        var (launchResultValue, stdoutRead, stderrRead) = CreateProcessWithCapturedOutput(commandLine, cwd);

        var pid = launchResultValue.ProcessId;
        var resumeHandle = launchResultValue.ResumeHandle;

        var tcs = new TaskCompletionSource<CorDebug>();

        var unregisterToken = _dbgShim.RegisterForRuntimeStartup(
            (int)pid,
            (pCordb, parameter, hr) =>
            {
                if (hr == HRESULT.S_OK && pCordb != null)
                {
                    tcs.TrySetResult(pCordb);
                }
                else
                {
                    tcs.TrySetException(new InvalidOperationException(
                        $"Failed to get ICorDebug from runtime startup. HRESULT: {hr}"));
                }
            },
            IntPtr.Zero);

        _dbgShim.ResumeProcess(resumeHandle);

        // Wait for the runtime to start (with timeout)
        if (!tcs.Task.Wait(TimeSpan.FromSeconds(30)))
        {
            _dbgShim.UnregisterForRuntimeStartup(unregisterToken);
            _dbgShim.CloseResumeHandle(resumeHandle);
            throw new TimeoutException("Timed out waiting for .NET runtime to start.");
        }

        _dbgShim.UnregisterForRuntimeStartup(unregisterToken);
        _dbgShim.CloseResumeHandle(resumeHandle);

        var corDebug = tcs.Task.Result;
        corDebug.Initialize();
        corDebug.SetManagedHandler(callback);

        var process = corDebug.DebugActiveProcess((int)pid, false);

        return new LaunchResult(corDebug, process, stdoutRead, stderrRead);
    }

    public LaunchResult Attach(
        int processId,
        CorDebugManagedCallback callback)
    {
        // For attach, use RegisterForRuntimeStartup to get the ICorDebug
        // This works if the runtime is already loaded
        var tcs = new TaskCompletionSource<CorDebug>();

        var unregisterToken = _dbgShim.RegisterForRuntimeStartup(
            processId,
            (pCordb, parameter, hr) =>
            {
                if (hr == HRESULT.S_OK && pCordb != null)
                {
                    tcs.TrySetResult(pCordb);
                }
                else
                {
                    tcs.TrySetException(new InvalidOperationException(
                        $"Failed to get ICorDebug for attach. HRESULT: {hr}"));
                }
            },
            IntPtr.Zero);

        // For an already-running process, the callback fires immediately
        if (!tcs.Task.Wait(TimeSpan.FromSeconds(10)))
        {
            _dbgShim.UnregisterForRuntimeStartup(unregisterToken);
            throw new TimeoutException("Timed out waiting to attach to .NET runtime.");
        }

        _dbgShim.UnregisterForRuntimeStartup(unregisterToken);

        var corDebug = tcs.Task.Result;
        corDebug.Initialize();
        corDebug.SetManagedHandler(callback);

        var process = corDebug.DebugActiveProcess(processId, false);

        // Attach can't capture output — the process already has its own handles
        return new LaunchResult(corDebug, process);
    }

    internal static string FindDbgShimPath()
    {
        // Search for dbgshim.dll in the output directory
        var assemblyDir = AppContext.BaseDirectory;
        var dbgShimInOutput = Path.Combine(assemblyDir, "dbgshim.dll");
        if (File.Exists(dbgShimInOutput))
            return dbgShimInOutput;

        // Search in NuGet packages folder
        var nugetDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget", "packages", "microsoft.diagnostics.dbgshim.win-x64");

        if (Directory.Exists(nugetDir))
        {
            var versions = Directory.GetDirectories(nugetDir)
                .OrderByDescending(d => d)
                .ToList();

            foreach (var versionDir in versions)
            {
                var nativePath = Path.Combine(versionDir, "runtimes", "win-x64", "native", "dbgshim.dll");
                if (File.Exists(nativePath))
                    return nativePath;
            }
        }

        throw new FileNotFoundException(
            "Could not find dbgshim.dll. Ensure Microsoft.Diagnostics.DbgShim.win-x64 NuGet package is installed.");
    }

    /// <summary>
    /// Creates the debuggee process with stdout/stderr redirected to anonymous pipes.
    /// Returns the pipe read-end streams so the caller can capture output.
    /// Stdin is redirected to NUL (debuggees don't read interactive input during debugging).
    /// </summary>
    private (CreateProcessForLaunchResult Result, Stream StdoutRead, Stream StderrRead)
        CreateProcessWithCapturedOutput(string commandLine, string? cwd)
    {
        const int STD_INPUT_HANDLE = -10;
        const int STD_OUTPUT_HANDLE = -11;
        const int STD_ERROR_HANDLE = -12;

        var origStdin = GetStdHandle(STD_INPUT_HANDLE);
        var origStdout = GetStdHandle(STD_OUTPUT_HANDLE);
        var origStderr = GetStdHandle(STD_ERROR_HANDLE);

        // AnonymousPipeServerStream(PipeDirection.In) creates:
        //   server end = read (our side), client end = write (child side)
        var stdoutPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderrPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        // NUL for stdin
        using var nulFile = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var nulHandle = nulFile.DangerousGetHandle();

        try
        {
            // Replace std handles so the child inherits our pipe write-ends
            SetStdHandle(STD_INPUT_HANDLE, nulHandle);
            SetStdHandle(STD_OUTPUT_HANDLE, stdoutPipe.ClientSafePipeHandle.DangerousGetHandle());
            SetStdHandle(STD_ERROR_HANDLE, stderrPipe.ClientSafePipeHandle.DangerousGetHandle());

            var result = _dbgShim.CreateProcessForLaunch(
                commandLine,
                bSuspendProcess: true,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: cwd);

            // Return the server (read) ends — these are the streams we'll read from
            return (result, stdoutPipe, stderrPipe);
        }
        catch
        {
            stdoutPipe.Dispose();
            stderrPipe.Dispose();
            throw;
        }
        finally
        {
            // Restore original handles for DnD.Host's own stdio
            SetStdHandle(STD_INPUT_HANDLE, origStdin);
            SetStdHandle(STD_OUTPUT_HANDLE, origStdout);
            SetStdHandle(STD_ERROR_HANDLE, origStderr);

            // Close the write-end handles in our process — only the child holds them now.
            // This ensures ReadLine() on the server end will return null when the child exits.
            stdoutPipe.DisposeLocalCopyOfClientHandle();
            stderrPipe.DisposeLocalCopyOfClientHandle();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    private static string BuildCommandLine(string program, string[]? args)
    {
        // For .NET Core apps, run via 'dotnet exec' if it's a DLL, or directly if it's an EXE
        var extension = Path.GetExtension(program).ToLowerInvariant();
        string baseCommand;

        if (extension == ".dll")
        {
            baseCommand = $"dotnet exec \"{program}\"";
        }
        else
        {
            baseCommand = $"\"{program}\"";
        }

        if (args is null || args.Length == 0)
            return baseCommand;

        return $"{baseCommand} {string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
    }
}
