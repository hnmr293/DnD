using DnD.Core.Inspection;
using static DnD.Core.Inspection.ExpressionParser;

namespace DnD.Core.Tests;

public class PrimitiveArithmeticTests
{
    // === Compute: int operations ===

    [Theory]
    [InlineData(BinaryOp.Add, 3, 4, 7)]
    [InlineData(BinaryOp.Sub, 10, 3, 7)]
    [InlineData(BinaryOp.Mul, 3, 4, 12)]
    [InlineData(BinaryOp.Div, 10, 3, 3)]
    [InlineData(BinaryOp.Mod, 10, 3, 1)]
    public void Compute_IntArithmetic(BinaryOp op, int left, int right, int expected)
    {
        var result = PrimitiveArithmetic.Compute(left, op, right);
        Assert.IsType<int>(result);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(BinaryOp.Eq, 3, 3, true)]
    [InlineData(BinaryOp.Eq, 3, 4, false)]
    [InlineData(BinaryOp.NotEq, 3, 4, true)]
    [InlineData(BinaryOp.NotEq, 3, 3, false)]
    [InlineData(BinaryOp.Lt, 3, 4, true)]
    [InlineData(BinaryOp.Lt, 4, 3, false)]
    [InlineData(BinaryOp.Lt, 3, 3, false)]
    [InlineData(BinaryOp.Gt, 4, 3, true)]
    [InlineData(BinaryOp.Gt, 3, 4, false)]
    [InlineData(BinaryOp.LtEq, 3, 3, true)]
    [InlineData(BinaryOp.LtEq, 3, 4, true)]
    [InlineData(BinaryOp.LtEq, 4, 3, false)]
    [InlineData(BinaryOp.GtEq, 3, 3, true)]
    [InlineData(BinaryOp.GtEq, 4, 3, true)]
    [InlineData(BinaryOp.GtEq, 3, 4, false)]
    public void Compute_IntComparisons(BinaryOp op, int left, int right, bool expected)
    {
        var result = PrimitiveArithmetic.Compute(left, op, right);
        Assert.IsType<bool>(result);
        Assert.Equal(expected, result);
    }

    // === Compute: long operations ===

    [Theory]
    [InlineData(BinaryOp.Add, 3L, 4L, 7L)]
    [InlineData(BinaryOp.Sub, 10L, 3L, 7L)]
    [InlineData(BinaryOp.Mul, 3L, 4L, 12L)]
    [InlineData(BinaryOp.Div, 10L, 3L, 3L)]
    [InlineData(BinaryOp.Mod, 10L, 3L, 1L)]
    public void Compute_LongArithmetic(BinaryOp op, long left, long right, long expected)
    {
        var result = PrimitiveArithmetic.Compute(left, op, right);
        Assert.IsType<long>(result);
        Assert.Equal(expected, result);
    }

    // === Compute: double operations ===

    [Fact]
    public void Compute_DoubleAdd()
    {
        var result = PrimitiveArithmetic.Compute(1.5, BinaryOp.Add, 2.5);
        Assert.IsType<double>(result);
        Assert.Equal(4.0, result);
    }

    [Fact]
    public void Compute_DoubleSub()
    {
        var result = PrimitiveArithmetic.Compute(5.0, BinaryOp.Sub, 2.5);
        Assert.Equal(2.5, result);
    }

    [Fact]
    public void Compute_DoubleMul()
    {
        var result = PrimitiveArithmetic.Compute(2.0, BinaryOp.Mul, 3.5);
        Assert.Equal(7.0, result);
    }

    [Fact]
    public void Compute_DoubleDiv()
    {
        var result = PrimitiveArithmetic.Compute(7.0, BinaryOp.Div, 2.0);
        Assert.Equal(3.5, result);
    }

    [Fact]
    public void Compute_DoubleMod()
    {
        var result = PrimitiveArithmetic.Compute(7.5, BinaryOp.Mod, 2.0);
        Assert.Equal(1.5, result);
    }

    [Theory]
    [InlineData(BinaryOp.Eq, 1.0, 1.0, true)]
    [InlineData(BinaryOp.Eq, 1.0, 2.0, false)]
    [InlineData(BinaryOp.Lt, 1.0, 2.0, true)]
    [InlineData(BinaryOp.Gt, 2.0, 1.0, true)]
    public void Compute_DoubleComparisons(BinaryOp op, double left, double right, bool expected)
    {
        var result = PrimitiveArithmetic.Compute(left, op, right);
        Assert.Equal(expected, result);
    }

    // === Type promotion ===

    [Fact]
    public void Compute_IntAndLong_PromotesToLong()
    {
        // int + long → long
        var result = PrimitiveArithmetic.Compute(1, BinaryOp.Add, 2L);
        Assert.IsType<long>(result);
        Assert.Equal(3L, result);
    }

    [Fact]
    public void Compute_IntAndDouble_PromotesToDouble()
    {
        // int + double → double
        var result = PrimitiveArithmetic.Compute(1, BinaryOp.Add, 2.5);
        Assert.IsType<double>(result);
        Assert.Equal(3.5, result);
    }

