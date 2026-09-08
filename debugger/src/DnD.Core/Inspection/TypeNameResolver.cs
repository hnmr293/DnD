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
            var typeDefName = GetTypeDefName(objVal);
            if (typeDefName != null)
                return typeDefName;
        }

        return "object";
    }

    /// <summary>
    /// Reads the metadata type name of an object value, qualified with the
    /// declaring types of nested types.
    /// Generic types keep their arity suffix (e.g. "System.Collections.Generic.List`1").
    /// Returns null when the metadata cannot be read.
    /// </summary>
    internal static string? GetTypeDefName(CorDebugObjectValue objVal)
    {
        try
        {
            var classType = objVal.Class;
            var module = classType.Module;
            var import = module.GetMetaDataInterface<MetaDataImport>();
            return GetTypeDefFullName(import, classType.Token);
        }
        catch { return null; }
    }

    /// <summary>
    /// Builds the type name of a TypeDef. Metadata stores a nested type under its
    /// simple name only ("Inner"), so the declaring types are prepended to make the
    /// name resolvable from C# source ("Outer.Inner").
    /// </summary>
    private static string GetTypeDefFullName(MetaDataImport import, mdTypeDef token)
    {
        var typeProps = import.GetTypeDefProps(token);
        var name = typeProps.szTypeDef;

        var visibility = typeProps.pdwTypeDefFlags & CorTypeAttr.tdVisibilityMask;
        if (visibility < CorTypeAttr.tdNestedPublic)
            return name;

        try
        {
            var enclosing = import.GetNestedClassProps(token);
            return $"{GetTypeDefFullName(import, enclosing)}.{name}";
        }
        catch { return name; }
    }

    /// <summary>
    /// Removes generic arity suffixes ("Outer`1.Inner`2" → "Outer.Inner") so that
    /// type arguments can be appended in C# syntax.
    /// </summary>
    private static string StripGenericArity(string typeName)
    {
        var backtickIdx = typeName.IndexOf('`');
        if (backtickIdx < 0)
            return typeName;

        var sb = new System.Text.StringBuilder(typeName.Length);
        for (int i = 0; i < typeName.Length; i++)
        {
            if (typeName[i] != '`')
            {
                sb.Append(typeName[i]);
                continue;
            }
            // Skip the arity digits following the backtick
            i++;
            while (i < typeName.Length && char.IsDigit(typeName[i]))
                i++;
            i--;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolves the element type name of an array value (e.g. "int" for int[3],
    /// "System.Collections.Generic.List&lt;string&gt;" for List&lt;string&gt;[]).
    /// </summary>
    internal static string GetArrayElementTypeName(CorDebugArrayValue arrVal)
    {
        // The exact type of an array carries its element type as the first type parameter.
        try
        {
            var val2 = (ICorDebugValue2)arrVal.Raw;
            val2.GetExactType(out var exactType);
            exactType.GetFirstTypeParameter(out var elemType);
            return FormatExactType(elemType);
        }
        catch { }

        return GetPrimitiveTypeName(arrVal.ElementType)
            ?? (arrVal.ElementType == CorElementType.String ? "string" : "object");
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
            var fullName = GetTypeDefFullName(import, cls.Token);

            // Check for generic type parameters
            exactType.EnumerateTypeParameters(out var typeParamEnum);
            if (typeParamEnum != null)
            {
                typeParamEnum.GetCount(out var count);
                if (count > 0)
                {
                    // "System.Collections.Generic.List`1" → "System.Collections.Generic.List"
                    fullName = StripGenericArity(fullName);

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
