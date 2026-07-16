namespace DnD.Host.Tests;

using DnD.Protocol;
using StreamJsonRpc;

[Collection("DebugSession")]
[Trait("Category", "Eval")]
public class DuplicateAssemblyEvalTests : DebugTestBase
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Launch DuplicateAssemblyTest, which loads two copies of itself with the
        // same simple name into separate AssemblyLoadContexts before Debugger.Break()
        var program = FindFixture("DuplicateAssemblyTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Pause, stopped.Reason);

        // Populate frame map (required before evaluate)
        await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));
    }

    [Fact]
    public async Task Evaluate_Literal_WithDuplicateSimpleNameAssemblies()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "1+1"));

        Assert.Equal("2", result.Result);
        Assert.Equal("int", result.Type);
    }

    [Fact]
    public async Task Evaluate_LocalVariableExpression_WithDuplicateSimpleNameAssemblies()
    {
        // "number + 1" bypasses the SimpleEvaluator fast path and forces
        // Roslyn compilation with the frame's locals
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number + 1"));

        Assert.Equal("43", result.Result);
        Assert.Equal("int", result.Type);
    }
}
