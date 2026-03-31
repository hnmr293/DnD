namespace DnD.Core.Runtime;

using System.IO.Pipes;
using System.Runtime.InteropServices;
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
            var processInfo = new PROCESS_INFORMATION();

            var process = corDebug.CreateProcess(
                lpApplicationName: null,
                lpCommandLine: commandLine,
                lpProcessAttributes: default,
                lpThreadAttributes: default,
                bInheritHandles: true,
                dwCreationFlags: CreateProcessFlags.CREATE_NO_WINDOW,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: cwd,
                lpStartupInfo: startupInfo,
                lpProcessInformation: ref processInfo,
                debuggingFlags: CorDebugCreateProcessFlags.DEBUG_NO_SPECIAL_OPTIONS);

            return new LaunchResult(corDebug, process, stdoutPipe, stderrPipe);
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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, int dwMask, int dwFlags);

    private const int HANDLE_FLAG_INHERIT = 0x00000001;

    private static string BuildCommandLine(string program, string[]? args)
    {
        if (args is null || args.Length == 0)
            return $"\"{program}\"";
        return $"\"{program}\" {string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}";
    }
}
