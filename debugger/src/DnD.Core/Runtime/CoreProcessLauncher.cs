namespace DnD.Core.Runtime;

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

        var launchResult = _dbgShim.CreateProcessForLaunch(
            commandLine,
            bSuspendProcess: true,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: cwd);

        var pid = launchResult.ProcessId;
        var resumeHandle = launchResult.ResumeHandle;

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

        return new LaunchResult(corDebug, process);
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
