namespace DnD.Core.Inspection;

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ClrDebug;
using DnD.Core.Symbols;
using DnD.Protocol;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using StreamJsonRpc;

/// <summary>
/// Evaluates C# expressions by compiling them with Roslyn and executing
/// the compiled code in the debuggee via func-eval.
/// Supports new, typeof, lambda, LINQ, generics, and private member access.
/// </summary>
public class RoslynEvaluator
{
    private readonly IEvalExecutor _evalExecutor;
    private readonly CorDebugThread _thread;
    private readonly DebugSession _session;
    private readonly ValueReader _valueReader = new();

    private static readonly TimeSpan EvalTimeout = TimeSpan.FromSeconds(30);

    public RoslynEvaluator(IEvalExecutor evalExecutor, CorDebugThread thread, DebugSession session)
    {
        _evalExecutor = evalExecutor;
        _thread = thread;
        _session = session;
    }

    public async Task<EvaluateResponse> EvaluateAsync(
        string expression, CorDebugILFrame frame,
        ISymbolReader? reader,
        CorDebugValue? exceptionValue,
        CorDebugValue? returnValue)
    {
        // Step 1: Collect context
        var context = CollectContext(frame, reader, exceptionValue, returnValue);

        // Step 2: Generate wrapper source
        var wrapperSource = GenerateWrapper(expression, context);

        // Step 3: Compile with Roslyn
        var (assemblyBytes, evalMethodToken) = Compile(wrapperSource, context);

        // Step 4: Write temp DLL
        var tempPath = Path.Combine(Path.GetTempPath(), $"__DnDEval_{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllBytes(tempPath, assemblyBytes);

            // Step 5: Load DLL into debuggee
            await LoadAssemblyInDebuggee(tempPath);

            // Step 6: Call the compiled __Eval method
            var result = await CallEvalMethod(tempPath, evalMethodToken, frame, context);

            // Step 7: Read result
            if (result == null)
                return new EvaluateResponse("null", "null");

            var (displayValue, type) = _valueReader.Read(result);
            // Null reference values: display as null
            if (displayValue == "null")
                return new EvaluateResponse("null", "null");
            return new EvaluateResponse(displayValue, type);
        }
        finally
        {
            // Clean up temp DLL (best effort)
            try { File.Delete(tempPath); } catch { }
        }
    }

    // ── Context collection ──────────────────────────────────────────

