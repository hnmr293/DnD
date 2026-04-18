namespace DnD.Core.Tests;

using DnD.Core.Inspection;

public class StateMachineHelperTests
{
    // ── IsStateMachineType ──────────────────────────────────────────

    [Theory]
    [InlineData("<ProcessAsync>d__3", true)]
    [InlineData("<Main>d__0", true)]
    [InlineData("<MoveNext>d__1", true)]
    [InlineData("<GetDataAsync>d__12", true)]
    // Nested type in metadata: Namespace.Class/<Method>d__N
    [InlineData("MyNamespace.MyClass/<ProcessAsync>d__3", true)]
    // Not state machines
    [InlineData("MyService", false)]
    [InlineData("System.String", false)]
    [InlineData("", false)]
    // Display class (compiler-generated but not state machine)
    [InlineData("<>c", false)]
    [InlineData("<>c__DisplayClass0_0", false)]
    // Iterator state machine (also uses d__ pattern)
    [InlineData("<GetItems>d__5", true)]
    public void IsStateMachineType_DetectsCorrectly(string typeName, bool expected)
    {
        Assert.Equal(expected, StateMachineHelper.IsStateMachineType(typeName));
    }

    // ── TryGetVariableName ──────────────────────────────────────────

    [Theory]
    // Hoisted local variables: <name>5__N → name
    [InlineData("<prefix>5__1", "prefix")]
    [InlineData("<items>5__2", "items")]
    [InlineData("<result>5__3", "result")]
    [InlineData("<matchedHub>5__1", "matchedHub")]
    [InlineData("<longVariableName>5__10", "longVariableName")]
    // Outer this: <>4__this → this
    [InlineData("<>4__this", "this")]
    public void TryGetVariableName_ReturnsName_ForUserVisibleFields(string fieldName, string expectedName)
    {
        Assert.True(StateMachineHelper.TryGetVariableName(fieldName, out var name));
        Assert.Equal(expectedName, name);
    }

    [Theory]
    // Internal state machine fields — should be hidden
    [InlineData("<>1__state")]
    [InlineData("<>t__builder")]
    [InlineData("<>u__1")]
    [InlineData("<>u__2")]
    // Other compiler-generated fields
    [InlineData("<>s__1")]
    [InlineData("<>7__wrap1")]
    public void TryGetVariableName_ReturnsFalse_ForInternalFields(string fieldName)
    {
        Assert.False(StateMachineHelper.TryGetVariableName(fieldName, out _));
    }

    [Theory]
    // Plain parameter names (no angle brackets) — returned as-is
    [InlineData("count", "count")]
    [InlineData("message", "message")]
    [InlineData("value", "value")]
    public void TryGetVariableName_ReturnsName_ForPlainParameters(string fieldName, string expectedName)
    {
        Assert.True(StateMachineHelper.TryGetVariableName(fieldName, out var name));
        Assert.Equal(expectedName, name);
    }
}
