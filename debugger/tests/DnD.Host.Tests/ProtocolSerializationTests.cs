namespace DnD.Host.Tests;

using System.Text.Json;
using System.Text.Json.Serialization;
using DnD.Protocol;

/// <summary>
/// Verifies that all Protocol types serialize/deserialize correctly
/// with the same JsonSerializerOptions used by the DnD.Host server.
/// </summary>
public class ProtocolSerializationTests
{
    private static readonly JsonSerializerOptions Options;

    static ProtocolSerializationTests()
    {
        Options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        Options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    private static T RoundTrip<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        return JsonSerializer.Deserialize<T>(json, Options)!;
    }

    // === Models ===

    [Fact]
    public void Breakpoint_RoundTrip()
    {
        var bp = new Breakpoint(Id: 5, File: "test.cs", Line: 42, Verified: true);
        var result = RoundTrip(bp);
        Assert.Equal(bp, result);
    }

    [Fact]
    public void Breakpoint_CamelCasePropertyNames()
    {
        var bp = new Breakpoint(Id: 1, File: "a.cs", Line: 1, Verified: false);
        var json = JsonSerializer.Serialize(bp, Options);
        Assert.Contains("\"id\":", json);
        Assert.Contains("\"file\":", json);
        Assert.Contains("\"line\":", json);
        Assert.Contains("\"verified\":", json);
        Assert.DoesNotContain("\"Id\":", json);
    }

    [Fact]
    public void StackFrame_RequiredFieldsOnly()
    {
        var frame = new StackFrame(Id: 0, Name: "Main");
        var result = RoundTrip(frame);
        Assert.Equal(0, result.Id);
        Assert.Equal("Main", result.Name);
        Assert.Null(result.File);
        Assert.Null(result.Line);
        Assert.Null(result.Column);
        Assert.Null(result.ModuleId);
    }

    [Fact]
    public void StackFrame_AllFields()
    {
        var frame = new StackFrame(
            Id: 3, Name: "DoWork", File: "Worker.cs", Line: 50, Column: 8, ModuleId: "Worker.dll");
        var result = RoundTrip(frame);
        Assert.Equal(frame, result);
    }

    [Fact]
    public void Variable_WithType()
    {
        var v = new Variable(Name: "count", Value: "10", VariablesReference: 0, Type: "int");
        var result = RoundTrip(v);
        Assert.Equal(v, result);
    }

    [Fact]
    public void Variable_WithoutType()
    {
        var v = new Variable(Name: "x", Value: "hello", VariablesReference: 1);
        var result = RoundTrip(v);
        Assert.Equal("x", result.Name);
        Assert.Null(result.Type);
    }

    // === Enum serialization ===

    [Theory]
    [InlineData(StopReason.Breakpoint, "breakpoint")]
    [InlineData(StopReason.Step, "step")]
    [InlineData(StopReason.Pause, "pause")]
    [InlineData(StopReason.Exception, "exception")]
    [InlineData(StopReason.Entry, "entry")]
    public void StopReason_SerializesToCamelCase(StopReason reason, string expected)
    {
        var json = JsonSerializer.Serialize(reason, Options);
        Assert.Equal($"\"{expected}\"", json);
    }

    [Theory]
    [InlineData(OutputCategory.Stdout, "stdout")]
    [InlineData(OutputCategory.Stderr, "stderr")]
    [InlineData(OutputCategory.Console, "console")]
    public void OutputCategory_SerializesToCamelCase(OutputCategory category, string expected)
    {
        var json = JsonSerializer.Serialize(category, Options);
        Assert.Equal($"\"{expected}\"", json);
    }

    [Theory]
    [InlineData("\"breakpoint\"", StopReason.Breakpoint)]
    [InlineData("\"exception\"", StopReason.Exception)]
    public void StopReason_DeserializesFromCamelCase(string json, StopReason expected)
    {
        var result = JsonSerializer.Deserialize<StopReason>(json, Options);
        Assert.Equal(expected, result);
    }

    // === Requests ===

    [Fact]
    public void LaunchRequest_RequiredOnly()
    {
        var req = new LaunchRequest(Program: "app.exe");
        var result = RoundTrip(req);
        Assert.Equal("app.exe", result.Program);
        Assert.Null(result.Args);
        Assert.Null(result.Cwd);
        Assert.Null(result.Env);
        Assert.False(result.StopAtEntry);
    }

