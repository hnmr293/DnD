namespace DnD.Core.Runtime;

using ClrDebug;

public class AutoProcessLauncher : IProcessLauncher
{
    private IProcessLauncher? _inner;

    public LaunchResult Launch(
        string program, string[]? args, string? cwd,
        Dictionary<string, string>? env,
        CorDebugManagedCallback callback)
    {
        _inner = Detect(program);
        return _inner.Launch(program, args, cwd, env, callback);
    }

    public LaunchResult Attach(
        int processId,
        CorDebugManagedCallback callback)
    {
        // For attach, always try Core first since .NET Framework is legacy
        _inner = new CoreProcessLauncher();
        try
        {
            return _inner.Attach(processId, callback);
        }
        catch
        {
            _inner = new FrameworkProcessLauncher();
            return _inner.Attach(processId, callback);
        }
    }

    private static IProcessLauncher Detect(string program)
    {
        // Check for runtimeconfig.json which indicates .NET Core
        var basePath = Path.ChangeExtension(program, null);
        var runtimeConfigPath = basePath + ".runtimeconfig.json";

        if (File.Exists(runtimeConfigPath))
            return new CoreProcessLauncher();

        // If the program is a .dll, it's .NET Core (needs dotnet exec)
        if (Path.GetExtension(program).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            return new CoreProcessLauncher();

        // Check if it's a .NET Core EXE by looking for runtimeconfig.json
        var exeBasePath = Path.ChangeExtension(program, null);
        if (File.Exists(exeBasePath + ".runtimeconfig.json"))
            return new CoreProcessLauncher();

        // Default to .NET Framework
        return new FrameworkProcessLauncher();
    }
}
