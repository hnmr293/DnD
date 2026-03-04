using DnD.Core.Inspection;
using static DnD.Core.Inspection.ExpressionParser;

namespace DnD.Core.Tests;

public class ExpressionParserTests
{
    [Fact]
    public void Parse_SimpleName()
    {
        var node = ExpressionParser.Parse("x");
        Assert.IsType<NameNode>(node);
        Assert.Equal("x", ((NameNode)node).Name);
    }

    [Fact]
    public void Parse_DottedName()
    {
        var node = ExpressionParser.Parse("obj.Name");
        var member = Assert.IsType<MemberAccessNode>(node);
        Assert.Equal("Name", member.MemberName);
        Assert.Equal("obj", ((NameNode)member.Object).Name);
    }

    [Fact]
    public void Parse_ChainedDots()
    {
        var node = ExpressionParser.Parse("a.b.c");
        var outer = Assert.IsType<MemberAccessNode>(node);
        Assert.Equal("c", outer.MemberName);
        var inner = Assert.IsType<MemberAccessNode>(outer.Object);
        Assert.Equal("b", inner.MemberName);
        Assert.Equal("a", ((NameNode)inner.Object).Name);
    }

    [Fact]
    public void Parse_ArrayIndex()
    {
        var node = ExpressionParser.Parse("arr[0]");
        var idx = Assert.IsType<IndexAccessNode>(node);
        Assert.Equal("arr", ((NameNode)idx.Object).Name);
        var lit = Assert.IsType<LiteralNode>(idx.Index);
        Assert.Equal(0, lit.Value);
    }

    [Fact]
    public void Parse_StringIndex()
    {
        var node = ExpressionParser.Parse("dict[\"key\"]");
        var idx = Assert.IsType<IndexAccessNode>(node);
        Assert.Equal("dict", ((NameNode)idx.Object).Name);
        var lit = Assert.IsType<LiteralNode>(idx.Index);
        Assert.Equal("key", lit.Value);
    }

    [Fact]
    public void Parse_MethodCallNoArgs()
    {
        var node = ExpressionParser.Parse("obj.ToString()");
        var call = Assert.IsType<MethodCallNode>(node);
        Assert.Equal("ToString", call.MethodName);
        Assert.Equal("obj", ((NameNode)call.Object).Name);
        Assert.Empty(call.Arguments);
    }

    [Fact]
    public void Parse_MethodCallWithArgs()
    {
        var node = ExpressionParser.Parse("str.Substring(0, 5)");
        var call = Assert.IsType<MethodCallNode>(node);
        Assert.Equal("Substring", call.MethodName);
        Assert.Equal(2, call.Arguments.Length);
        Assert.Equal(0, ((LiteralNode)call.Arguments[0]).Value);
        Assert.Equal(5, ((LiteralNode)call.Arguments[1]).Value);
    }

    [Fact]
    public void Parse_IntLiteral()
    {
        var node = ExpressionParser.Parse("42");
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Equal(42, lit.Value);
        Assert.Equal(LiteralKind.Int, lit.Kind);
    }

    [Fact]
    public void Parse_DoubleLiteral()
    {
        var node = ExpressionParser.Parse("3.14");
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Equal(3.14, lit.Value);
        Assert.Equal(LiteralKind.Double, lit.Kind);
    }

    [Fact]
    public void Parse_StringLiteral()
    {
        var node = ExpressionParser.Parse("\"hello\"");
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Equal("hello", lit.Value);
        Assert.Equal(LiteralKind.String, lit.Kind);
    }

    [Fact]
    public void Parse_BoolLiterals()
    {
        var t = ExpressionParser.Parse("true");
        Assert.Equal(true, ((LiteralNode)t).Value);

        var f = ExpressionParser.Parse("false");
        Assert.Equal(false, ((LiteralNode)f).Value);
    }

    [Fact]
    public void Parse_NullLiteral()
    {
        var node = ExpressionParser.Parse("null");
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Null(lit.Value);
        Assert.Equal(LiteralKind.Null, lit.Kind);
    }

