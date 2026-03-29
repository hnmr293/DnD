namespace DnD.Core.Inspection;

using ClrDebug;

/// <summary>
/// Helpers for searching metadata (fields, methods, properties) on ICorDebug objects.
/// </summary>
public static class MetadataHelper
{
    /// <summary>
    /// Try to get a field value by name from an object value.
    /// Walks the type hierarchy to find fields on base classes.
    /// </summary>
    public static CorDebugValue? TryGetField(CorDebugObjectValue objVal, string fieldName)
    {
        try
        {
            foreach (var (module, import, classToken, rawClass) in WalkTypeHierarchy(objVal))
            {
                var token = FindFieldToken(import, classToken, fieldName);
                if (token.HasValue)
                    return objVal.GetFieldValue(rawClass, (mdFieldDef)token.Value);
            }
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
    /// Walks the type hierarchy to find methods on base classes.
    /// </summary>
    public static CorDebugFunction? FindMethodByName(CorDebugObjectValue objVal, string methodName, int argCount)
    {
        try
        {
            foreach (var (module, import, classToken, _) in WalkTypeHierarchy(objVal))
            {
                var result = FindMethodInClassToken(module, import, classToken, methodName, argCount);
                if (result != null) return result;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Find a method by name and argument count on any value's exact runtime type.
    /// Uses ICorDebugValue2.GetExactType to walk the type hierarchy.
    /// Works for CorDebugStringValue and other non-object value types.
    /// </summary>
    public static CorDebugFunction? FindMethodByExactType(CorDebugValue value, string methodName, int argCount)
    {
        try
        {
            var val2 = (ICorDebugValue2)value.Raw;
            val2.GetExactType(out var currentType);

            int depth = 0;
            while (currentType != null && depth++ < 20)
            {
                ICorDebugClass? cls;
                try
                {
                    currentType.GetClass(out cls);
                    if (cls == null) break;
                }
                catch { break; }

                var clsWrapper = new CorDebugClass(cls);
                var result = FindMethodInClass(clsWrapper, methodName, argCount);
                if (result != null) return result;

                try { currentType.GetBase(out currentType); }
                catch { break; }
            }
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

    /// <summary>
    /// Walk the type hierarchy of an object value, yielding (module, import, classToken, rawClass)
    /// for each level from the runtime type up to (but not including) System.Object.
    /// </summary>
    private static IEnumerable<(CorDebugModule module, MetaDataImport import, mdTypeDef classToken, ICorDebugClass rawClass)>
        WalkTypeHierarchy(CorDebugObjectValue objVal)
    {
        ICorDebugType? currentType = null;
        bool fallback = false;
        try
        {
            var val2 = (ICorDebugValue2)objVal.Raw;
            val2.GetExactType(out currentType);
        }
        catch { fallback = true; }

        if (fallback || currentType == null)
        {
            // Fallback: yield only the immediate class
            var classType = objVal.Class;
            var module = classType.Module;
            var import = module.GetMetaDataInterface<MetaDataImport>();
            yield return (module, import, classType.Token, classType.Raw);
            yield break;
        }

        int depth = 0;
        while (currentType != null && depth++ < 20)
        {
            ICorDebugClass? cls;
            try
            {
                currentType.GetClass(out cls);
                if (cls == null) break;
            }
            catch { break; }

            CorDebugClass clsWrapper;
            CorDebugModule mod;
            MetaDataImport imp;
            try
            {
                clsWrapper = new CorDebugClass(cls);
                mod = clsWrapper.Module;
                imp = mod.GetMetaDataInterface<MetaDataImport>();
            }
            catch { break; }

            yield return (mod, imp, clsWrapper.Token, cls);

            try { currentType.GetBase(out currentType); }
            catch { break; }
        }
    }

    /// <summary>
    /// Enumerate all property getters (get_* methods with 0 parameters) on a class.
    /// Returns (propertyName, getter function) pairs.
    /// </summary>
    public static List<(string PropertyName, CorDebugFunction Getter)> EnumPropertyGetters(
        CorDebugModule module, MetaDataImport import, mdTypeDef classToken)
    {
        var result = new List<(string, CorDebugFunction)>();
        var enumHandle = IntPtr.Zero;
        var methodTokens = new mdMethodDef[64];

        try
        {
            var count = import.EnumMethods(ref enumHandle, classToken, methodTokens);
            while (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var props = import.GetMethodProps(methodTokens[i]);
                        if (props.szMethod.StartsWith("get_"))
                        {
                            var paramCount = CountMethodParams(import, methodTokens[i]);
                            if (paramCount == 0)
                            {
                                var propertyName = props.szMethod[4..];
                                var function = module.GetFunctionFromToken(methodTokens[i]);
                                result.Add((propertyName, function));
                            }
                        }
                    }
                    catch { }
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

        return result;
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
