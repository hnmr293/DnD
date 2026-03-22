namespace DnD.Core.Runtime;

using ClrDebug;

public class FrameworkProcessLauncher : IProcessLauncher
{
    public LaunchResult Launch(
        string program, string[]? args, string? cwd,
        Dictionary<string, string>? env,
        CorDebugManagedCallback callback)
    {
        var corDebug = CreateCorDebugForFramework();
        corDebug.SetManagedHandler(callback);

        var commandLine = BuildCommandLine(program, args);

        var startupInfo = new STARTUPINFOW();
        var processInfo = new PROCESS_INFORMATION();

        var process = corDebug.CreateProcess(
            lpApplicationName: null,
            lpCommandLine: commandLine,
            lpProcessAttributes: default,
            lpThreadAttributes: default,
            bInheritHandles: false,
            dwCreationFlags: CreateProcessFlags.CREATE_NO_WINDOW,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: cwd,
            lpStartupInfo: startupInfo,
            lpProcessInformation: ref processInfo,
            debuggingFlags: CorDebugCreateProcessFlags.DEBUG_NO_SPECIAL_OPTIONS);

        return new LaunchResult(corDebug, process);
    }

    public LaunchResult Attach(
        int processId,
        CorDebugManagedCallback callback)
    {
        var corDebug = CreateCorDebugForFramework();
        corDebug.SetManagedHandler(callback);

        corDebug.DebugActiveProcess(processId, false);
        var process = corDebug.GetProcess(processId);

        return new LaunchResult(corDebug, process);
    }

    /// <summary>
    /// Creates an ICorDebug instance for .NET Framework 4.x by explicitly requesting
    /// the v4.0 runtime via CLRMetaHost. The parameterless CorDebug() constructor
    /// tries to get the CLR for the current process (which is .NET 8), so we must
    /// use the CLRCreateInstance → GetRuntime → GetInterface path instead.
    /// </summary>
    private static CorDebug CreateCorDebugForFramework()
    {
        var metaHost = Extensions.CLRCreateInstance().CLRMetaHost;
        var runtimeInfo = metaHost.GetRuntime("v4.0.30319");
        var corDebug = runtimeInfo.GetInterface().CorDebug;
        corDebug.Initialize();
        return corDebug;
    }

    private static string BuildCommandLine(string program, string[]? args)
    {
        if (args is null || args.Length == 0)
            return $"\"{program}\"";
        return $"\"{program}\" {string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
    }
}
