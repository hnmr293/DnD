namespace DnD.Host.Tests;

using DnD.Protocol;
using StreamJsonRpc;

/// <summary>
/// Tests for debugging async methods — verifying that state machine fields
/// are unwrapped into user-visible variables for getVariables and evaluate.
/// </summary>
[Collection("DebugSession")]
[Trait("Category", "AsyncEval")]
public class AsyncEvalTests : DebugTestBase
{
    // Breakpoint line: Console.WriteLine(result) inside ProcessAsync, after await
    private const int BreakpointLine = 27;

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var program = FindFixture("AsyncTest");
        var sourceFile = FindFixtureSrc("AsyncTest", "Program.cs");

        // Set breakpoint after the await inside ProcessAsync
        await Rpc!.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: sourceFile, Line: BreakpointLine));

        // Launch — will hit breakpoint inside state machine's MoveNext
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped(TimeSpan.FromSeconds(30));
        Assert.Equal(StopReason.Breakpoint, stopped.Reason);

        // Populate frame map
        await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));
    }

    // === getVariables ===

    [Fact]
    public async Task GetVariables_InAsyncMethod_ShowsUnwrappedFields()
    {
        var vars = await Rpc!.InvokeWithParameterObjectAsync<GetVariablesResponse>(
            "getVariables", new GetVariablesRequest());

        var names = vars.Variables.Select(v => v.Name).ToList();

        // Captured parameters should appear with their original names
        Assert.Contains("count", names);
        Assert.Contains("message", names);

        // Captured local variables (hoisted from <name>5__N fields) should appear
        Assert.Contains("prefix", names);
        Assert.Contains("items", names);

        // Outer this should appear as "this"
        Assert.Contains("this", names);

        // Internal state machine fields should NOT appear
        Assert.DoesNotContain("<>1__state", names);
        Assert.DoesNotContain("<>t__builder", names);
        // No raw state machine type name
        Assert.DoesNotContain(names, n => n.Contains(">d__"));
    }

    // === evaluate: parameters ===

    [Fact]
    public async Task Evaluate_InAsyncMethod_CapturedIntParameter()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "count"));

        Assert.Equal("42", result.Result);
        Assert.Equal("int", result.Type);
    }

    [Fact]
    public async Task Evaluate_InAsyncMethod_CapturedStringParameter()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "message"));

        Assert.Equal("\"hello\"", result.Result);
        Assert.Equal("string", result.Type);
    }

    // === evaluate: captured locals ===

    [Fact]
    public async Task Evaluate_InAsyncMethod_CapturedLocal_Prefix()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "prefix"));

        Assert.Equal("\"[test-instance]\"", result.Result);
        Assert.Equal("string", result.Type);
    }

    [Fact]
    public async Task Evaluate_InAsyncMethod_CapturedLocal_ItemsCount()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "items.Count"));

        Assert.Equal("3", result.Result);
    }

    // === evaluate: outer this ===

    [Fact]
    public async Task Evaluate_InAsyncMethod_ThisResolvesToOuterClass()
    {
        // In the state machine, "this" should resolve to the MyService instance,
        // not the state machine struct
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "this._name"));

        Assert.Equal("\"test-instance\"", result.Result);
        Assert.Equal("string", result.Type);
    }
}
