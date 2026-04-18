namespace DnD.Core.Inspection;

using System.Text.RegularExpressions;
using ClrDebug;

/// <summary>
/// Detects async/iterator state machines and unwraps their fields
/// into user-visible variable names following Roslyn's naming conventions.
/// </summary>
public static class StateMachineHelper
{
    // Matches compiler-generated state machine type names: <MethodName>d__N
    // May be preceded by namespace/class path (e.g., Namespace.Class/<Method>d__3)
    private static readonly Regex StateMachineTypePattern =
        new(@"<\w+>d__\d+$", RegexOptions.Compiled);

    // Matches hoisted local variable fields: <variableName>5__N
    private static readonly Regex HoistedLocalPattern =
        new(@"^<(.+)>5__\d+$", RegexOptions.Compiled);

    /// <summary>
    /// Returns true if the type name matches Roslyn's state machine naming pattern.
    /// </summary>
    public static bool IsStateMachineType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return false;
        return StateMachineTypePattern.IsMatch(typeName);
    }

    /// <summary>
    /// Attempts to map a state machine field name to the original variable name.
    /// Returns false for internal fields that should be hidden from the user.
    /// </summary>
    public static bool TryGetVariableName(string fieldName, out string variableName)
    {
        variableName = "";

        if (string.IsNullOrEmpty(fieldName))
            return false;

        // <>4__this → the captured outer "this"
        if (fieldName == "<>4__this")
        {
            variableName = "this";
            return true;
        }

        // Fields starting with <> (empty name between angle brackets) are internal
        // Examples: <>1__state, <>t__builder, <>u__1, <>s__1, <>7__wrap1
        if (fieldName.StartsWith("<>"))
            return false;

        // <variableName>5__N → hoisted local variable
        var match = HoistedLocalPattern.Match(fieldName);
        if (match.Success)
        {
            variableName = match.Groups[1].Value;
            return true;
        }

        // Fields starting with < but not matching hoisted pattern → other compiler-generated
        if (fieldName.StartsWith('<'))
            return false;

        // Plain name (no angle brackets) → parameter stored with its original name
        variableName = fieldName;
        return true;
    }

    /// <summary>
    /// Enumerates all fields on the state machine object and returns
    /// user-visible variables with their unwrapped names.
    /// </summary>
    public static List<(string Name, CorDebugValue Value)> UnwrapFields(
        CorDebugObjectValue stateMachineObj)
    {
        var results = new List<(string, CorDebugValue)>();

        // Step 1: Collect field names and tokens
        var fields = EnumAllFields(stateMachineObj);

        // Step 2: Read values for user-visible fields
        var classType = stateMachineObj.Class;
        foreach (var (fieldName, fieldToken) in fields)
        {
            if (!TryGetVariableName(fieldName, out var varName))
                continue;

            try
            {
                var value = stateMachineObj.GetFieldValue(classType.Raw, fieldToken);
                results.Add((varName, value));
            }
            catch { }
        }

        return results;
    }

    private static List<(string Name, mdFieldDef Token)> EnumAllFields(
        CorDebugObjectValue objVal)
    {
        var results = new List<(string, mdFieldDef)>();
        var classType = objVal.Class;
        var module = classType.Module;
        var import = module.GetMetaDataInterface<MetaDataImport>();

        var enumHandle = IntPtr.Zero;
        var fieldTokens = new mdFieldDef[64];
        try
        {
            var count = import.EnumFields(ref enumHandle, classType.Token, fieldTokens);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var props = import.GetFieldProps(fieldTokens[i]);
                    results.Add((props.szField, fieldTokens[i]));
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            if (enumHandle != IntPtr.Zero)
            {
                try { import.CloseEnum(enumHandle); } catch { }
            }
        }

        return results;
    }

    /// <summary>
    /// Returns the captured outer "this" (<>4__this field) from the state machine,
    /// or null if the async method is static.
    /// </summary>
    public static CorDebugValue? GetOuterThis(CorDebugObjectValue stateMachineObj)
    {
        return MetadataHelper.TryGetField(stateMachineObj, "<>4__this");
    }

    /// <summary>
    /// Finds a state machine field matching the given variable name.
    /// Used by SimpleEvaluator to resolve variable references in async methods.
    /// </summary>
    public static CorDebugValue? FindVariable(
        CorDebugObjectValue stateMachineObj, string variableName)
    {
        if (variableName == "this")
            return GetOuterThis(stateMachineObj);

        var classType = stateMachineObj.Class;
        var module = classType.Module;
        var import = module.GetMetaDataInterface<MetaDataImport>();

        var enumHandle = IntPtr.Zero;
        var fieldTokens = new mdFieldDef[64];
        try
        {
            var count = import.EnumFields(ref enumHandle, classType.Token, fieldTokens);
            while (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        var props = import.GetFieldProps(fieldTokens[i]);
                        if (TryGetVariableName(props.szField, out var varName) &&
                            varName == variableName)
                        {
                            return stateMachineObj.GetFieldValue(
                                classType.Raw, fieldTokens[i]);
                        }
                    }
                    catch { }
                }
                count = import.EnumFields(ref enumHandle, classType.Token, fieldTokens);
            }
        }
        finally
        {
            if (enumHandle != IntPtr.Zero)
                import.CloseEnum(enumHandle);
        }

        return null;
    }
}