    [Fact]
    public void Parse_Addition()
    {
        var node = ExpressionParser.Parse("a + b");
        var bin = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Add, bin.Op);
        Assert.Equal("a", ((NameNode)bin.Left).Name);
        Assert.Equal("b", ((NameNode)bin.Right).Name);
    }

    [Fact]
    public void Parse_ArithmeticPrecedence()
    {
        // a + b * c  =>  a + (b * c)
        var node = ExpressionParser.Parse("a + b * c");
        var add = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Add, add.Op);
        Assert.Equal("a", ((NameNode)add.Left).Name);
        var mul = Assert.IsType<BinaryOpNode>(add.Right);
        Assert.Equal(BinaryOp.Mul, mul.Op);
    }

    [Fact]
    public void Parse_ParenthesizedExpression()
    {
        // (a + b) * c  =>  (a + b) * c
        var node = ExpressionParser.Parse("(a + b) * c");
        var mul = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Mul, mul.Op);
        var add = Assert.IsType<BinaryOpNode>(mul.Left);
        Assert.Equal(BinaryOp.Add, add.Op);
    }

    [Fact]
    public void Parse_ComparisonOperators()
    {
        var node = ExpressionParser.Parse("x > 40");
        var bin = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Gt, bin.Op);

        node = ExpressionParser.Parse("x <= 40");
        bin = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.LtEq, bin.Op);
    }

    [Fact]
    public void Parse_EqualityOperators()
    {
        var node = ExpressionParser.Parse("x == 42");
        var bin = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Eq, bin.Op);

        node = ExpressionParser.Parse("x != null");
        bin = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.NotEq, bin.Op);
    }

    [Fact]
    public void Parse_LogicalOperators()
    {
        var node = ExpressionParser.Parse("a && b || c");
        var or = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Or, or.Op);
        var and = Assert.IsType<BinaryOpNode>(or.Left);
        Assert.Equal(BinaryOp.And, and.Op);
    }

    [Fact]
    public void Parse_Cast()
    {
        var node = ExpressionParser.Parse("(int)x");
        var cast = Assert.IsType<CastNode>(node);
        Assert.Equal("int", cast.TypeName);
        Assert.Equal("x", ((NameNode)cast.Operand).Name);
    }

    [Fact]
    public void Parse_ComplexExpression()
    {
        // obj.List[0].ToString()
        var node = ExpressionParser.Parse("obj.List[0].ToString()");
        var call = Assert.IsType<MethodCallNode>(node);
        Assert.Equal("ToString", call.MethodName);
        var idx = Assert.IsType<IndexAccessNode>(call.Object);
        var member = Assert.IsType<MemberAccessNode>(idx.Object);
        Assert.Equal("List", member.MemberName);
        Assert.Equal("obj", ((NameNode)member.Object).Name);
    }

    [Fact]
    public void Parse_IntArithmetic()
    {
        var node = ExpressionParser.Parse("number + 1");
        var bin = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Add, bin.Op);
        Assert.Equal("number", ((NameNode)bin.Left).Name);
        Assert.Equal(1, ((LiteralNode)bin.Right).Value);
    }

    [Fact]
    public void Parse_EmptyExpression_Throws()
    {
        Assert.Throws<FormatException>(() => ExpressionParser.Parse(""));
    }

    [Fact]
    public void Parse_UnterminatedString_Throws()
    {
        Assert.Throws<FormatException>(() => ExpressionParser.Parse("\"hello"));
    }

    [Fact]
    public void Parse_InvalidToken_Throws()
    {
        Assert.Throws<FormatException>(() => ExpressionParser.Parse("@invalid"));
    }

    [Fact]
    public void Parse_EscapedString()
    {
        var node = ExpressionParser.Parse("\"hello\\nworld\"");
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Equal("hello\nworld", lit.Value);
    }

    [Fact]
    public void Parse_NegativeNumber()
    {
        var node = ExpressionParser.Parse("-42");
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Equal(-42, lit.Value);
    }

    [Fact]
    public void Parse_SubtractionNotNegation()
    {
        // x - 1 should be subtraction, not x followed by -1
        var node = ExpressionParser.Parse("x - 1");
        var bin = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Sub, bin.Op);
        Assert.Equal("x", ((NameNode)bin.Left).Name);
        Assert.Equal(1, ((LiteralNode)bin.Right).Value);
    }

    // === Additional edge cases ===

    [Fact]
    public void Parse_LongLiteral()
    {
        var node = ExpressionParser.Parse("42L");
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Equal(42L, lit.Value);
        Assert.Equal(LiteralKind.Long, lit.Kind);
    }

    [Fact]
    public void Parse_LongLiteral_Lowercase()
    {
        var node = ExpressionParser.Parse("100l");
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Equal(100L, lit.Value);
        Assert.Equal(LiteralKind.Long, lit.Kind);
    }

    [Fact]
    public void Parse_EmptyString()
    {
        var node = ExpressionParser.Parse("\"\"");
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Equal("", lit.Value);
        Assert.Equal(LiteralKind.String, lit.Kind);
    }

    [Fact]
    public void Parse_StringWithEscapes()
    {
        var node = ExpressionParser.Parse("\"tab\\there\\\\quote\\\"end\"");
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Equal("tab\there\\quote\"end", lit.Value);
    }

    [Fact]
    public void Parse_UnderscoreIdentifier()
    {
        var node = ExpressionParser.Parse("_private");
        Assert.IsType<NameNode>(node);
        Assert.Equal("_private", ((NameNode)node).Name);
    }

    [Fact]
    public void Parse_IdentifierWithDigits()
    {
        var node = ExpressionParser.Parse("var1");
        Assert.IsType<NameNode>(node);
        Assert.Equal("var1", ((NameNode)node).Name);
    }

    [Fact]
    public void Parse_WhitespaceHandling()
    {
        var node = ExpressionParser.Parse("  a  +  b  ");
        var bin = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Add, bin.Op);
        Assert.Equal("a", ((NameNode)bin.Left).Name);
        Assert.Equal("b", ((NameNode)bin.Right).Name);
    }

    [Fact]
    public void Parse_NoWhitespace()
    {
        var node = ExpressionParser.Parse("a+b*c");
        var add = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Add, add.Op);
    }

    // === All binary operators ===

    [Theory]
    [InlineData("-", BinaryOp.Sub)]
    [InlineData("*", BinaryOp.Mul)]
    [InlineData("/", BinaryOp.Div)]
    [InlineData("%", BinaryOp.Mod)]
    public void Parse_AllArithmeticOps(string opStr, BinaryOp expectedOp)
    {
        var node = ExpressionParser.Parse($"a {opStr} b");
        var bin = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(expectedOp, bin.Op);
    }

    [Theory]
    [InlineData("<", BinaryOp.Lt)]
    [InlineData(">", BinaryOp.Gt)]
    [InlineData(">=", BinaryOp.GtEq)]
    public void Parse_RemainingComparisonOps(string opStr, BinaryOp expectedOp)
    {
        var node = ExpressionParser.Parse($"a {opStr} b");
        var bin = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(expectedOp, bin.Op);
    }

    // === Operator precedence and associativity ===

    [Fact]
    public void Parse_MultiplyBeforeAdd()
    {
        // 1 + 2 * 3 => 1 + (2 * 3)
        var node = ExpressionParser.Parse("1 + 2 * 3");
        var add = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Add, add.Op);
        Assert.Equal(1, ((LiteralNode)add.Left).Value);
        var mul = Assert.IsType<BinaryOpNode>(add.Right);
        Assert.Equal(BinaryOp.Mul, mul.Op);
        Assert.Equal(2, ((LiteralNode)mul.Left).Value);
        Assert.Equal(3, ((LiteralNode)mul.Right).Value);
    }

    [Fact]
    public void Parse_ComparisonBeforeEquality()
    {
        // a < b == c  =>  (a < b) == c
        var node = ExpressionParser.Parse("a < b == c");
        var eq = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Eq, eq.Op);
        var lt = Assert.IsType<BinaryOpNode>(eq.Left);
        Assert.Equal(BinaryOp.Lt, lt.Op);
    }

    [Fact]
    public void Parse_EqualityBeforeAnd()
    {
        // a == b && c != d  =>  (a == b) && (c != d)
        var node = ExpressionParser.Parse("a == b && c != d");
        var and = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.And, and.Op);
        var eq = Assert.IsType<BinaryOpNode>(and.Left);
        Assert.Equal(BinaryOp.Eq, eq.Op);
        var neq = Assert.IsType<BinaryOpNode>(and.Right);
        Assert.Equal(BinaryOp.NotEq, neq.Op);
    }

    [Fact]
    public void Parse_AndBeforeOr()
    {
        // a || b && c  =>  a || (b && c)
        var node = ExpressionParser.Parse("a || b && c");
        var or = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Or, or.Op);
        Assert.Equal("a", ((NameNode)or.Left).Name);
        var and = Assert.IsType<BinaryOpNode>(or.Right);
        Assert.Equal(BinaryOp.And, and.Op);
    }

    [Fact]
    public void Parse_LeftAssociativity_Addition()
    {
        // a + b + c => (a + b) + c (left-to-right)
        var node = ExpressionParser.Parse("a + b + c");
        var outer = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Add, outer.Op);
        Assert.Equal("c", ((NameNode)outer.Right).Name);
        var inner = Assert.IsType<BinaryOpNode>(outer.Left);
        Assert.Equal(BinaryOp.Add, inner.Op);
        Assert.Equal("a", ((NameNode)inner.Left).Name);
        Assert.Equal("b", ((NameNode)inner.Right).Name);
    }

    [Fact]
    public void Parse_LeftAssociativity_Multiply()
    {
        // a * b * c => (a * b) * c
        var node = ExpressionParser.Parse("a * b * c");
        var outer = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Mul, outer.Op);
        Assert.Equal("c", ((NameNode)outer.Right).Name);
        var inner = Assert.IsType<BinaryOpNode>(outer.Left);
        Assert.Equal(BinaryOp.Mul, inner.Op);
    }

    // === Deeply nested expressions ===

    [Fact]
    public void Parse_DeeplyNestedParens()
    {
        var node = ExpressionParser.Parse("((((x))))");
        Assert.IsType<NameNode>(node);
        Assert.Equal("x", ((NameNode)node).Name);
    }

    [Fact]
    public void Parse_ChainedMethodCalls()
    {
        // a.B().C().D()
        var node = ExpressionParser.Parse("a.B().C().D()");
        var d = Assert.IsType<MethodCallNode>(node);
        Assert.Equal("D", d.MethodName);
        var c = Assert.IsType<MethodCallNode>(d.Object);
        Assert.Equal("C", c.MethodName);
        var b = Assert.IsType<MethodCallNode>(c.Object);
        Assert.Equal("B", b.MethodName);
        Assert.Equal("a", ((NameNode)b.Object).Name);
    }

    [Fact]
    public void Parse_NestedMethodCallInArgs()
    {
        // a.Method(b.Value)
        var node = ExpressionParser.Parse("a.Method(b.Value)");
        var call = Assert.IsType<MethodCallNode>(node);
        Assert.Equal("Method", call.MethodName);
        Assert.Single(call.Arguments);
        var arg = Assert.IsType<MemberAccessNode>(call.Arguments[0]);
        Assert.Equal("Value", arg.MemberName);
    }

    [Fact]
    public void Parse_MethodWithMultipleComplexArgs()
    {
        // obj.Call(1 + 2, x * 3)
        var node = ExpressionParser.Parse("obj.Call(1 + 2, x * 3)");
        var call = Assert.IsType<MethodCallNode>(node);
        Assert.Equal("Call", call.MethodName);
        Assert.Equal(2, call.Arguments.Length);
        Assert.IsType<BinaryOpNode>(call.Arguments[0]);
        Assert.IsType<BinaryOpNode>(call.Arguments[1]);
    }

    [Fact]
    public void Parse_IndexWithExpression()
    {
        // arr[1 + 2]
        var node = ExpressionParser.Parse("arr[1 + 2]");
        var idx = Assert.IsType<IndexAccessNode>(node);
        var bin = Assert.IsType<BinaryOpNode>(idx.Index);
        Assert.Equal(BinaryOp.Add, bin.Op);
    }

    [Fact]
    public void Parse_ChainedIndexAccess()
    {
        // arr[0][1]
        var node = ExpressionParser.Parse("arr[0][1]");
        var outer = Assert.IsType<IndexAccessNode>(node);
        Assert.Equal(1, ((LiteralNode)outer.Index).Value);
        var inner = Assert.IsType<IndexAccessNode>(outer.Object);
        Assert.Equal(0, ((LiteralNode)inner.Index).Value);
    }

    [Fact]
    public void Parse_CastWithExpression()
    {
        // (int)(a + b)
        var node = ExpressionParser.Parse("(int)(a + b)");
        var cast = Assert.IsType<CastNode>(node);
        Assert.Equal("int", cast.TypeName);
        Assert.IsType<BinaryOpNode>(cast.Operand);
    }

    [Fact]
    public void Parse_CastDouble()
    {
        var node = ExpressionParser.Parse("(double)x");
        var cast = Assert.IsType<CastNode>(node);
        Assert.Equal("double", cast.TypeName);
    }

    [Fact]
    public void Parse_NegativeDouble()
    {
        var node = ExpressionParser.Parse("-3.14");
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Equal(-3.14, lit.Value);
        Assert.Equal(LiteralKind.Double, lit.Kind);
    }

    // === Error cases ===

    [Fact]
    public void Parse_UnclosedParen_Throws()
    {
        Assert.Throws<FormatException>(() => ExpressionParser.Parse("(a + b"));
    }

    [Fact]
    public void Parse_UnclosedBracket_Throws()
    {
        Assert.Throws<FormatException>(() => ExpressionParser.Parse("arr[0"));
    }

    [Fact]
    public void Parse_TrailingOperator_Throws()
    {
        Assert.Throws<FormatException>(() => ExpressionParser.Parse("a +"));
    }

    [Fact]
    public void Parse_DoubleOperator_Throws()
    {
        Assert.Throws<FormatException>(() => ExpressionParser.Parse("a ++ b"));
    }

    [Fact]
    public void Parse_TrailingDot_Throws()
    {
        Assert.Throws<FormatException>(() => ExpressionParser.Parse("obj."));
    }

    [Fact]
    public void Parse_TrailingComma_Throws()
    {
        Assert.Throws<FormatException>(() => ExpressionParser.Parse("a.Method(1,)"));
    }

    [Fact]
    public void Parse_OnlyWhitespace_Throws()
    {
        Assert.Throws<FormatException>(() => ExpressionParser.Parse("   "));
    }

    [Fact]
    public void Parse_ExtraTokensAfterExpression_Throws()
    {
        Assert.Throws<FormatException>(() => ExpressionParser.Parse("a b"));
    }

    [Fact]
    public void Parse_InvalidCharacter_Throws()
    {
        Assert.Throws<FormatException>(() => ExpressionParser.Parse("a # b"));
    }

    [Fact]
    public void Parse_ZeroLiteral()
    {
        var node = ExpressionParser.Parse("0");
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Equal(0, lit.Value);
    }

    [Fact]
    public void Parse_LargeNumber()
    {
        var node = ExpressionParser.Parse("2147483647");  // int.MaxValue
        var lit = Assert.IsType<LiteralNode>(node);
        Assert.Equal(2147483647, lit.Value);
    }

    [Fact]
    public void Parse_ThisKeyword()
    {
        // "this" should parse as a name, not a keyword
        var node = ExpressionParser.Parse("this");
        Assert.IsType<NameNode>(node);
        Assert.Equal("this", ((NameNode)node).Name);
    }

    [Fact]
    public void Parse_ThisDotMember()
    {
        var node = ExpressionParser.Parse("this.Name");
        var member = Assert.IsType<MemberAccessNode>(node);
        Assert.Equal("Name", member.MemberName);
        Assert.Equal("this", ((NameNode)member.Object).Name);
    }
}
