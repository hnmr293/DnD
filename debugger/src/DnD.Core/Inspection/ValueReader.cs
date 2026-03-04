namespace DnD.Core.Inspection;

using System.Runtime.InteropServices;
using ClrDebug;
using DnD.Protocol;

public class ValueReader
{
    public (string Value, string? Type, int VariablesReference) Read(
        CorDebugValue value, VariableStore store, int depth = 0)
    {
        try
        {
            return ReadCore(value, store, depth);
        }
        catch
        {
            return ("<error reading value>", null, 0);
        }
    }

    private (string Value, string? Type, int VariablesReference) ReadCore(
        CorDebugValue value, VariableStore store, int depth)
    {
        if (value is CorDebugReferenceValue refVal)
        {
            if (refVal.IsNull)
                return ("null", "object", 0);
            try { value = refVal.Dereference(); }
            catch { return ("<invalid reference>", null, 0); }
        }

        if (value is CorDebugBoxValue boxVal)
            value = boxVal.Object;

        if (value is CorDebugStringValue strVal)
        {
            var s = strVal.GetString(strVal.Length);
            return ($"\"{s}\"", "string", 0);
        }

        if (value is CorDebugArrayValue arrVal)
        {
            var count = arrVal.Count;
            var elementType = arrVal.ElementType;
            var typeName = $"{GetElementTypeName(elementType)}[{count}]";
            if (depth < 2)
            {
                var varRef = store.Store(value);
                return (typeName, typeName, varRef);
            }
            return (typeName, typeName, 0);
        }

        if (value is CorDebugObjectValue objVal)
        {
            if (objVal.IsValueClass)
            {
                try
                {
                    return ReadGenericValue((CorDebugGenericValue)value);
                }
                catch { }
            }

            var className = GetClassName(objVal);
            if (depth < 2)
            {
                var varRef = store.Store(value);
                return ($"{{{className}}}", className, varRef);
            }
            return ($"{{{className}}}", className, 0);
        }

        if (value is CorDebugGenericValue genericVal)
            return ReadGenericValue(genericVal);

        return (value.ToString() ?? "<unknown>", null, 0);
    }

    private (string Value, string? Type, int VariablesReference) ReadGenericValue(CorDebugGenericValue value)
    {
        var elementType = value.Type;
        var size = (int)value.Size;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            value.GetValue(buffer);

            return elementType switch
            {
                CorElementType.Boolean => (Marshal.ReadByte(buffer) != 0 ? "true" : "false", "bool", 0),
                CorElementType.Char => ($"'{(char)Marshal.ReadInt16(buffer)}'", "char", 0),
                CorElementType.I1 => (((sbyte)Marshal.ReadByte(buffer)).ToString(), "sbyte", 0),
                CorElementType.U1 => (Marshal.ReadByte(buffer).ToString(), "byte", 0),
                CorElementType.I2 => (Marshal.ReadInt16(buffer).ToString(), "short", 0),
                CorElementType.U2 => (((ushort)Marshal.ReadInt16(buffer)).ToString(), "ushort", 0),
                CorElementType.I4 => (Marshal.ReadInt32(buffer).ToString(), "int", 0),
                CorElementType.U4 => (((uint)Marshal.ReadInt32(buffer)).ToString(), "uint", 0),
                CorElementType.I8 => (Marshal.ReadInt64(buffer).ToString(), "long", 0),
                CorElementType.U8 => (((ulong)Marshal.ReadInt64(buffer)).ToString(), "ulong", 0),
                CorElementType.R4 => (BitConverter.Int32BitsToSingle(Marshal.ReadInt32(buffer)).ToString(), "float", 0),
                CorElementType.R8 => (BitConverter.Int64BitsToDouble(Marshal.ReadInt64(buffer)).ToString(), "double", 0),
                _ => ($"<{elementType}>", elementType.ToString(), 0)
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public List<Variable> ExpandChildren(CorDebugValue value, VariableStore store)
    {
        var result = new List<Variable>();

        try
        {
            if (value is CorDebugReferenceValue refVal)
            {
                if (refVal.IsNull) return result;
                value = refVal.Dereference();
            }

            if (value is CorDebugBoxValue boxVal)
                value = boxVal.Object;

            if (value is CorDebugArrayValue arrVal)
            {
                var count = Math.Min((int)arrVal.Count, 100);
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var element = arrVal.GetElementAtPosition(i);
                        var (displayValue, type, varRef) = Read(element, store, 1);
                        result.Add(new Variable($"[{i}]", displayValue, varRef, type));
                    }
                    catch { }
                }
                return result;
            }

            if (value is CorDebugObjectValue objVal)
            {
                var classType = objVal.Class;
                var module = classType.Module;
                var import = module.GetMetaDataInterface<MetaDataImport>();
                var classToken = classType.Token;

                var fieldTokens = EnumFields(import, classToken);
                foreach (var fieldToken in fieldTokens)
                {
                    try
                    {
                        var fieldProps = import.GetFieldProps((mdFieldDef)fieldToken);
                        var fieldName = fieldProps.szField;
                        var fieldValue = objVal.GetFieldValue(classType.Raw, (mdFieldDef)fieldToken);
                        var (displayValue, type, varRef) = Read(fieldValue, store, 1);
                        result.Add(new Variable(fieldName, displayValue, varRef, type));
                    }
                    catch { }
                }
            }
        }
        catch { }

        return result;
    }

    private static List<int> EnumFields(MetaDataImport import, mdTypeDef classToken)
    {
        var fields = new List<int>();
        try
        {
            var enumHandle = IntPtr.Zero;
            var fieldTokens = new mdFieldDef[32];

            var count = import.EnumFields(ref enumHandle, classToken, fieldTokens);
            while (count > 0)
            {
                for (int i = 0; i < count; i++)
                    fields.Add((int)fieldTokens[i]);
                count = import.EnumFields(ref enumHandle, classToken, fieldTokens);
            }

            if (enumHandle != IntPtr.Zero)
                import.CloseEnum(enumHandle);
        }
        catch { }
        return fields;
    }

    private static string GetClassName(CorDebugObjectValue objVal)
    {
        try
        {
            var classType = objVal.Class;
            var module = classType.Module;
            var import = module.GetMetaDataInterface<MetaDataImport>();
            var typeProps = import.GetTypeDefProps(classType.Token);
            return typeProps.szTypeDef;
        }
        catch { return "<object>"; }
    }

    private static string GetElementTypeName(CorElementType elementType)
    {
        return elementType switch
        {
            CorElementType.Boolean => "bool",
            CorElementType.Char => "char",
            CorElementType.I1 => "sbyte",
            CorElementType.U1 => "byte",
            CorElementType.I2 => "short",
            CorElementType.U2 => "ushort",
            CorElementType.I4 => "int",
            CorElementType.U4 => "uint",
            CorElementType.I8 => "long",
            CorElementType.U8 => "ulong",
            CorElementType.R4 => "float",
            CorElementType.R8 => "double",
            CorElementType.String => "string",
            CorElementType.Object => "object",
            _ => elementType.ToString()
        };
    }
}