    [Fact]
    public void Compute_LongAndDouble_PromotesToDouble()
    {
        // long + double → double
        var result = PrimitiveArithmetic.Compute(1L, BinaryOp.Add, 2.5);
        Assert.IsType<double>(result);
        Assert.Equal(3.5, result);
    }

    // === Bool operations ===

    [Theory]
    [InlineData(BinaryOp.Eq, true, true, true)]
    [InlineData(BinaryOp.Eq, true, false, false)]
    [InlineData(BinaryOp.NotEq, true, false, true)]
    [InlineData(BinaryOp.NotEq, true, true, false)]
    [InlineData(BinaryOp.And, true, true, true)]
    [InlineData(BinaryOp.And, true, false, false)]
    [InlineData(BinaryOp.And, false, false, false)]
    [InlineData(BinaryOp.Or, true, false, true)]
    [InlineData(BinaryOp.Or, false, false, false)]
    [InlineData(BinaryOp.Or, false, true, true)]
    public void Compute_BoolOperations(BinaryOp op, bool left, bool right, bool expected)
    {
        var result = PrimitiveArithmetic.Compute(left, op, right);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Compute_BoolAdd_ReturnsNull()
    {
        // Arithmetic on bools is not supported
        var result = PrimitiveArithmetic.Compute(true, BinaryOp.Add, false);
        Assert.Null(result);
    }

    // === Division by zero ===

    [Fact]
    public void Compute_IntDivisionByZero_Throws()
    {
        Assert.Throws<DivideByZeroException>(() =>
            PrimitiveArithmetic.Compute(10, BinaryOp.Div, 0));
    }

    [Fact]
    public void Compute_LongDivisionByZero_Throws()
    {
        Assert.Throws<DivideByZeroException>(() =>
            PrimitiveArithmetic.Compute(10L, BinaryOp.Div, 0L));
    }

    [Fact]
    public void Compute_DoubleDivisionByZero_Throws()
    {
        Assert.Throws<DivideByZeroException>(() =>
            PrimitiveArithmetic.Compute(10.0, BinaryOp.Div, 0.0));
    }

    // === Null handling ===

    [Fact]
    public void TryBinaryOp_NullLeft_ReturnsNull()
    {
        var result = PrimitiveArithmetic.TryBinaryOp(null, BinaryOp.Add, null);
        Assert.Null(result);
    }

    [Fact]
    public void GetIntValue_Null_ReturnsNull()
    {
        var result = PrimitiveArithmetic.GetIntValue(null);
        Assert.Null(result);
    }

    // === GetResultType ===

    [Theory]
    [InlineData(true, ClrDebug.CorElementType.Boolean, 1)]
    [InlineData(42, ClrDebug.CorElementType.I4, 4)]
    [InlineData(42L, ClrDebug.CorElementType.I8, 8)]
    [InlineData(3.14f, ClrDebug.CorElementType.R4, 4)]
    [InlineData(3.14, ClrDebug.CorElementType.R8, 8)]
    public void GetResultType_ReturnsCorrectTypeAndSize(object value, ClrDebug.CorElementType expectedType, int expectedSize)
    {
        var (type, size) = PrimitiveArithmetic.GetResultType(value);
        Assert.Equal(expectedType, type);
        Assert.Equal(expectedSize, size);
    }

    [Fact]
    public void GetResultType_UnknownType_DefaultsToI4()
    {
        var (type, size) = PrimitiveArithmetic.GetResultType("string");
        Assert.Equal(ClrDebug.CorElementType.I4, type);
        Assert.Equal(4, size);
    }

    // === Cast ===

    [Fact]
    public void Cast_ReturnsValueAsIs()
    {
        // Current implementation returns value unchanged
        var result = PrimitiveArithmetic.Cast(null, "int");
        Assert.Null(result);
    }

    // === Edge cases ===

    [Fact]
    public void Compute_IntOverflow_Wraps()
    {
        // int.MaxValue + 1 wraps to int.MinValue (C# unchecked behavior)
        var result = PrimitiveArithmetic.Compute(int.MaxValue, BinaryOp.Add, 1);
        Assert.IsType<int>(result);
        Assert.Equal(int.MinValue, result);
    }

    [Fact]
    public void Compute_IntModByZero_Throws()
    {
        Assert.Throws<DivideByZeroException>(() =>
            PrimitiveArithmetic.Compute(10, BinaryOp.Mod, 0));
    }

    [Fact]
    public void Compute_NegativeNumbers()
    {
        var result = PrimitiveArithmetic.Compute(-3, BinaryOp.Mul, -4);
        Assert.Equal(12, result);
    }

    [Fact]
    public void Compute_UnsupportedOp_ReturnsNull()
    {
        // BinaryOp.And on ints is not supported, should return null
        var result = PrimitiveArithmetic.Compute(1, BinaryOp.And, 2);
        Assert.Null(result);
    }
}