    [Fact]
    public void LaunchRequest_AllFields()
    {
        var req = new LaunchRequest(
            Program: "app.exe",
            Args: ["--verbose"],
            Cwd: @"C:\work",
            Env: new Dictionary<string, string> { ["KEY"] = "VAL" },
            StopAtEntry: true);
        var result = RoundTrip(req);
        Assert.Equal(req.Program, result.Program);
        Assert.Equal(req.Args, result.Args);
        Assert.Equal(req.Cwd, result.Cwd);
        Assert.Equal(req.Env, result.Env);
        Assert.True(result.StopAtEntry);
    }

    [Fact]
    public void AttachRequest_RoundTrip()
    {
        var req = new AttachRequest(ProcessId: 42);
        var result = RoundTrip(req);
        Assert.Equal(42, result.ProcessId);
    }

    [Fact]
    public void ContinueRequest_NullThreadId()
    {
        var req = new ContinueRequest();
        var result = RoundTrip(req);
        Assert.Null(result.ThreadId);
    }

    [Fact]
    public void ContinueRequest_WithThreadId()
    {
        var req = new ContinueRequest(ThreadId: 7);
        var result = RoundTrip(req);
        Assert.Equal(7, result.ThreadId);
    }

    [Fact]
    public void SetBreakpointRequest_RoundTrip()
    {
        var req = new SetBreakpointRequest(File: "Foo.cs", Line: 100);
        var result = RoundTrip(req);
        Assert.Equal("Foo.cs", result.File);
        Assert.Equal(100, result.Line);
    }

    [Fact]
    public void RemoveBreakpointRequest_RoundTrip()
    {
        var req = new RemoveBreakpointRequest(BreakpointId: 3);
        var result = RoundTrip(req);
        Assert.Equal(3, result.BreakpointId);
    }

    [Fact]
    public void EvaluateRequest_RequiredOnly()
    {
        var req = new EvaluateRequest(Expression: "x + 1");
        var result = RoundTrip(req);
        Assert.Equal("x + 1", result.Expression);
        Assert.Null(result.FrameId);
    }

    [Fact]
    public void EvaluateRequest_WithFrameId()
    {
        var req = new EvaluateRequest(Expression: "y", FrameId: 5);
        var result = RoundTrip(req);
        Assert.Equal(5, result.FrameId);
    }

    [Fact]
    public void GetVariablesRequest_RoundTrip()
    {
        var req = new GetVariablesRequest(VariablesReference: 99);
        var result = RoundTrip(req);
        Assert.Equal(99, result.VariablesReference);
    }

    // === Responses ===

    [Fact]
    public void LaunchResponse_RoundTrip()
    {
        var resp = new LaunchResponse(ProcessId: 12345);
        var result = RoundTrip(resp);
        Assert.Equal(12345, result.ProcessId);
    }

    [Fact]
    public void SetBreakpointResponse_RoundTrip()
    {
        var resp = new SetBreakpointResponse(
            Breakpoint: new Breakpoint(Id: 1, File: "x.cs", Line: 10, Verified: true));
        var result = RoundTrip(resp);
        Assert.Equal(1, result.Breakpoint.Id);
        Assert.True(result.Breakpoint.Verified);
    }

    [Fact]
    public void GetBreakpointsResponse_EmptyArray()
    {
        var resp = new GetBreakpointsResponse(Breakpoints: []);
        var result = RoundTrip(resp);
        Assert.Empty(result.Breakpoints);
    }

    [Fact]
    public void GetBreakpointsResponse_MultipleBreakpoints()
    {
        var resp = new GetBreakpointsResponse(Breakpoints: [
            new Breakpoint(1, "a.cs", 10, true),
            new Breakpoint(2, "b.cs", 20, false),
        ]);
        var result = RoundTrip(resp);
        Assert.Equal(2, result.Breakpoints.Length);
        Assert.False(result.Breakpoints[1].Verified);
    }

    [Fact]
    public void EvaluateResponse_WithType()
    {
        var resp = new EvaluateResponse(Result: "42", VariablesReference: 0, Type: "int");
        var result = RoundTrip(resp);
        Assert.Equal("42", result.Result);
        Assert.Equal("int", result.Type);
    }

