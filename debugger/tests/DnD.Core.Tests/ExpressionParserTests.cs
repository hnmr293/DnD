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
}
