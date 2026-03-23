using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using DnD.Core;
using DnD.Host;
using StreamJsonRpc;

Console.Error.WriteLine("DnD.Host starting...");

// Log unhandled exceptions to a file before crashing
AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    var crashLog = Path.Combine(Path.GetTempPath(), "dnd-host-crash.log");
    File.WriteAllText(crashLog, $"{DateTime.Now:O}\n{e.ExceptionObject}");
};

// Orphan process prevention: monitor parent process and exit when it dies
var parentPidIndex = Array.IndexOf(args, "--parentPid");
if (parentPidIndex >= 0 && parentPidIndex + 1 < args.Length &&
    int.TryParse(args[parentPidIndex + 1], out var parentPid))
{
    try
    {
        var parentProcess = Process.GetProcessById(parentPid);
        parentProcess.EnableRaisingEvents = true;
        parentProcess.Exited += (s, e) =>
        {
            Console.Error.WriteLine($"Parent process {parentPid} exited, shutting down.");
            Environment.Exit(1);
        };
        // Check if already exited (race between GetProcessById and EnableRaisingEvents)
        if (parentProcess.HasExited)
        {
            Console.Error.WriteLine($"Parent process {parentPid} already exited.");
            Environment.Exit(1);
        }
        Console.Error.WriteLine($"Monitoring parent process {parentPid}.");
    }
    catch (ArgumentException)
    {
        Console.Error.WriteLine($"Parent process {parentPid} not found, shutting down.");
        Environment.Exit(1);
    }
}

var useStub = args.Contains("--stub");
IDebuggerEngine engine = useStub
    ? new StubDebuggerEngine()
    : new DebuggerEngine(new DnD.Core.Runtime.AutoProcessLauncher());

var formatter = new SystemTextJsonFormatter();
formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
formatter.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

var handler = new HeaderDelimitedMessageHandler(
    sendingStream: Console.OpenStandardOutput(),
    receivingStream: Console.OpenStandardInput(),
    formatter: formatter
);

var rpc = new JsonRpc(handler);

var target = new DebuggerRpcTarget(engine, rpc);

rpc.AddLocalRpcTarget(target, new JsonRpcTargetOptions
{
    MethodNameTransform = CommonMethodNameTransforms.CamelCase,
    NotifyClientOfEvents = false,
});

rpc.StartListening();

Console.Error.WriteLine("DnD.Host listening on stdio...");

await rpc.Completion;

Console.Error.WriteLine("DnD.Host shutting down.");

if (engine is IDisposable disposable)
    disposable.Dispose();