    private EvalContext CollectContext(
        CorDebugILFrame frame, ISymbolReader? reader,
        CorDebugValue? exceptionValue, CorDebugValue? returnValue)
    {
        var ctx = new EvalContext();

        // Get source file for using extraction
        try
        {
            if (reader != null)
            {
                var seqPoints = reader.GetSequencePoints((int)frame.Function.Token);
                if (seqPoints.Count > 0)
                    ctx.SourceFilePath = seqPoints[0].FilePath;
            }
        }
        catch { }

        // Determine if method is static and get this type
        try
        {
            var module = frame.Function.Module;
            var import = module.GetMetaDataInterface<MetaDataImport>();
            var methodToken = (mdMethodDef)frame.Function.Token;
            var methodProps = import.GetMethodProps(methodToken);
            ctx.IsStatic = methodProps.pdwAttr.HasFlag(CorMethodAttr.mdStatic);

            if (!ctx.IsStatic)
            {
                try
                {
                    var thisValue = frame.GetArgument(0);
                    var thisTypeName = TypeNameResolver.GetCSharpTypeName(thisValue);

                    // State machine: unwrap <>4__this as the real "this" and
                    // extract hoisted fields as locals
                    if (StateMachineHelper.IsStateMachineType(thisTypeName))
                    {
                        var smObj = DereferenceToObject(thisValue);
                        if (smObj != null)
                        {
                            // Get outer "this" if the async method is an instance method
                            var outerThis = StateMachineHelper.GetOuterThis(smObj);
                            if (outerThis != null)
                            {
                                ctx.ThisValue = outerThis;
                                ctx.ThisTypeName = TypeNameResolver.GetCSharpTypeName(outerThis);
                            }
                            // else: static async method — no outer this

                            // Add state machine fields as locals
                            var unwrapped = StateMachineHelper.UnwrapFields(smObj);
                            foreach (var (name, value) in unwrapped)
                            {
                                if (name == "this") continue; // already handled above
                                try
                                {
                                    var typeName = TypeNameResolver.GetCSharpTypeName(value);
                                    ctx.Locals.Add(new EvalLocal(name, typeName, value));
                                }
                                catch { }
                            }
                            ctx.IsStateMachine = true;
                        }
                    }
                    else
                    {
                        ctx.ThisValue = thisValue;
                        ctx.ThisTypeName = thisTypeName;
                    }
                }
                catch { }
            }
        }
        catch { }

        // Collect local variables and parameters (skip if state machine — already handled)
        if (!ctx.IsStateMachine)
        {
            if (reader != null)
            {
                try
                {
                    var ipResult = frame.IP;
                    var ilOffset = (int)ipResult.pnOffset;
                    var locals = reader.GetLocalVariables((int)frame.Function.Token, ilOffset);
                    foreach (var local in locals)
                    {
                        try
                        {
                            var value = frame.GetLocalVariable(local.SlotIndex);
                            var typeName = TypeNameResolver.GetCSharpTypeName(value);
                            ctx.Locals.Add(new EvalLocal(local.Name, typeName, value));
                        }
                        catch { }
                    }
                }
                catch { }
            }

            try
            {
                var module = frame.Function.Module;
                var import = module.GetMetaDataInterface<MetaDataImport>();
                var methodToken = (mdMethodDef)frame.Function.Token;
                var argNameMap = GetArgumentNameMap(import, methodToken, ctx.IsStatic);

                int startIdx = ctx.IsStatic ? 0 : 1;
                for (int i = startIdx; ; i++)
                {
                    try
                    {
                        var value = frame.GetArgument(i);
                        var name = argNameMap.GetValueOrDefault(i, $"arg{i}");
                        if (name == "this") continue;
                        var typeName = TypeNameResolver.GetCSharpTypeName(value);
                        ctx.Locals.Add(new EvalLocal(name, typeName, value));
                    }
                    catch { break; }
                }
            }
            catch { }
        }

        // Pseudo-variables
        if (exceptionValue != null)
        {
            var typeName = TypeNameResolver.GetCSharpTypeName(exceptionValue);
            ctx.Locals.Add(new EvalLocal("__exception", typeName, exceptionValue));
            ctx.HasException = true;
        }
        if (returnValue != null)
        {
            var typeName = TypeNameResolver.GetCSharpTypeName(returnValue);
            ctx.Locals.Add(new EvalLocal("__returnValue", typeName, returnValue));
            ctx.HasReturnValue = true;
        }

        return ctx;
    }

