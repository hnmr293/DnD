namespace DnD.Host.Tests;

using DnD.Protocol;
using StreamJsonRpc;

[Trait("Category", "Eval")]
public class EvalTests : DebugTestBase
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        // Launch EvalTest and wait for Debugger.Break()
        var program = FindFixture("EvalTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Pause, stopped.Reason);

        // Populate frame map (required before evaluate)
        await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));
    }

    // === Eval tests ===

    [Fact]
    public async Task Evaluate_SimpleVariable()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number"));

        Assert.Equal("42", result.Result);
        Assert.Equal("int", result.Type);
    }

    [Fact]
    public async Task Evaluate_PropertyAccess()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "greeting.Length"));

        Assert.Equal("13", result.Result);
    }

    [Fact]
    public async Task Evaluate_ToString()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "obj.ToString()"));

        Assert.Equal("\"TestClass(test, 100)\"", result.Result);
    }

    [Fact]
    public async Task Evaluate_MethodWithArgs()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "obj.Add(10)"));

        Assert.Equal("110", result.Result);
    }

    [Fact]
    public async Task Evaluate_IntArithmetic()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number + 1"));

        Assert.Equal("43", result.Result);
    }

    [Fact]
    public async Task Evaluate_Comparison()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number > 40"));

        Assert.Equal("true", result.Result);
    }

    [Fact]
    public async Task Evaluate_ListCount()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "list.Count"));

        Assert.Equal("3", result.Result);
    }

    // === Additional normal cases ===

    [Fact]
    public async Task Evaluate_StringVariable()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "greeting"));

        Assert.Equal("\"Hello, World!\"", result.Result);
        Assert.Equal("string", result.Type);
    }

    [Fact]
    public async Task Evaluate_IntLiteral()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "42"));

        Assert.Equal("42", result.Result);
        Assert.Equal("int", result.Type);
    }

    [Fact]
    public async Task Evaluate_BoolLiteral_True()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "true"));

        Assert.Equal("true", result.Result);
        Assert.Equal("bool", result.Type);
    }

    [Fact]
    public async Task Evaluate_BoolLiteral_False()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "false"));

        Assert.Equal("false", result.Result);
        Assert.Equal("bool", result.Type);
    }

    [Fact]
    public async Task Evaluate_NullLiteral()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "null"));

        Assert.Equal("null", result.Result);
        Assert.Equal("null", result.Type);
    }

    [Fact]
    public async Task Evaluate_StringLiteral()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "\"hello\""));

        Assert.Equal("\"hello\"", result.Result);
        Assert.Equal("string", result.Type);
    }

    [Fact]
    public async Task Evaluate_Subtraction()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number - 2"));

        Assert.Equal("40", result.Result);
    }

    [Fact]
    public async Task Evaluate_Multiplication()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number * 2"));

        Assert.Equal("84", result.Result);
    }

    [Fact]
    public async Task Evaluate_Division()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number / 2"));

        Assert.Equal("21", result.Result);
    }

    [Fact]
    public async Task Evaluate_Modulo()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number % 5"));

        Assert.Equal("2", result.Result);
    }

    [Fact]
    public async Task Evaluate_ComparisonLessThan()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number < 100"));

        Assert.Equal("true", result.Result);
    }

    [Fact]
    public async Task Evaluate_ComparisonFalse()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number > 100"));

        Assert.Equal("false", result.Result);
    }

    [Fact]
    public async Task Evaluate_Equality()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number == 42"));

        Assert.Equal("true", result.Result);
    }

    [Fact]
    public async Task Evaluate_Inequality()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "number != 42"));

        Assert.Equal("false", result.Result);
    }

    [Fact]
    public async Task Evaluate_ObjectProperty_Name()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "obj.Name"));

        Assert.Equal("\"test\"", result.Result);
        Assert.Equal("string", result.Type);
    }

    [Fact]
    public async Task Evaluate_ObjectProperty_Value()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "obj.Value"));

        Assert.Equal("100", result.Result);
    }

    [Fact]
    public async Task Evaluate_ParenthesizedExpression()
    {
        var result = await Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: "(number + 8) * 2"));

        Assert.Equal("100", result.Result);
    }

    // === Error cases ===

    [Fact]
    public async Task Evaluate_NonExistentVariable_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
                "evaluate", new EvaluateRequest(Expression: "nonexistent")));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task Evaluate_EmptyExpression_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
                "evaluate", new EvaluateRequest(Expression: "")));

        // Should throw some form of evaluation error
        Assert.NotNull(ex.Message);
    }

    [Fact]
    public async Task Evaluate_InvalidSyntax_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
                "evaluate", new EvaluateRequest(Expression: "+++")));

        Assert.NotNull(ex.Message);
    }

    [Fact]
    public async Task Evaluate_NonExistentMember_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
                "evaluate", new EvaluateRequest(Expression: "obj.NonExistent")));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task Evaluate_NonExistentMethod_ThrowsError()
    {
        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(
            () => Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
                "evaluate", new EvaluateRequest(Expression: "obj.NotAMethod()")));

        Assert.Contains("not found", ex.Message);
    }
}
