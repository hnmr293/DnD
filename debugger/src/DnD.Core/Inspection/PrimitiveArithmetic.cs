namespace DnD.Core.Inspection;

using System.Runtime.InteropServices;
using ClrDebug;
using static ExpressionParser;

/// <summary>
/// Performs primitive arithmetic and type operations on debuggee values.
/// Handles int, long, float, double with appropriate type promotion.
/// </summary>
public static class PrimitiveArithmetic
{
    /// <summary>
    /// Try to compute a binary operation on two primitive values.
    /// Returns the C# result as a boxed object, or null if the operands aren't primitives.
    /// </summary>
    public static object? TryBinaryOp(CorDebugValue? left, BinaryOp op, CorDebugValue? right)
    {
        var l = ExtractPrimitive(left);
        var r = ExtractPrimitive(right);
        if (l == null || r == null) return null;

        return Compute(l, op, r);
    }

    /// <summary>
    /// Extract an integer value from a CorDebugValue (for array indexing).
    /// </summary>
    public static int? GetIntValue(CorDebugValue? value)
    {
        var prim = ExtractPrimitive(value);
        if (prim == null) return null;
        return Convert.ToInt32(prim);
    }

    /// <summary>
    /// Get the CorElementType and size for a computed result value.
    /// </summary>
    public static (CorElementType Type, int Size) GetResultType(object value)
    {
        return value switch
        {
            bool => (CorElementType.Boolean, 1),
            int => (CorElementType.I4, 4),
            long => (CorElementType.I8, 8),
            float => (CorElementType.R4, 4),
            double => (CorElementType.R8, 8),
            _ => (CorElementType.I4, 4),
        };
    }

    /// <summary>
    /// Write a boxed primitive value into a CorDebugGenericValue.
    /// </summary>
    public static void SetGenericValue(CorDebugGenericValue gv, object value)
    {
        var size = (int)gv.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            switch (value)
            {
                case bool b:
                    Marshal.WriteByte(buffer, b ? (byte)1 : (byte)0);
                    break;
                case int i:
                    Marshal.WriteInt32(buffer, i);
                    break;
                case long l:
                    Marshal.WriteInt64(buffer, l);
                    break;
                case float f:
                    Marshal.WriteInt32(buffer, BitConverter.SingleToInt32Bits(f));
                    break;
                case double d:
                    Marshal.WriteInt64(buffer, BitConverter.DoubleToInt64Bits(d));
                    break;
                default:
                    Marshal.WriteInt32(buffer, Convert.ToInt32(value));
                    break;
            }
            gv.SetValue(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Cast a primitive value to a different type.
    /// </summary>
    public static CorDebugValue? Cast(CorDebugValue? value, string typeName)
    {
        // For primitive casts, we can't actually change the type in-process without eval
        // Just return the value as-is; the display will show the appropriate type
        return value;
    }

    private static object? ExtractPrimitive(CorDebugValue? value)
    {
        if (value == null) return null;

        if (value is CorDebugReferenceValue refVal)
        {
            if (refVal.IsNull) return null;
            value = refVal.Dereference();
        }

        if (value is CorDebugBoxValue boxVal)
            value = boxVal.Object;

        if (value is CorDebugGenericValue gv)
        {
            var elementType = gv.Type;
            var size = (int)gv.Size;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                gv.GetValue(buffer);
                return elementType switch
                {
                    CorElementType.Boolean => Marshal.ReadByte(buffer) != 0,
                    CorElementType.I1 => (int)(sbyte)Marshal.ReadByte(buffer),
                    CorElementType.U1 => (int)Marshal.ReadByte(buffer),
                    CorElementType.I2 => (int)Marshal.ReadInt16(buffer),
                    CorElementType.U2 => (int)(ushort)Marshal.ReadInt16(buffer),
                    CorElementType.I4 => Marshal.ReadInt32(buffer),
                    CorElementType.U4 => (long)(uint)Marshal.ReadInt32(buffer),
                    CorElementType.I8 => Marshal.ReadInt64(buffer),
                    CorElementType.U8 => (long)(ulong)Marshal.ReadInt64(buffer),
                    CorElementType.R4 => (double)BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer)),
                    CorElementType.R8 => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer)),
                    _ => null,
                };
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return null;
    }

    private static object? Compute(object left, BinaryOp op, object right)
    {
        // Promote to common type
        if (left is double || right is double)
        {
            var l = Convert.ToDouble(left);
            var r = Convert.ToDouble(right);
            return op switch
            {
                BinaryOp.Add => l + r,
                BinaryOp.Sub => l - r,
                BinaryOp.Mul => l * r,
                BinaryOp.Div => r != 0 ? l / r : throw new DivideByZeroException(),
                BinaryOp.Mod => l % r,
                BinaryOp.Eq => l == r,
                BinaryOp.NotEq => l != r,
                BinaryOp.Lt => l < r,
                BinaryOp.Gt => l > r,
                BinaryOp.LtEq => l <= r,
                BinaryOp.GtEq => l >= r,
                _ => null,
            };
        }

        if (left is long || right is long)
        {
            var l = Convert.ToInt64(left);
            var r = Convert.ToInt64(right);
            return op switch
            {
                BinaryOp.Add => l + r,
                BinaryOp.Sub => l - r,
                BinaryOp.Mul => l * r,
                BinaryOp.Div => r != 0 ? l / r : throw new DivideByZeroException(),
                BinaryOp.Mod => l % r,
                BinaryOp.Eq => l == r,
                BinaryOp.NotEq => l != r,
                BinaryOp.Lt => l < r,
                BinaryOp.Gt => l > r,
                BinaryOp.LtEq => l <= r,
                BinaryOp.GtEq => l >= r,
                _ => null,
            };
        }

        if (left is bool lb && right is bool rb)
        {
            return op switch
            {
                BinaryOp.Eq => lb == rb,
                BinaryOp.NotEq => lb != rb,
                BinaryOp.And => lb && rb,
                BinaryOp.Or => lb || rb,
                _ => null,
            };
        }

        // Default: int
        {
            var l = Convert.ToInt32(left);
            var r = Convert.ToInt32(right);
            return op switch
            {
                BinaryOp.Add => l + r,
                BinaryOp.Sub => l - r,
                BinaryOp.Mul => l * r,
                BinaryOp.Div => r != 0 ? l / r : throw new DivideByZeroException(),
                BinaryOp.Mod => l % r,
                BinaryOp.Eq => l == r,
                BinaryOp.NotEq => l != r,
                BinaryOp.Lt => l < r,
                BinaryOp.Gt => l > r,
                BinaryOp.LtEq => l <= r,
                BinaryOp.GtEq => l >= r,
                _ => null,
            };
        }
    }
}
