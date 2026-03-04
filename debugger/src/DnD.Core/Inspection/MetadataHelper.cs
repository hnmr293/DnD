namespace DnD.Core.Inspection;

using ClrDebug;

/// <summary>
/// Helpers for searching metadata (fields, methods, properties) on ICorDebug objects.
/// </summary>
public static class MetadataHelper
{
    /// <summary>
    /// Try to get a field value by name from an object value.
    /// </summary>
    public static CorDebugValue? TryGetField(CorDebugObjectValue objVal, string fieldName)
    {
        try
        {
            var classType = objVal.Class;
            var module = classType.Module;
            var import = module.GetMetaDataInterface<MetaDataImport>();

            var token = FindFieldToken(import, classType.Token, fieldName);
            if (token.HasValue)
                return objVal.GetFieldValue(classType.Raw, (mdFieldDef)token.Value);
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Find a property getter method (get_PropertyName) on an object.
    /// </summary>
    public static CorDebugFunction? FindPropertyGetter(CorDebugObjectValue objVal, string propertyName)
    {
        return FindMethodByName(objVal, $"get_{propertyName}", 0);
    }

    /// <summary>
    /// Find a method by name and argument count on an object value's type.
    /// </summary>
    public static CorDebugFunction? FindMethodByName(CorDebugObjectValue objVal, string methodName, int argCount)
    {
        try
        {
            var classType = objVal.Class;
            var module = classType.Module;
            var import = module.GetMetaDataInterface<MetaDataImport>();
            var classToken = classType.Token;

            return FindMethodInClassToken(module, import, classToken, methodName, argCount);
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Find a method by name on a given class (CorDebugClass), searching the class token.
    /// </summary>
    public static CorDebugFunction? FindMethodInClass(CorDebugClass classType, string methodName, int argCount)
    {
        try
        {
            var module = classType.Module;
            var import = module.GetMetaDataInterface<MetaDataImport>();
            return FindMethodInClassToken(module, import, classType.Token, methodName, argCount);
        }
        catch { }
        return null;
    }

    private static CorDebugFunction? FindMethodInClassToken(
        CorDebugModule module, MetaDataImport import, mdTypeDef classToken,
        string methodName, int argCount)
    {
        var enumHandle = IntPtr.Zero;
        var methodTokens = new mdMethodDef[64];

        try
        {
            var count = import.EnumMethods(ref enumHandle, classToken, methodTokens);
            while (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var props = import.GetMethodProps(methodTokens[i]);
                    if (props.szMethod == methodName)
                    {
                        // Check param count
                        var paramCount = CountMethodParams(import, methodTokens[i]);
                        if (paramCount == argCount)
                        {
                            import.CloseEnum(enumHandle);
                            return module.GetFunctionFromToken(methodTokens[i]);
                        }
                    }
                }
                count = import.EnumMethods(ref enumHandle, classToken, methodTokens);
            }

            if (enumHandle != IntPtr.Zero)
                import.CloseEnum(enumHandle);

            // If no exact param match, try by name only (for default/optional params)
            enumHandle = IntPtr.Zero;
            count = import.EnumMethods(ref enumHandle, classToken, methodTokens);
            while (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var props = import.GetMethodProps(methodTokens[i]);
                    if (props.szMethod == methodName)
                    {
                        import.CloseEnum(enumHandle);
                        return module.GetFunctionFromToken(methodTokens[i]);
                    }
                }
                count = import.EnumMethods(ref enumHandle, classToken, methodTokens);
            }

            if (enumHandle != IntPtr.Zero)
                import.CloseEnum(enumHandle);
        }
        catch
        {
            try { if (enumHandle != IntPtr.Zero) import.CloseEnum(enumHandle); } catch { }
        }

        return null;
    }

    private static int CountMethodParams(MetaDataImport import, mdMethodDef methodToken)
    {
        var enumHandle = IntPtr.Zero;
        var paramTokens = new mdParamDef[32];
        int total = 0;
        try
        {
            var count = import.EnumParams(ref enumHandle, methodToken, paramTokens);
            while (count > 0)
            {
                total += count;
                count = import.EnumParams(ref enumHandle, methodToken, paramTokens);
            }
            if (enumHandle != IntPtr.Zero)
                import.CloseEnum(enumHandle);
        }
        catch
        {
            try { if (enumHandle != IntPtr.Zero) import.CloseEnum(enumHandle); } catch { }
        }
        return total;
    }

    private static int? FindFieldToken(MetaDataImport import, mdTypeDef classToken, string fieldName)
    {
        var enumHandle = IntPtr.Zero;
        var fieldTokens = new mdFieldDef[32];

        try
        {
            var count = import.EnumFields(ref enumHandle, classToken, fieldTokens);
            while (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var props = import.GetFieldProps(fieldTokens[i]);
                    if (props.szField == fieldName)
                    {
                        import.CloseEnum(enumHandle);
                        return (int)fieldTokens[i];
                    }
                }
                count = import.EnumFields(ref enumHandle, classToken, fieldTokens);
            }

            if (enumHandle != IntPtr.Zero)
                import.CloseEnum(enumHandle);
        }
        catch
        {
            try { if (enumHandle != IntPtr.Zero) import.CloseEnum(enumHandle); } catch { }
        }

        return null;
    }
}
