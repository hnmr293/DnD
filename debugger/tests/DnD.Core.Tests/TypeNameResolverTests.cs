namespace DnD.Core.Tests;

using ClrDebug;
using DnD.Core.Inspection;

public class TypeNameResolverTests
{
    [Theory]
    [InlineData(CorElementType.Boolean, "bool")]
    [InlineData(CorElementType.Char, "char")]
    [InlineData(CorElementType.I1, "sbyte")]
    [InlineData(CorElementType.U1, "byte")]
    [InlineData(CorElementType.I2, "short")]
    [InlineData(CorElementType.U2, "ushort")]
    [InlineData(CorElementType.I4, "int")]
    [InlineData(CorElementType.U4, "uint")]
    [InlineData(CorElementType.I8, "long")]
    [InlineData(CorElementType.U8, "ulong")]
    [InlineData(CorElementType.R4, "float")]
    [InlineData(CorElementType.R8, "double")]
    [InlineData(CorElementType.Object, "object")]
    public void GetPrimitiveTypeName_ReturnsExpectedName(CorElementType elementType, string expected)
    {
        var result = TypeNameResolver.GetPrimitiveTypeName(elementType);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(CorElementType.String)]
    [InlineData(CorElementType.Class)]
    [InlineData(CorElementType.ValueType)]
    [InlineData(CorElementType.SZArray)]
    public void GetPrimitiveTypeName_ReturnsNull_ForNonPrimitiveTypes(CorElementType elementType)
    {
        var result = TypeNameResolver.GetPrimitiveTypeName(elementType);
        Assert.Null(result);
    }
}
