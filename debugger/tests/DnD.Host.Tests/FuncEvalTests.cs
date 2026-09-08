namespace DnD.Host.Tests;

using DnD.Protocol;
using StreamJsonRpc;

/// <summary>
/// Reproduction tests for func-eval failures caused by the kind of locals that
/// live in the stopped frame.
///
/// RoslynEvaluator turns every variable of the frame into a parameter of a single
/// generated wrapper method and emits an assignment for each of them before the
/// expression itself. A local that cannot be passed or cannot be named in C#
/// therefore breaks every evaluate on that frame — including expressions that do
/// not mention the local at all, such as "1 + 1".
/// </summary>
public abstract class FuncEvalTestBase : DebugTestBase
{
    /// <summary>Fixture mode selecting which method the debuggee stops in.</summary>
    protected abstract string Mode { get; }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();

        var program = FindFixture("FuncEvalTest");
        await Rpc!.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program, Args: new[] { Mode }));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Pause, stopped.Reason);

        // Populate frame map (required before evaluate)
        await Rpc!.InvokeWithParameterObjectAsync<GetStackTraceResponse>(
            "getStackTrace", new GetStackTraceRequest(ThreadId: stopped.ThreadId));
    }

    protected Task<EvaluateResponse> EvaluateAsync(string expression) =>
        Rpc!.InvokeWithParameterObjectAsync<EvaluateResponse>(
            "evaluate", new EvaluateRequest(Expression: expression));
}

[Collection("DebugSession")]
[Trait("Category", "FuncEval")]
public class FuncEvalStructLocalTests : FuncEvalTestBase
{
    protected override string Mode => "struct";

    [Fact]
    public async Task Evaluate_ConstantExpression_WithStructLocalInFrame()
    {
        var result = await EvaluateAsync("1 + 1");

        Assert.Equal("2", result.Result);
    }

    [Fact]
    public async Task Evaluate_StructProperty()
    {
        var result = await EvaluateAsync("point.Sum");

        Assert.Equal("7", result.Result);
    }
}

[Collection("DebugSession")]
[Trait("Category", "FuncEval")]
public class FuncEvalEnumLocalTests : FuncEvalTestBase
{
    protected override string Mode => "enum";

    [Fact]
    public async Task Evaluate_ConstantExpression_WithEnumLocalInFrame()
    {
        var result = await EvaluateAsync("1 + 1");

        Assert.Equal("2", result.Result);
    }

    [Fact]
    public async Task Evaluate_EnumToString()
    {
        var result = await EvaluateAsync("color.ToString()");

        Assert.Equal("\"Green\"", result.Result);
    }
}

[Collection("DebugSession")]
[Trait("Category", "FuncEval")]
public class FuncEvalGenericStructLocalTests : FuncEvalTestBase
{
    protected override string Mode => "generic-struct";

    [Fact]
    public async Task Evaluate_ConstantExpression_WithGenericStructLocalInFrame()
    {
        var result = await EvaluateAsync("1 + 1");

        Assert.Equal("2", result.Result);
    }

    [Fact]
    public async Task Evaluate_GenericStructProperty()
    {
        var result = await EvaluateAsync("pair.Key");

        Assert.Equal("1", result.Result);
    }
}

/// <summary>
/// The frame holds a Dictionary&lt;string, Outer.Inner&gt;. Metadata reports the
/// nested type as "Inner", and the file's using directives bring an unrelated
/// Shadow.Inner into scope, so the generated wrapper binds the parameter to the
/// wrong type.
/// </summary>
[Collection("DebugSession")]
[Trait("Category", "FuncEval")]
public class FuncEvalShadowedNestedTypeTests : FuncEvalTestBase
{
    protected override string Mode => "nested-type";

    [Fact]
    public async Task Evaluate_ConstantExpression_WithShadowedNestedTypeLocalInFrame()
    {
        var result = await EvaluateAsync("1 + 1");

        Assert.Equal("2", result.Result);
    }

    [Fact]
    public async Task Evaluate_CollectionCount_WithShadowedNestedTypeArgument()
    {
        var result = await EvaluateAsync("map.Count");

        Assert.Equal("1", result.Result);
    }
}

/// <summary>
/// Same as above but the nested type's simple name is not visible anywhere else,
/// so the generated wrapper fails to compile instead of binding to a wrong type.
/// </summary>
[Collection("DebugSession")]
[Trait("Category", "FuncEval")]
public class FuncEvalUniqueNestedTypeTests : FuncEvalTestBase
{
    protected override string Mode => "nested-type-unique";

    [Fact]
    public async Task Evaluate_CollectionCount_WithUnresolvableNestedTypeArgument()
    {
        var result = await EvaluateAsync("map.Count");

        Assert.Equal("1", result.Result);
    }

    /// <summary>
    /// getVariables lists the local, so evaluate must resolve the same name.
    /// </summary>
    [Fact]
    public async Task Evaluate_LocalListedByGetVariables_IsResolvable()
    {
        var vars = await Rpc!.InvokeWithParameterObjectAsync<GetVariablesResponse>(
            "getVariables", new GetVariablesRequest());
        Assert.Contains("map", vars.Variables.Select(v => v.Name));

        var result = await EvaluateAsync("map.Count");

        Assert.Equal("1", result.Result);
    }
}