    private static Dictionary<int, string> GetArgumentNameMap(
        MetaDataImport import, mdMethodDef methodToken, bool isStatic)
    {
        var map = new Dictionary<int, string>();
        if (!isStatic)
            map[0] = "this";

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
                    if (props.pulSequence > 0)
                    {
                        int argIdx = isStatic ? props.pulSequence - 1 : props.pulSequence;
                        map[argIdx] = props.szName;
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
        return map;
    }

    // ── Wrapper code generation ─────────────────────────────────────

    private static string GenerateWrapper(string expression, EvalContext context)
    {
        var sb = new System.Text.StringBuilder();

        // Using directives from source file
        var usings = SourceFileHelper.ExtractUsings(context.SourceFilePath);
        foreach (var u in usings)
            sb.AppendLine(u);
        sb.AppendLine();

        sb.AppendLine("namespace __DnDEval");
        sb.AppendLine("{");
        sb.AppendLine("    internal static class Evaluator");
        sb.AppendLine("    {");

        // For parameters whose types are always public (primitives, System.*), use the
        // real type in the signature — this avoids boxing issues with ICorDebug func-eval.
        // For types that might be internal/private (user-defined), use 'object' to avoid
        // CS0051 (inconsistent accessibility) and cast inside the body where
        // IgnoreAccessibility handles CS0122.
        var paramList = new List<string>();
        var castStatements = new List<string>();
        int paramIdx = 0;

        if (context.ThisValue != null)
        {
            bool useObject = NeedsObjectParameter(context.ThisTypeName!);
            var paramType = useObject ? "object" : context.ThisTypeName!;
            paramList.Add($"            {paramType} __p{paramIdx}");
            if (useObject)
                castStatements.Add($"            var __this = ({context.ThisTypeName})__p{paramIdx};");
            else
                castStatements.Add($"            var __this = __p{paramIdx};");
            paramIdx++;
        }

        foreach (var local in context.Locals)
        {
            bool useObject = NeedsObjectParameter(local.TypeName);
            var paramType = useObject ? "object" : local.TypeName;
            paramList.Add($"            {paramType} __p{paramIdx}");
            if (useObject)
                castStatements.Add($"            var {local.Name} = ({local.TypeName})__p{paramIdx};");
            else
                castStatements.Add($"            var {local.Name} = __p{paramIdx};");
            paramIdx++;
        }

        sb.AppendLine("        internal static object __Eval(");
        sb.AppendLine(string.Join(",\n", paramList));
        sb.AppendLine("        )");
        sb.AppendLine("        {");

        // Typed casts/assignments from parameters
        foreach (var cast in castStatements)
            sb.AppendLine(cast);

        // Rename $exception / $returnValue
        if (context.HasException)
            sb.AppendLine("            var __dollar_exception = __exception;");
        if (context.HasReturnValue)
            sb.AppendLine("            var __dollar_returnValue = __returnValue;");

        // Replace $exception/$returnValue in the expression
        var expr = expression;
        if (context.HasException)
            expr = expr.Replace("$exception", "__dollar_exception");
        if (context.HasReturnValue)
            expr = expr.Replace("$returnValue", "__dollar_returnValue");

        // Replace bare 'this' with '__this' in expression
        if (context.ThisValue != null)
            expr = ReplaceThisKeyword(expr);

        sb.AppendLine($"            return {expr};");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Determines whether a parameter type needs to use 'object' in the method signature.
    /// Primitives and System types are always public and safe to use directly.
    /// User-defined types might be internal and would cause CS0051.
    /// </summary>
    private static bool NeedsObjectParameter(string typeName)
    {
        // Compiler-generated type names (e.g., <Method>d__3) contain '<'
        // which is invalid in C# source. Must use object.
        if (typeName.Contains('<'))
            return true;

        // Primitives — always public
        if (typeName is "int" or "uint" or "long" or "ulong" or
            "short" or "ushort" or "byte" or "sbyte" or
            "float" or "double" or "decimal" or
            "bool" or "char" or "string" or "object")
            return false;

        // System types (e.g., System.Collections.Generic.List<int>)
        if (typeName.StartsWith("System."))
            return false;

        // Arrays — check element type
        if (typeName.EndsWith("[]"))
            return NeedsObjectParameter(typeName[..^2]);

        // User-defined types — use object to avoid CS0051
        return true;
    }

    private static string ReplaceThisKeyword(string expr)
    {
        // Replace standalone 'this' with '__this' but not 'this_something'
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < expr.Length; i++)
        {
            if (i + 4 <= expr.Length && expr.Substring(i, 4) == "this")
            {
                bool precededByIdentChar = i > 0 && (char.IsLetterOrDigit(expr[i - 1]) || expr[i - 1] == '_');
                bool followedByIdentChar = i + 4 < expr.Length && (char.IsLetterOrDigit(expr[i + 4]) || expr[i + 4] == '_');
                if (!precededByIdentChar && !followedByIdentChar)
                {
                    result.Append("__this");
                    i += 3; // skip 'this' (loop increments by 1)
                    continue;
                }
            }
            result.Append(expr[i]);
        }
        return result.ToString();
    }

    // ── Roslyn compilation ──────────────────────────────────────────

    private (byte[] AssemblyBytes, int EvalMethodToken) Compile(string wrapperSource, EvalContext context)
    {
        var (references, assemblyNames) = CollectReferences();

        // Generate IgnoresAccessChecksTo attributes source
        var attrSource = GenerateIgnoresAccessChecksSource(assemblyNames);
        var attrSyntaxTree = CSharpSyntaxTree.ParseText(attrSource);

        var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            .WithMetadataImportOptions(MetadataImportOptions.All);

        // Set TopLevelBinderFlags.IgnoreAccessibility via reflection.
        // This suppresses compile-time accessibility errors for member access (CS0122)
        // so expressions can reference internal/private members from the debuggee.
        // CS0051 (inconsistent accessibility in declarations) is avoided by using object
        // parameters in the method signature and casting inside the body.
        options = SetIgnoreAccessibility(options);

        var syntaxTree = CSharpSyntaxTree.ParseText(wrapperSource);
        var currentRefs = references;
        var currentSource = wrapperSource;

        // Retry loop: handle CS0009 (bad references) and CS0234 (missing namespaces)
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var compilation = CSharpCompilation.Create(
                assemblyName: $"__DnDEval_{Guid.NewGuid():N}",
                syntaxTrees: [syntaxTree, attrSyntaxTree],
                references: currentRefs,
                options: options);

            using var ms = new MemoryStream();
            var emitResult = compilation.Emit(ms);

            if (emitResult.Success)
            {
                var assemblyBytes = ms.ToArray();
                var evalMethodToken = ReadEvalMethodToken(assemblyBytes);
                return (assemblyBytes, evalMethodToken);
            }

            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

            // CS0009 (can't open metadata file) / CS1509 (not an assembly):
            // bad references from mixed-mode assemblies or native DLLs.
            // Remove the specific bad references and retry.
            var badRefErrors = errors.Where(d => d.Id is "CS0009" or "CS1509").ToList();
            if (badRefErrors.Count > 0)
            {
                var badPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var diag in badRefErrors)
                {
                    var msg = diag.GetMessage();
                    // Roslyn uses Unicode smart quotes (\u2018 / \u2019) in messages
                    var start = msg.IndexOfAny(['\u2018', '\'']);
                    var end = start >= 0 ? msg.IndexOfAny(['\u2019', '\''], start + 1) : -1;
                    if (start >= 0 && end > start)
                        badPaths.Add(msg.Substring(start + 1, end - start - 1));
                }
                if (badPaths.Count > 0)
                {
                    currentRefs = currentRefs
                        .Where(r => r is not PortableExecutableReference pe ||
                                    pe.FilePath == null ||
                                    !badPaths.Contains(pe.FilePath))
                        .ToList();
                    continue;
                }
            }

            // CS0234/CS0246: missing namespace/type in using directive.
            // Strip the problematic using directives and retry.
            var usingErrors = errors
                .Where(d => d.Id is "CS0234" or "CS0246" && d.Location.SourceTree == syntaxTree)
                .ToList();
            if (usingErrors.Count > 0 && usingErrors.Count == errors.Count(d => d.Id is not "CS0009" and not "CS1509"))
            {
                currentSource = StripProblematicUsings(currentSource, usingErrors);
                syntaxTree = CSharpSyntaxTree.ParseText(currentSource);
                continue;
            }

            // Non-recoverable errors
            var errorMessages = errors
                .Where(d => d.Id is not "CS0009" and not "CS1509")
                .Select(d => d.GetMessage())
                .Take(5);
            throw new LocalRpcException($"Compilation failed: {string.Join("; ", errorMessages)}")
            { ErrorCode = ErrorCodes.EvaluationFailed };
        }

