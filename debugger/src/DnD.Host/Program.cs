using System.Text.Json;
using System.Text.Json.Serialization;
using DnD.Core;
using DnD.Host;
using StreamJsonRpc;

Console.Error.WriteLine("DnD.Host starting...");

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
