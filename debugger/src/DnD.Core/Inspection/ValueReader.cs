namespace DnD.Core.Inspection;

using System.Runtime.InteropServices;
using ClrDebug;

public class ValueReader
{
    public (string Value, string? Type) Read(CorDebugValue value)
    {
        try
        {
            return ReadCore(value);
        }
        catch
        {
            return ("<error reading value>", null);
        }
    }

    private (string Value, string? Type) ReadCore(CorDebugValue value)
    {
        if (value is CorDebugReferenceValue refVal)
        {
            if (refVal.IsNull)
                return ("null", "object");
            try { value = refVal.Dereference(); }
            catch { return ("<invalid reference>", null); }
        }

        if (value is CorDebugBoxValue boxVal)
            value = boxVal.Object;

        if (value is CorDebugStringValue strVal)
        {
            var s = strVal.GetString(strVal.Length);
            return ($"\"{s}\"", "string");
        }

        if (value is CorDebugArrayValue arrVal)
        {
            var count = arrVal.Count;
            var elementTypeName = TypeNameResolver.GetArrayElementTypeName(arrVal);
            var rank = arrVal.Rank;
            var arraySuffix = rank > 1 ? $"[{new string(',', rank - 1)}]" : "[]";
            return ($"{elementTypeName}[{count}]", $"{elementTypeName}{arraySuffix}");
        }

        if (value is CorDebugObjectValue objVal)
        {
            var typeName = TypeNameResolver.GetCSharpTypeName(objVal);

            if (objVal.IsValueClass)
            {
                try
                {
                    // QI from ICorDebugObjectValue to ICorDebugGenericValue for value types
                    var genericVal = new CorDebugGenericValue((ICorDebugGenericValue)objVal.Raw);
                    // For boxed primitives, the element type is ValueType, not the specific
                    // primitive type. Use the metadata type name to determine the actual type.
                    if (genericVal.Type == CorElementType.ValueType)
                    {
                        var metadataName = TypeNameResolver.GetTypeDefName(objVal);
                        if (metadataName != null)
                            return ReadBoxedPrimitive(genericVal, metadataName, typeName);
                    }
                    else
                    {
                        return ReadGenericValue(genericVal);
                    }
                }
                catch { }
            }

            return ($"{{{typeName}}}", typeName);
        }

        if (value is CorDebugGenericValue genVal)
            return ReadGenericValue(genVal);

        return (value.ToString() ?? "<unknown>", null);
    }

    /// <param name="metadataName">Metadata type name, used to identify the primitive.</param>
    /// <param name="typeName">C# type name, reported when the value is not a primitive.</param>
    private (string Value, string? Type) ReadBoxedPrimitive(CorDebugGenericValue value, string metadataName, string typeName)
    {
        var size = (int)value.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            value.GetValue(buffer);
            return metadataName switch
            {
                "System.Boolean" => (Marshal.ReadByte(buffer) != 0 ? "true" : "false", "bool"),
                "System.Char" => ($"'{(char)Marshal.ReadInt16(buffer)}'", "char"),
                "System.SByte" => (((sbyte)Marshal.ReadByte(buffer)).ToString(), "sbyte"),
                "System.Byte" => (Marshal.ReadByte(buffer).ToString(), "byte"),
                "System.Int16" => (Marshal.ReadInt16(buffer).ToString(), "short"),
                "System.UInt16" => (((ushort)Marshal.ReadInt16(buffer)).ToString(), "ushort"),
                "System.Int32" => (Marshal.ReadInt32(buffer).ToString(), "int"),
                "System.UInt32" => (((uint)Marshal.ReadInt32(buffer)).ToString(), "uint"),
                "System.Int64" => (Marshal.ReadInt64(buffer).ToString(), "long"),
                "System.UInt64" => (((ulong)Marshal.ReadInt64(buffer)).ToString(), "ulong"),
                "System.Single" => (BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer)).ToString(), "float"),
                "System.Double" => (BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer)).ToString(), "double"),
                _ => ($"{{{typeName}}}", typeName)
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private (string Value, string? Type) ReadGenericValue(CorDebugGenericValue value)
    {
        var elementType = value.Type;
        var size = (int)value.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            value.GetValue(buffer);

            return elementType switch
            {
                CorElementType.Boolean => (Marshal.ReadByte(buffer) != 0 ? "true" : "false", "bool"),
                CorElementType.Char => ($"'{(char)Marshal.ReadInt16(buffer)}'", "char"),
                CorElementType.I1 => (((sbyte)Marshal.ReadByte(buffer)).ToString(), "sbyte"),
                CorElementType.U1 => (Marshal.ReadByte(buffer).ToString(), "byte"),
                CorElementType.I2 => (Marshal.ReadInt16(buffer).ToString(), "short"),
                CorElementType.U2 => (((ushort)Marshal.ReadInt16(buffer)).ToString(), "ushort"),
                CorElementType.I4 => (Marshal.ReadInt32(buffer).ToString(), "int"),
                CorElementType.U4 => (((uint)Marshal.ReadInt32(buffer)).ToString(), "uint"),
                CorElementType.I8 => (Marshal.ReadInt64(buffer).ToString(), "long"),
                CorElementType.U8 => (((ulong)Marshal.ReadInt64(buffer)).ToString(), "ulong"),
                CorElementType.R4 => (BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer)).ToString(), "float"),
                CorElementType.R8 => (BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer)).ToString(), "double"),
                _ => ($"<{elementType}>", elementType.ToString())
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

}
