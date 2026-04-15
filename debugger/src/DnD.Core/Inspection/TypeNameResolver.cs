namespace DnD.Core.Inspection;

using ClrDebug;

/// <summary>
/// Resolves ICorDebugValue runtime types to C# fully-qualified type names.
/// Supports primitives, objects, generics, and arrays.
/// </summary>
public static class TypeNameResolver
{
    public static string GetCSharpTypeName(CorDebugValue value)
    {
        try
        {
            return GetTypeNameCore(value);
        }
        catch
        {
            return "object";
        }
    }

    private static string GetTypeNameCore(CorDebugValue value)
    {
        if (value is CorDebugReferenceValue refVal)
        {
            if (refVal.IsNull)
                return "object";
            try { value = refVal.Dereference(); }
            catch { return "object"; }
        }

        if (value is CorDebugBoxValue boxVal)
            value = boxVal.Object;

        // Try to get exact type via ICorDebugValue2 (handles generics)
        try
        {
            var val2 = (ICorDebugValue2)value.Raw;
            val2.GetExactType(out var exactType);
            return FormatExactType(exactType);
        }
        catch { }

        // Fallback to element type
        var elementType = value.Type;
        var primitiveName = GetPrimitiveTypeName(elementType);
        if (primitiveName != null)
            return primitiveName;

        if (value is CorDebugStringValue)
            return "string";

        if (value is CorDebugArrayValue arrVal)
        {
            var elemTypeName = GetPrimitiveTypeName(arrVal.ElementType) ?? "object";
            return $"{elemTypeName}[]";
        }

        if (value is CorDebugObjectValue objVal)
        {
            try
            {
                var classType = objVal.Class;
                var module = classType.Module;
                var import = module.GetMetaDataInterface<MetaDataImport>();
                var typeProps = import.GetTypeDefProps(classType.Token);
                return typeProps.szTypeDef;
            }
            catch { }
        }

        return "object";
    }

    private static string FormatExactType(ICorDebugType exactType)
    {
        exactType.GetType(out var elementType);

        var primitiveName = GetPrimitiveTypeName(elementType);
        if (primitiveName != null)
            return primitiveName;

        if (elementType == CorElementType.String)
            return "string";

        if (elementType == CorElementType.SZArray)
        {
            exactType.GetFirstTypeParameter(out var elemType);
            var elemName = FormatExactType(elemType);
            return $"{elemName}[]";
        }

        if (elementType == CorElementType.Class || elementType == CorElementType.ValueType)
        {
            exactType.GetClass(out var classRaw);
            var cls = new CorDebugClass(classRaw);
            var module = cls.Module;
            var import = module.GetMetaDataInterface<MetaDataImport>();
            var typeProps = import.GetTypeDefProps(cls.Token);
            var fullName = typeProps.szTypeDef;

            // Check for generic type parameters
            exactType.EnumerateTypeParameters(out var typeParamEnum);
            if (typeParamEnum != null)
            {
                typeParamEnum.GetCount(out var count);
                if (count > 0)
                {
                    // Remove backtick suffix (e.g., "System.Collections.Generic.List`1" → "System.Collections.Generic.List")
                    var backtickIdx = fullName.IndexOf('`');
                    if (backtickIdx >= 0)
                        fullName = fullName[..backtickIdx];

                    var typeArgs = new string[(int)count];
                    for (int i = 0; i < (int)count; i++)
                    {
                        typeParamEnum.Next(1, out var typeArg, out _);
                        typeArgs[i] = FormatExactType(typeArg);
                    }
                    return $"{fullName}<{string.Join(", ", typeArgs)}>";
                }
            }

            return fullName;
        }

        return "object";
    }

    internal static string? GetPrimitiveTypeName(CorElementType elementType)
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
            CorElementType.Object => "object",
            _ => null
        };
    }
}
