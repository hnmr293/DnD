namespace DnD.Core.Inspection;

using ClrDebug;
using DnD.Core.Symbols;
using DnD.Protocol;
using StreamJsonRpc;
using static ExpressionParser;

/// <summary>
/// Interface for executing func-evals on the debuggee.
/// Decouples FuncEvalEvaluator from DebuggerEngine.
/// </summary>
public interface IEvalExecutor
{
    Task<CorDebugValue> ExecuteEvalAsync(
        Action<CorDebugEval> setupAction, CorDebugThread thread, TimeSpan? timeout = null);
}

/// <summary>
/// Evaluates parsed expression ASTs using ICorDebugEval for property access,
/// method calls, and arithmetic that cannot be resolved by simple field lookup.
/// </summary>
public class FuncEvalEvaluator
{
    private readonly IEvalExecutor _evalExecutor;
    private readonly CorDebugThread _thread;
    private readonly CorDebugILFrame _frame;
    private readonly ISymbolReader? _reader;
    private readonly VariableStore _store;
    private readonly CorDebugValue? _exceptionValue;
    private readonly CorDebugValue? _returnValue;
    private readonly ValueReader _valueReader = new();

    public FuncEvalEvaluator(
        IEvalExecutor evalExecutor,
        CorDebugThread thread,
        CorDebugILFrame frame,
        ISymbolReader? reader,
        VariableStore store,
        CorDebugValue? exceptionValue = null,
        CorDebugValue? returnValue = null)
    {
        _evalExecutor = evalExecutor;
        _thread = thread;
        _frame = frame;
        _reader = reader;
        _store = store;
        _exceptionValue = exceptionValue;
        _returnValue = returnValue;
    }

    public async Task<EvaluateResponse> EvaluateAsync(ExprNode node)
    {
        var value = await EvalNodeAsync(node);
        if (value == null)
            return new EvaluateResponse("null", 0, "null");

        var (displayValue, type, varRef) = _valueReader.Read(value, _store);
        return new EvaluateResponse(displayValue, varRef, type);
    }

    private async Task<CorDebugValue?> EvalNodeAsync(ExprNode node)
    {
        switch (node)
        {
            case LiteralNode lit:
                return await EvalLiteralAsync(lit);

            case NameNode name:
                return ResolveVariable(name.Name);

            case MemberAccessNode member:
                var obj = await EvalNodeAsync(member.Object) ?? throw MakeError($"Cannot access '{member.MemberName}' on null");
                return await ResolveMemberAsync(obj, member.MemberName);

            case MethodCallNode call:
                var callObj = await EvalNodeAsync(call.Object) ?? throw MakeError($"Cannot call '{call.MethodName}' on null");
                var callArgs = new CorDebugValue[call.Arguments.Length];
                for (int i = 0; i < call.Arguments.Length; i++)
                    callArgs[i] = await EvalNodeAsync(call.Arguments[i])
                        ?? throw MakeError("Null argument not supported for method calls");
                return await CallMethodAsync(callObj, call.MethodName, callArgs);

            case IndexAccessNode idx:
                var idxObj = await EvalNodeAsync(idx.Object) ?? throw MakeError("Cannot index null");
                var idxVal = await EvalNodeAsync(idx.Index);
                return await IndexAccessAsync(idxObj, idxVal);

            case BinaryOpNode bin:
                return await EvalBinaryAsync(bin);

            case CastNode cast:
                var castVal = await EvalNodeAsync(cast.Operand);
                return PrimitiveArithmetic.Cast(castVal, cast.TypeName);

            default:
                throw MakeError($"Unsupported expression node: {node.GetType().Name}");
        }
    }

