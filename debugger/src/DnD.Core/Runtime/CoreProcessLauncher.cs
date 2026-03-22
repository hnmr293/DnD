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

        var (pid, hThread, hProcess, stdoutRead, stderrRead) =
            CreateProcessWithCapturedOutput(commandLine, cwd);

        var tcs = new TaskCompletionSource<CorDebug>();

        var unregisterToken = _dbgShim.RegisterForRuntimeStartup(
            pid,
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

        ResumeThread(hThread);

        // Wait for the runtime to start (with timeout)
        if (!tcs.Task.Wait(TimeSpan.FromSeconds(30)))
        {
            _dbgShim.UnregisterForRuntimeStartup(unregisterToken);
            CloseHandle(hThread);
            CloseHandle(hProcess);
            throw new TimeoutException("Timed out waiting for .NET runtime to start.");
        }

        _dbgShim.UnregisterForRuntimeStartup(unregisterToken);
        CloseHandle(hThread);
        CloseHandle(hProcess);

        var corDebug = tcs.Task.Result;
        corDebug.Initialize();
        corDebug.SetManagedHandler(callback);

        var process = corDebug.DebugActiveProcess(pid, false);

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
    /// Uses CreateProcessW directly (instead of DbgShim.CreateProcessForLaunch) to
    /// control creation flags — CREATE_NO_WINDOW prevents console windows from appearing.
    /// STARTF_USESTDHANDLES passes the pipe handles to the child process.
    /// </summary>
    private (int ProcessId, IntPtr ThreadHandle, IntPtr ProcessHandle, Stream StdoutRead, Stream StderrRead)
        CreateProcessWithCapturedOutput(string commandLine, string? cwd)
    {
        var stdoutPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderrPipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        // NUL for stdin — mark inheritable so the child can use it
        using var nulFile = File.OpenHandle("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var nulHandle = nulFile.DangerousGetHandle();
        SetHandleInformation(nulHandle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT);

        try
        {
            var startupInfo = new STARTUPINFOW();
            startupInfo.cb = Marshal.SizeOf<STARTUPINFOW>();
            startupInfo.dwFlags = STARTF.STARTF_USESTDHANDLES;
            startupInfo.hStdInput = nulHandle;
            startupInfo.hStdOutput = stdoutPipe.ClientSafePipeHandle.DangerousGetHandle();
            startupInfo.hStdError = stderrPipe.ClientSafePipeHandle.DangerousGetHandle();

            if (!CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                true,
                CreateProcessFlags.CREATE_NO_WINDOW | CreateProcessFlags.CREATE_SUSPENDED,
                IntPtr.Zero,
                cwd,
                ref startupInfo,
                out var processInfo))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            return (processInfo.dwProcessId, processInfo.hThread, processInfo.hProcess,
                stdoutPipe, stderrPipe);
        }
        catch
        {
            stdoutPipe.Dispose();
            stderrPipe.Dispose();
            throw;
        }
        finally
        {
            // Close the write-end handles in our process — only the child holds them now.
            // This ensures ReadLine() on the server end will return null when the child exits.
            stdoutPipe.DisposeLocalCopyOfClientHandle();
            stderrPipe.DisposeLocalCopyOfClientHandle();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        CreateProcessFlags dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, int dwFlags);

    private const int HANDLE_FLAG_INHERIT = 0x00000001;

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