    [Fact]
    public void EvaluateResponse_NullType()
    {
        var resp = new EvaluateResponse(Result: "hello", VariablesReference: 0);
        var result = RoundTrip(resp);
        Assert.Null(result.Type);
    }

    // === Notifications ===

    [Fact]
    public void StoppedNotification_RoundTrip()
    {
        var notif = new StoppedNotification(
            Reason: StopReason.Breakpoint, ThreadId: 1, Description: "Hit breakpoint");
        var result = RoundTrip(notif);
        Assert.Equal(StopReason.Breakpoint, result.Reason);
        Assert.Equal(1, result.ThreadId);
        Assert.Equal("Hit breakpoint", result.Description);
    }

    [Fact]
    public void StoppedNotification_EnumInJson()
    {
        var notif = new StoppedNotification(Reason: StopReason.Exception, ThreadId: 1);
        var json = JsonSerializer.Serialize(notif, Options);
        Assert.Contains("\"exception\"", json);
        Assert.DoesNotContain("\"Exception\"", json);
    }

    [Fact]
    public void StoppedNotification_NullDescription()
    {
        var notif = new StoppedNotification(Reason: StopReason.Step, ThreadId: 2);
        var result = RoundTrip(notif);
        Assert.Null(result.Description);
    }

    [Fact]
    public void ExitedNotification_RoundTrip()
    {
        var notif = new ExitedNotification(ExitCode: 1);
        var result = RoundTrip(notif);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void OutputNotification_RoundTrip()
    {
        var notif = new OutputNotification(Category: OutputCategory.Stderr, Output: "error msg");
        var result = RoundTrip(notif);
        Assert.Equal(OutputCategory.Stderr, result.Category);
        Assert.Equal("error msg", result.Output);
    }

    [Fact]
    public void OutputNotification_EnumInJson()
    {
        var notif = new OutputNotification(Category: OutputCategory.Stdout, Output: "hi");
        var json = JsonSerializer.Serialize(notif, Options);
        Assert.Contains("\"stdout\"", json);
    }

    // === Missing request types ===

    [Fact]
    public void StepInRequest_RoundTrip()
    {
        var req = new StepInRequest(ThreadId: 3);
        var result = RoundTrip(req);
        Assert.Equal(3, result.ThreadId);
    }

    [Fact]
    public void StepInRequest_NullThreadId()
    {
        var req = new StepInRequest();
        var result = RoundTrip(req);
        Assert.Null(result.ThreadId);
    }

    [Fact]
    public void StepOverRequest_RoundTrip()
    {
        var req = new StepOverRequest(ThreadId: 5);
        var result = RoundTrip(req);
        Assert.Equal(5, result.ThreadId);
    }

    [Fact]
    public void StepOutRequest_RoundTrip()
    {
        var req = new StepOutRequest(ThreadId: 8);
        var result = RoundTrip(req);
        Assert.Equal(8, result.ThreadId);
    }

    [Fact]
    public void GetStackTraceRequest_NullThreadId()
    {
        var req = new GetStackTraceRequest();
        var result = RoundTrip(req);
        Assert.Null(result.ThreadId);
    }

    [Fact]
    public void GetStackTraceRequest_WithThreadId()
    {
        var req = new GetStackTraceRequest(ThreadId: 2);
        var result = RoundTrip(req);
        Assert.Equal(2, result.ThreadId);
    }

    // === Missing response types ===

    [Fact]
    public void AttachResponse_RoundTrip()
    {
        var resp = new AttachResponse(ProcessId: 999);
        var result = RoundTrip(resp);
        Assert.Equal(999, result.ProcessId);
    }

    [Fact]
    public void GetStackTraceResponse_RoundTrip()
    {
        var resp = new GetStackTraceResponse(StackFrames: [
            new StackFrame(Id: 0, Name: "Main", File: "Program.cs", Line: 1),
            new StackFrame(Id: 1, Name: "Helper"),
        ]);
        var result = RoundTrip(resp);
        Assert.Equal(2, result.StackFrames.Length);
        Assert.Equal("Main", result.StackFrames[0].Name);
        Assert.Null(result.StackFrames[1].File);
    }

    [Fact]
    public void GetStackTraceResponse_EmptyArray()
    {
        var resp = new GetStackTraceResponse(StackFrames: []);
        var result = RoundTrip(resp);
        Assert.Empty(result.StackFrames);
    }

    [Fact]
    public void GetVariablesResponse_RoundTrip()
    {
        var resp = new GetVariablesResponse(Variables: [
            new Variable(Name: "x", Value: "42", VariablesReference: 0, Type: "int"),
            new Variable(Name: "y", Value: "hello", VariablesReference: 1),
        ]);
        var result = RoundTrip(resp);
        Assert.Equal(2, result.Variables.Length);
        Assert.Equal("int", result.Variables[0].Type);
        Assert.Null(result.Variables[1].Type);
    }

    [Fact]
    public void GetVariablesResponse_EmptyArray()
    {
        var resp = new GetVariablesResponse(Variables: []);
        var result = RoundTrip(resp);
        Assert.Empty(result.Variables);
    }

    // === Edge cases: special characters ===

    [Fact]
    public void EvaluateRequest_SpecialJsonCharacters()
    {
        var req = new EvaluateRequest(Expression: "obj.Name == \"hello\\nworld\"");
        var result = RoundTrip(req);
        Assert.Equal("obj.Name == \"hello\\nworld\"", result.Expression);
    }

    [Fact]
    public void Variable_ValueWithNewlines()
    {
        var v = new Variable(Name: "msg", Value: "line1\nline2\ttab", VariablesReference: 0);
        var result = RoundTrip(v);
        Assert.Equal("line1\nline2\ttab", result.Value);
    }

    [Fact]
    public void SetBreakpointRequest_UnicodeFile()
    {
        var req = new SetBreakpointRequest(File: "ソース/テスト.cs", Line: 10);
        var result = RoundTrip(req);
        Assert.Equal("ソース/テスト.cs", result.File);
    }

    [Fact]
    public void LaunchRequest_EmptyArgsArray()
    {
        var req = new LaunchRequest(Program: "a.exe", Args: []);
        var result = RoundTrip(req);
        Assert.NotNull(result.Args);
        Assert.Empty(result.Args!);
    }

    [Fact]
    public void LaunchRequest_EmptyEnvDictionary()
    {
        var req = new LaunchRequest(Program: "a.exe", Env: new Dictionary<string, string>());
        var result = RoundTrip(req);
        Assert.NotNull(result.Env);
        Assert.Empty(result.Env!);
    }

    // === Cross-deserialization (JSON string -> C# type) ===

    [Fact]
    public void LaunchRequest_DeserializeFromRawJson()
    {
        var json = """{"program":"test.exe","args":["a","b"],"stopAtEntry":true}""";
        var req = JsonSerializer.Deserialize<LaunchRequest>(json, Options)!;
        Assert.Equal("test.exe", req.Program);
        Assert.Equal(["a", "b"], req.Args);
        Assert.True(req.StopAtEntry);
        Assert.Null(req.Cwd);
    }

    [Fact]
    public void StoppedNotification_DeserializeFromRawJson()
    {
        var json = """{"reason":"breakpoint","threadId":5,"description":"line 10"}""";
        var notif = JsonSerializer.Deserialize<StoppedNotification>(json, Options)!;
        Assert.Equal(StopReason.Breakpoint, notif.Reason);
        Assert.Equal(5, notif.ThreadId);
        Assert.Equal("line 10", notif.Description);
    }

    [Fact]
    public void AttachRequest_DeserializeFromRawJson()
    {
        var json = """{"processId":42}""";
        var req = JsonSerializer.Deserialize<AttachRequest>(json, Options)!;
        Assert.Equal(42, req.ProcessId);
    }

    [Fact]
    public void OutputNotification_DeserializeFromRawJson()
    {
        var json = """{"category":"stderr","output":"error message"}""";
        var notif = JsonSerializer.Deserialize<OutputNotification>(json, Options)!;
        Assert.Equal(OutputCategory.Stderr, notif.Category);
        Assert.Equal("error message", notif.Output);
    }

    [Theory]
    [InlineData("\"stdout\"", OutputCategory.Stdout)]
    [InlineData("\"stderr\"", OutputCategory.Stderr)]
    [InlineData("\"console\"", OutputCategory.Console)]
    public void OutputCategory_DeserializesFromCamelCase(string json, OutputCategory expected)
    {
        var result = JsonSerializer.Deserialize<OutputCategory>(json, Options);
        Assert.Equal(expected, result);
    }
}