    private CorDebugValue? ResolveVariable(string name)
    {
        if (name == "$exception" && _exceptionValue != null)
            return _exceptionValue;
        if (name == "$returnValue" && _returnValue != null)
            return _returnValue;

        // Delegate to SimpleEvaluator's variable resolution logic
        if (name == "this")
        {
            try { return _frame.GetArgument(0); }
            catch { return null; }
        }

        if (_reader != null)
        {
            try
            {
                var ipResult = _frame.IP;
                var ilOffset = (int)ipResult.pnOffset;
                var locals = _reader.GetLocalVariables((int)_frame.Function.Token, ilOffset);
                foreach (var local in locals)
                {
                    if (local.Name == name)
                    {
                        try { return _frame.GetLocalVariable(local.SlotIndex); }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // Look up parameter by name from metadata
        try
        {
            var module = _frame.Function.Module;
            var import = module.GetMetaDataInterface<MetaDataImport>();
            var methodToken = (mdMethodDef)_frame.Function.Token;
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
                            try { return _frame.GetArgument(paramArgIndex); }
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
            try { return _frame.GetArgument(argIndex); }
            catch { return null; }
        }

        throw MakeError($"Variable '{name}' not found");
    }

    private async Task<CorDebugValue?> EvalLiteralAsync(LiteralNode lit)
    {
        if (lit.Kind == LiteralKind.Null)
            return null;

        if (lit.Kind == LiteralKind.String)
        {
            // NewString requires eval execution (code runs in debuggee)
            return await _evalExecutor.ExecuteEvalAsync(eval =>
            {
                eval.NewString((string)lit.Value!);
            }, _thread);
        }

        if (lit.Kind == LiteralKind.Int || lit.Kind == LiteralKind.Long ||
            lit.Kind == LiteralKind.Double || lit.Kind == LiteralKind.Bool)
        {
            // CreateValue is synchronous — does NOT require Continue
            return CreatePrimitiveValue(lit.Kind switch
            {
                LiteralKind.Int => CorElementType.I4,
                LiteralKind.Long => CorElementType.I8,
                LiteralKind.Double => CorElementType.R8,
                LiteralKind.Bool => CorElementType.Boolean,
                _ => CorElementType.I4,
            }, lit.Value!);
        }

        throw MakeError($"Unsupported literal kind: {lit.Kind}");
    }

    /// <summary>
    /// Create a primitive value in the debuggee without executing code (no Continue needed).
    /// </summary>
    private CorDebugValue CreatePrimitiveValue(CorElementType elementType, object value)
    {
        var eval = _thread.CreateEval();
        var genVal = eval.CreateValue(elementType, null);
        var gv = (CorDebugGenericValue)genVal;
        PrimitiveArithmetic.SetGenericValue(gv, value);
        return genVal;
    }

    private async Task<CorDebugValue?> ResolveMemberAsync(CorDebugValue obj, string memberName)
    {
        var raw = obj; // keep original for passing to func-eval
        obj = Dereference(obj);

        // String special cases
        if (obj is CorDebugStringValue strVal)
        {
            if (memberName == "Length")
                return CreatePrimitiveValue(CorElementType.I4, (int)strVal.Length);
        }

        // Try field first
        if (obj is CorDebugObjectValue objVal)
        {
            var fieldValue = MetadataHelper.TryGetField(objVal, memberName);
            if (fieldValue != null) return fieldValue;

            // Try property getter
            var getter = MetadataHelper.FindPropertyGetter(objVal, memberName);
            if (getter != null)
            {
                return await _evalExecutor.ExecuteEvalAsync(eval =>
                {
                    CallFunction(eval, getter, [raw]);
                }, _thread);
            }
        }

        throw MakeError($"Member '{memberName}' not found");
    }

    private async Task<CorDebugValue?> CallMethodAsync(
        CorDebugValue obj, string methodName, CorDebugValue[] args)
    {
        var raw = obj;
        obj = Dereference(obj);

        if (obj is CorDebugObjectValue objVal)
        {
            var method = MetadataHelper.FindMethodByName(objVal, methodName, args.Length);
            if (method != null)
            {
                var allArgs = new CorDebugValue[args.Length + 1];
                allArgs[0] = raw;
                Array.Copy(args, 0, allArgs, 1, args.Length);
                return await _evalExecutor.ExecuteEvalAsync(eval =>
                {
                    CallFunction(eval, method, allArgs);
                }, _thread);
            }
        }

        throw MakeError($"Method '{methodName}' not found");
    }

    private async Task<CorDebugValue?> IndexAccessAsync(CorDebugValue obj, CorDebugValue? index)
    {
        var raw = obj;
        obj = Dereference(obj);

        if (obj is CorDebugArrayValue arrVal && index != null)
        {
            var idx = PrimitiveArithmetic.GetIntValue(index);
            if (idx.HasValue)
                return arrVal.GetElementAtPosition(idx.Value);
        }

        // Try get_Item for indexer
        if (obj is CorDebugObjectValue objVal && index != null)
        {
            var getItem = MetadataHelper.FindMethodByName(objVal, "get_Item", 1);
            if (getItem != null)
            {
                return await _evalExecutor.ExecuteEvalAsync(eval =>
                {
                    CallFunction(eval, getItem, [raw, index]);
                }, _thread);
            }
        }

        throw MakeError("Index access failed");
    }

    /// <summary>
    /// Calls a function via ICorDebugEval, using CallParameterizedFunction when
    /// the target object is a generic type (e.g., List&lt;int&gt;.Count).
    /// </summary>
    internal static void CallFunction(CorDebugEval eval, CorDebugFunction function, CorDebugValue[] args)
    {
        var rawArgs = new ICorDebugValue[args.Length];
        for (int i = 0; i < args.Length; i++)
            rawArgs[i] = args[i].Raw;

        // Try to get type arguments from the first arg (the 'this' object)
        ICorDebugType[]? typeArgs = null;
        if (args.Length > 0)
        {
            try
            {
                typeArgs = GetTypeArguments(args[0]);
            }
            catch { }
        }

        if (typeArgs != null && typeArgs.Length > 0)
        {
            // Generic type — use CallParameterizedFunction
            ((ICorDebugEval2)eval.Raw).CallParameterizedFunction(
                function.Raw, typeArgs.Length, typeArgs, rawArgs.Length, rawArgs);
        }
        else
        {
            eval.CallFunction(function.Raw, args.Length, rawArgs);
        }
    }

    private static ICorDebugType[]? GetTypeArguments(CorDebugValue value)
    {
        try
        {
            // Get ICorDebugValue2 for exact type
            var val2 = (ICorDebugValue2)value.Raw;
            val2.GetExactType(out var exactType);

            // Enumerate type parameters
            exactType.EnumerateTypeParameters(out var typeParamEnum);
            if (typeParamEnum == null) return null;

            typeParamEnum.GetCount(out var count);
            if (count == 0) return null;

            var types = new ICorDebugType[count];
            for (int i = 0; i < (int)count; i++)
            {
                typeParamEnum.Next(1, out var t, out _);
                types[i] = t;
            }
            return types;
        }
        catch
        {
            return null;
        }
    }

    private async Task<CorDebugValue?> EvalBinaryAsync(BinaryOpNode bin)
    {
        var left = await EvalNodeAsync(bin.Left);
        var right = await EvalNodeAsync(bin.Right);

        // Try primitive arithmetic (no Continue needed)
        var result = PrimitiveArithmetic.TryBinaryOp(left, bin.Op, right);
        if (result != null)
        {
            var (elementType, _) = PrimitiveArithmetic.GetResultType(result);
            return CreatePrimitiveValue(elementType, result);
        }

        // String concatenation with +
        if (bin.Op == BinaryOp.Add)
        {
            var leftStr = GetStringValue(left);
            var rightStr = GetStringValue(right);
            if (leftStr != null || rightStr != null)
            {
                var concatResult = (leftStr ?? "null") + (rightStr ?? "null");
                return await _evalExecutor.ExecuteEvalAsync(eval =>
                {
                    eval.NewString(concatResult);
                }, _thread);
            }
        }

        throw MakeError($"Cannot apply operator '{bin.Op}' to the given operands");
    }

    private static CorDebugValue Dereference(CorDebugValue value)
    {
        if (value is CorDebugReferenceValue refVal)
        {
            if (refVal.IsNull) return value;
            value = refVal.Dereference();
        }
        if (value is CorDebugBoxValue boxVal)
            value = boxVal.Object;
        return value;
    }

    private static string? GetStringValue(CorDebugValue? value)
    {
        if (value == null) return null;
        if (value is CorDebugReferenceValue refVal)
        {
            if (refVal.IsNull) return null;
            value = refVal.Dereference();
        }
        if (value is CorDebugStringValue strVal)
            return strVal.GetString(strVal.Length);
        return null;
    }

    private static LocalRpcException MakeError(string message) =>
        new(message) { ErrorCode = ErrorCodes.EvaluationFailed };
}
