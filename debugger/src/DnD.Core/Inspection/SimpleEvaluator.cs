namespace DnD.Core.Inspection;

using ClrDebug;
using DnD.Core.Symbols;
using DnD.Protocol;
using StreamJsonRpc;

public class SimpleEvaluator
{
    public EvaluateResponse Evaluate(
        string expression, CorDebugILFrame frame,
        ISymbolReader? reader,
        CorDebugValue? exceptionValue = null,
        CorDebugValue? returnValue = null)
    {
        try
        {
            var segments = ParseExpression(expression);
            if (segments.Count == 0)
                throw new LocalRpcException("Empty expression")
                { ErrorCode = ErrorCodes.EvaluationFailed };

            var value = ResolveFirstSegment(segments[0], frame, reader, exceptionValue, returnValue)
                ?? throw new LocalRpcException($"Variable '{segments[0]}' not found")
                { ErrorCode = ErrorCodes.EvaluationFailed };
            for (int i = 1; i < segments.Count; i++)
            {
                value = NavigateSegment(value, segments[i]) ?? throw new LocalRpcException($"Cannot resolve '{string.Join(".", segments.Take(i + 1))}'")
                { ErrorCode = ErrorCodes.EvaluationFailed };
            }

            var valueReader = new ValueReader();
            var (displayValue, type) = valueReader.Read(value);
            return new EvaluateResponse(displayValue, type);
        }
        catch (LocalRpcException) { throw; }
        catch (Exception ex)
        {
            throw new LocalRpcException($"Evaluation failed: {ex.Message}")
            { ErrorCode = ErrorCodes.EvaluationFailed };
        }
    }

    private static List<string> ParseExpression(string expression)
    {
        var segments = new List<string>();
        var current = "";

        for (int i = 0; i < expression.Length; i++)
        {
            if (expression[i] == '.')
            {
                if (current.Length > 0)
                {
                    segments.Add(current);
                    current = "";
                }
            }
            else if (expression[i] == '[')
            {
                if (current.Length > 0)
                {
                    segments.Add(current);
                    current = "";
                }
                var bracket = "";
                i++;
                while (i < expression.Length && expression[i] != ']')
                {
                    bracket += expression[i];
                    i++;
                }
                segments.Add($"[{bracket}]");
            }
            else
            {
                current += expression[i];
            }
        }

        if (current.Length > 0)
            segments.Add(current);

        return segments;
    }

    private static CorDebugValue? ResolveFirstSegment(
        string name, CorDebugILFrame frame, ISymbolReader? reader,
        CorDebugValue? exceptionValue = null,
        CorDebugValue? returnValue = null)
    {
        if (name == "$exception" && exceptionValue != null)
            return exceptionValue;
        if (name == "$returnValue" && returnValue != null)
            return returnValue;

        if (name == "this")
        {
            try { return frame.GetArgument(0); }
            catch { return null; }
        }

        if (reader != null)
        {
            try
            {
                var ipResult = frame.IP;
                var ilOffset = (int)ipResult.pnOffset;
                var locals = reader.GetLocalVariables((int)frame.Function.Token, ilOffset);
                foreach (var local in locals)
                {
                    if (local.Name == name)
                    {
                        try { return frame.GetLocalVariable(local.SlotIndex); }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // Look up parameter by name from metadata
        try
        {
            var module = frame.Function.Module;
            var import = module.GetMetaDataInterface<MetaDataImport>();
            var methodToken = (mdMethodDef)frame.Function.Token;
            var methodProps = import.GetMethodProps(methodToken);
            bool isStatic = methodProps.pdwAttr.HasFlag(CorMethodAttr.mdStatic);
            var enumHandle = IntPtr.Zero;
            var paramTokens = new mdParamDef[32];
            try
            {
                var count = import.EnumParams(ref enumHandle, methodToken, paramTokens);
                while (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        var props = import.GetParamProps(paramTokens[i]);
                        if (props.szName == name && props.pulSequence > 0)
                        {
                            int paramArgIndex = isStatic
                                ? props.pulSequence - 1
                                : props.pulSequence;
                            if (enumHandle != IntPtr.Zero) import.CloseEnum(enumHandle);
                            try { return frame.GetArgument(paramArgIndex); }
                            catch { return null; }
                        }
                    }
                    count = import.EnumParams(ref enumHandle, methodToken, paramTokens);
                }
                if (enumHandle != IntPtr.Zero) import.CloseEnum(enumHandle);
            }
            catch
            {
                try { if (enumHandle != IntPtr.Zero) import.CloseEnum(enumHandle); } catch { }
            }
        }
        catch { }

        if (name.StartsWith("arg") && int.TryParse(name[3..], out var argIndex))
        {
            try { return frame.GetArgument(argIndex); }
            catch { return null; }
        }

        // Fallback: check if we're in a state machine and look up the field
        try
        {
            var thisVal = frame.GetArgument(0);
            var typeName = TypeNameResolver.GetCSharpTypeName(thisVal);
            if (StateMachineHelper.IsStateMachineType(typeName))
            {
                if (thisVal is CorDebugReferenceValue refVal2 && !refVal2.IsNull)
                    thisVal = refVal2.Dereference();
                if (thisVal is CorDebugBoxValue boxVal2)
                    thisVal = boxVal2.Object;
                if (thisVal is CorDebugObjectValue objVal)
                    return StateMachineHelper.FindVariable(objVal, name);
            }
        }
        catch { }

        return null;
    }

    private static CorDebugValue? NavigateSegment(CorDebugValue value, string segment)
    {
        if (value is CorDebugReferenceValue refVal)
        {
            if (refVal.IsNull) return null;
            value = refVal.Dereference();
        }

        if (value is CorDebugBoxValue boxVal)
            value = boxVal.Object;

        // Array index: [n]
        if (segment.StartsWith('[') && segment.EndsWith(']'))
        {
            if (value is CorDebugArrayValue arrVal)
            {
                var indexStr = segment[1..^1];
                if (int.TryParse(indexStr, out var index))
                {
                    try { return arrVal.GetElementAtPosition(index); }
                    catch { return null; }
                }
            }
            return null;
        }

        // Field access
        if (value is CorDebugObjectValue objVal)
        {
            try
            {
                var classType = objVal.Class;
                var module = classType.Module;
                var import = module.GetMetaDataInterface<MetaDataImport>();

                var fieldToken = FindFieldByName(import, classType.Token, segment);
                if (fieldToken.HasValue)
                {
                    return objVal.GetFieldValue(classType.Raw, (mdFieldDef)fieldToken.Value);
                }
            }
            catch { }
        }

        return null;
    }

    private static int? FindFieldByName(MetaDataImport import, mdTypeDef classToken, string fieldName)
    {
        try
        {
            var enumHandle = IntPtr.Zero;
            var fieldTokens = new mdFieldDef[32];

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
        catch { }

        return null;
    }
}