        throw new LocalRpcException("Compilation failed after retries")
        { ErrorCode = ErrorCodes.EvaluationFailed };
    }

    private (List<MetadataReference> References, List<string> AssemblyNames) CollectReferences()
    {
        var addedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assemblyNames = new List<string>();
        var references = new List<MetadataReference>();

        // Loaded modules — already running in the CLR, so always valid references.
        // Use HasMetadata check only (R2R assemblies have native code but are valid).
        foreach (var (path, _) in _session.Modules)
        {
            if (File.Exists(path) && addedPaths.Add(path) && HasManagedMetadata(path))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                    var asmName = System.Reflection.AssemblyName.GetAssemblyName(path);
                    if (asmName.Name != null)
                        assemblyNames.Add(asmName.Name);
                }
                catch { }
            }
        }

        // Runtime directory DLLs — unloaded assemblies for compilation references.
        var runtimeDir = FindRuntimeDirectory();
        if (runtimeDir != null)
        {
            foreach (var dll in Directory.EnumerateFiles(runtimeDir, "*.dll"))
            {
                if (addedPaths.Add(dll) && HasManagedMetadata(dll))
                {
                    try
                    {
                        references.Add(MetadataReference.CreateFromFile(dll));
                        var asmName = System.Reflection.AssemblyName.GetAssemblyName(dll);
                        if (asmName.Name != null)
                            assemblyNames.Add(asmName.Name);
                    }
                    catch { }
                }
            }
        }

        return (references, assemblyNames);
    }

    /// <summary>
    /// Removes using directives that caused CS0234/CS0246 compilation errors.
    /// </summary>
    private static string StripProblematicUsings(string source, List<Diagnostic> usingErrors)
    {
        var lines = source.Split('\n');
        var errorLineNumbers = new HashSet<int>();
        foreach (var error in usingErrors)
        {
            var lineSpan = error.Location.GetLineSpan();
            errorLineNumbers.Add(lineSpan.StartLinePosition.Line);
        }

        var result = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!errorLineNumbers.Contains(i))
                result.AppendLine(lines[i].TrimEnd('\r'));
        }
        return result.ToString();
    }

    private static string GenerateIgnoresAccessChecksSource(List<string> assemblyNames)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var name in assemblyNames)
            sb.AppendLine($"[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo(\"{name}\")]");

        sb.AppendLine();
        sb.AppendLine("namespace System.Runtime.CompilerServices");
        sb.AppendLine("{");
        sb.AppendLine("    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]");
        sb.AppendLine("    internal class IgnoresAccessChecksToAttribute : Attribute");
        sb.AppendLine("    {");
        sb.AppendLine("        public IgnoresAccessChecksToAttribute(string assemblyName) { }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static int ReadEvalMethodToken(byte[] assemblyBytes)
    {
        using var peReader = new PEReader(new MemoryStream(assemblyBytes));
        var metadataReader = peReader.GetMetadataReader();
        foreach (var methodHandle in metadataReader.MethodDefinitions)
        {
            var method = metadataReader.GetMethodDefinition(methodHandle);
            if (metadataReader.GetString(method.Name) == "__Eval")
                return MetadataTokens.GetToken(methodHandle);
        }
        throw new InvalidOperationException("__Eval method not found in compiled assembly");
    }

    // ── Debuggee interaction ────────────────────────────────────────

    private async Task LoadAssemblyInDebuggee(string dllPath)
    {
        var loadFromFunc = FindAssemblyLoadFrom();

        // Create string argument for the DLL path
        var pathString = await _evalExecutor.ExecuteEvalAsync(eval =>
        {
            eval.NewString(dllPath);
        }, _thread, EvalTimeout);

        // Call Assembly.LoadFrom(dllPath)
        await _evalExecutor.ExecuteEvalAsync(eval =>
        {
            var rawArgs = new ICorDebugValue[] { pathString.Raw };
            eval.CallFunction(loadFromFunc.Raw, 1, rawArgs);
        }, _thread, EvalTimeout);
    }

    private CorDebugFunction FindAssemblyLoadFrom()
    {
        // Find CoreLib module (System.Private.CoreLib or mscorlib)
        CorDebugModule? coreLibModule = null;
        foreach (var (path, (module, _)) in _session.Modules)
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase))
            {
                coreLibModule = module;
                break;
            }
        }

        if (coreLibModule == null)
            throw new LocalRpcException("Cannot find CoreLib module")
            { ErrorCode = ErrorCodes.EvaluationFailed };

        var import = coreLibModule.GetMetaDataInterface<MetaDataImport>();

        // Find System.Reflection.Assembly type
        var assemblyTypeDef = import.FindTypeDefByName("System.Reflection.Assembly", 0);

        // Find LoadFrom method (static, 1 string parameter)
        var enumHandle = IntPtr.Zero;
        var methodTokens = new mdMethodDef[64];
        try
        {
            var count = import.EnumMethods(ref enumHandle, assemblyTypeDef, methodTokens);
            while (count > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    var props = import.GetMethodProps(methodTokens[i]);
                    if (props.szMethod == "LoadFrom" &&
                        props.pdwAttr.HasFlag(CorMethodAttr.mdStatic))
                    {
                        // Check it's the single-string overload
                        var paramCount = CountParams(import, methodTokens[i]);
                        if (paramCount == 1)
                        {
                            import.CloseEnum(enumHandle);
                            return coreLibModule.GetFunctionFromToken(methodTokens[i]);
                        }
                    }
                }
                count = import.EnumMethods(ref enumHandle, assemblyTypeDef, methodTokens);
            }
            if (enumHandle != IntPtr.Zero) import.CloseEnum(enumHandle);
        }
        catch
        {
            try { if (enumHandle != IntPtr.Zero) import.CloseEnum(enumHandle); } catch { }
        }

        throw new LocalRpcException("Cannot find Assembly.LoadFrom method")
        { ErrorCode = ErrorCodes.EvaluationFailed };
    }

    private static int CountParams(MetaDataImport import, mdMethodDef methodToken)
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
            if (enumHandle != IntPtr.Zero) import.CloseEnum(enumHandle);
        }
        catch
        {
            try { if (enumHandle != IntPtr.Zero) import.CloseEnum(enumHandle); } catch { }
        }
        return total;
    }

    private async Task<CorDebugValue?> CallEvalMethod(
        string dllPath, int evalMethodToken, CorDebugILFrame frame, EvalContext context)
    {
        // Find the loaded module
        if (!_session.Modules.TryGetValue(dllPath, out var moduleEntry))
            throw new LocalRpcException("Compiled assembly was not loaded into debuggee")
            { ErrorCode = ErrorCodes.EvaluationFailed };

        var evalModule = moduleEntry.Module;
        var evalFunction = evalModule.GetFunctionFromToken((mdMethodDef)evalMethodToken);

        // Build argument array
        var args = new List<ICorDebugValue>();
        if (context.ThisValue != null)
            args.Add(context.ThisValue.Raw);
        foreach (var local in context.Locals)
            args.Add(local.Value.Raw);

        var rawArgs = args.ToArray();
        return await _evalExecutor.ExecuteEvalAsync(eval =>
        {
            eval.CallFunction(evalFunction.Raw, rawArgs.Length, rawArgs);
        }, _thread, EvalTimeout);
    }

    /// <summary>
    /// Checks whether a DLL has managed metadata (for loaded modules).
    /// Allows R2R (Ready to Run) assemblies which have native code but valid metadata.
    /// </summary>
    private static bool HasManagedMetadata(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var peReader = new PEReader(stream);
            return peReader.HasMetadata;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Finds the .NET runtime directory from the CoreLib module path.
    /// For .NET Core: e.g., C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.x\
    /// For .NET Framework: e.g., C:\Windows\Microsoft.NET\Framework64\v4.0.30319\
    /// </summary>
    private string? FindRuntimeDirectory()
    {
        foreach (var (path, _) in _session.Modules)
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir))
                {
                    // On .NET Framework, mscorlib may be loaded from the NativeImages cache
                    // (e.g., C:\Windows\assembly\NativeImages_v4.0.30319_64\mscorlib\...\mscorlib.ni.dll)
                    // which doesn't contain other framework assemblies like System.Core.dll.
                    // Fall back to the standard framework directory.
                    if (dir.Contains("NativeImages", StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(Path.Combine(dir, "System.dll")))
                    {
                        var frameworkDir = FindFrameworkDirectory();
                        if (frameworkDir != null)
                            return frameworkDir;
                    }
                    return dir;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Finds the .NET Framework directory by looking at loaded module paths.
    /// Modules like System.dll, System.Core.dll are typically in the framework directory
    /// even when mscorlib is loaded from the NativeImages cache.
    /// </summary>
    private string? FindFrameworkDirectory()
    {
        // Check loaded modules for System.dll — it's always in the real framework dir
        foreach (var (path, _) in _session.Modules)
        {
            var fileName = Path.GetFileName(path);
            if (fileName.Equals("System.dll", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("System.Core.dll", StringComparison.OrdinalIgnoreCase))
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null && Directory.Exists(dir))
                    return dir;
            }
        }

        // Last resort: well-known .NET Framework 4.x path
        var wellKnown = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "Microsoft.NET", "Framework64", "v4.0.30319");
        if (Directory.Exists(wellKnown))
            return wellKnown;

        return null;
    }

    // ── Supporting types ────────────────────────────────────────────

    private class EvalContext
    {
        public string? SourceFilePath { get; set; }
        public bool IsStatic { get; set; }
        public bool IsStateMachine { get; set; }
        public string? ThisTypeName { get; set; }
        public CorDebugValue? ThisValue { get; set; }
        public List<EvalLocal> Locals { get; } = new();
        public bool HasException { get; set; }
        public bool HasReturnValue { get; set; }
    }

    private static CorDebugObjectValue? DereferenceToObject(CorDebugValue value)
    {
        if (value is CorDebugReferenceValue refVal)
        {
            if (refVal.IsNull) return null;
            value = refVal.Dereference();
        }
        if (value is CorDebugBoxValue boxVal)
            value = boxVal.Object;
        return value as CorDebugObjectValue;
    }

    private record EvalLocal(string Name, string TypeName, CorDebugValue Value);

    /// <summary>
    /// Sets TopLevelBinderFlags.IgnoreAccessibility on CSharpCompilationOptions via reflection.
    /// This is an internal Roslyn API that suppresses compile-time accessibility errors (CS0050, CS0122)
    /// so expressions can reference internal/private types from the debuggee.
    /// </summary>
    private static CSharpCompilationOptions SetIgnoreAccessibility(CSharpCompilationOptions options)
    {
        try
        {
            var csharpAssembly = typeof(CSharpCompilationOptions).Assembly;

            // Find the internal BinderFlags type
            var binderFlagsType = csharpAssembly.GetType("Microsoft.CodeAnalysis.CSharp.BinderFlags");
            if (binderFlagsType == null)
                return options;

            // Get the IgnoreAccessibility static field value
            var ignoreAccessField = binderFlagsType.GetField(
                "IgnoreAccessibility",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (ignoreAccessField == null)
                return options;

            var ignoreAccessValue = ignoreAccessField.GetValue(null);
            if (ignoreAccessValue == null)
                return options;

            // Find WithTopLevelBinderFlags(BinderFlags) internal method
            var withMethod = typeof(CSharpCompilationOptions).GetMethod(
                "WithTopLevelBinderFlags",
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { binderFlagsType },
                null);
            if (withMethod == null)
                return options;

            var result = withMethod.Invoke(options, new[] { ignoreAccessValue });
            if (result is CSharpCompilationOptions newOptions)
                return newOptions;
        }
        catch
        {
            // Reflection failed — accessibility errors may occur for internal/private types
        }

        return options;
    }
}
