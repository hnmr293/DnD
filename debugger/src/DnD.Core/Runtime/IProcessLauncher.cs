namespace DnD.Core.Runtime;

using ClrDebug;

public record LaunchResult(
    CorDebug CorDebug,
    CorDebugProcess Process,
    Stream? StandardOutput = null,
    Stream? StandardError = null);

public interface IProcessLauncher
{
    LaunchResult Launch(
        string program, string[]? args, string? cwd,
        Dictionary<string, string>? env,
        CorDebugManagedCallback callback);

    LaunchResult Attach(
        int processId,
        CorDebugManagedCallback callback);
}
