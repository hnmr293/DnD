namespace DnD.Core.Runtime;

using ClrDebug;

public class FrameworkProcessLauncher : IProcessLauncher
{
    public LaunchResult Launch(
        string program, string[]? args, string? cwd,
        Dictionary<string, string>? env,
        CorDebugManagedCallback callback)
    {
        var corDebug = new CorDebug();
        corDebug.Initialize();
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
            dwCreationFlags: CreateProcessFlags.CREATE_NEW_CONSOLE,
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
        var corDebug = new CorDebug();
        corDebug.Initialize();
        corDebug.SetManagedHandler(callback);

        corDebug.DebugActiveProcess(processId, false);
        var process = corDebug.GetProcess(processId);

        return new LaunchResult(corDebug, process);
    }

    private static string BuildCommandLine(string program, string[]? args)
    {
        if (args is null || args.Length == 0)
            return $"\"{program}\"";
        return $"\"{program}\" {string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
    }
}
