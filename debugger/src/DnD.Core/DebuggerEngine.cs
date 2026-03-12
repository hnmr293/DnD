namespace DnD.Core;

using System.Reflection.PortableExecutable;
using ClrDebug;
using DnD.Core.Callbacks;
using DnD.Core.Runtime;
using DnD.Protocol;
using StreamJsonRpc;

public class DebuggerEngine : IDebuggerEngine, IDisposable
{
    private DebuggerState _state = DebuggerState.NotStarted;
    private CorDebug? _corDebug;
    private CorDebugProcess? _process;
    private ManagedCallbackHandler? _callbackHandler;
    private readonly IProcessLauncher _launcher;

    private CorDebugThread? _stoppedThread;
    private readonly object _lock = new();
    private bool _stopAtEntry;
    private string? _programPath;
    private bool _exitedEventFired;

    private readonly Dictionary<int, CorDebugILFrame> _frameMap = new();
    private int _nextFrameId;

    private readonly Dictionary<string, (CorDebugModule Module, Symbols.ISymbolReader? Reader)> _modules = new();
    private readonly BreakpointManager _breakpointManager;
    private readonly Inspection.VariableStore _variableStore = new();
    private TaskCompletionSource<(CorDebugEval Eval, bool Success)>? _evalTcs;

    public event EventHandler<StoppedEventArgs>? Stopped;
    public event EventHandler<ExitedEventArgs>? Exited;
    public event EventHandler<OutputEventArgs>? Output;

    public DebuggerEngine(IProcessLauncher launcher)
    {
        _launcher = launcher;
        _breakpointManager = new BreakpointManager(_modules);
    }

    public Task<LaunchResponse> LaunchAsync(LaunchRequest request)
    {
        EnsureState(DebuggerState.NotStarted);

        _stopAtEntry = request.StopAtEntry;
        _programPath = Path.GetFullPath(request.Program);

        _callbackHandler = new ManagedCallbackHandler();
        WireCallbackEvents();

        var result = _launcher.Launch(
            request.Program, request.Args, request.Cwd, request.Env,
            _callbackHandler.Callback);

        _corDebug = result.CorDebug;
        _process = result.Process;
        _state = DebuggerState.Running;

        // Start reading debuggee's stdout/stderr on background threads
        if (result.StandardOutput != null)
            StartOutputReader(result.StandardOutput, OutputCategory.Stdout);
        if (result.StandardError != null)
            StartOutputReader(result.StandardError, OutputCategory.Stderr);

        return Task.FromResult(new LaunchResponse(ProcessId: _process.Id));
    }

    public Task<AttachResponse> AttachAsync(AttachRequest request)
    {
        EnsureState(DebuggerState.NotStarted);

        _callbackHandler = new ManagedCallbackHandler();
        WireCallbackEvents();

        var result = _launcher.Attach(request.ProcessId, _callbackHandler.Callback);

        _corDebug = result.CorDebug;
        _process = result.Process;
        _state = DebuggerState.Running;

        return Task.FromResult(new AttachResponse(ProcessId: _process.Id));
    }

    public Task DetachAsync()
    {
        if (_state == DebuggerState.Terminated || _state == DebuggerState.NotStarted)
            return Task.CompletedTask;

        try { _process?.Detach(); }
        catch { }

        _state = DebuggerState.Terminated;
        return Task.CompletedTask;
    }

    public async Task TerminateAsync()
    {
        if (_state == DebuggerState.Terminated)
        {
            // Ensure exited event is fired even if already terminated (e.g., callback already ran)
            lock (_lock)
            {
                if (_exitedEventFired) return;
                _exitedEventFired = true;
            }
            Exited?.Invoke(this, new ExitedEventArgs(new ExitedNotification(ExitCode: 0)));
            return;
        }

        if (_state == DebuggerState.NotStarted)
        {
            _state = DebuggerState.Terminated;
            lock (_lock) { _exitedEventFired = true; }
            Exited?.Invoke(this, new ExitedEventArgs(new ExitedNotification(ExitCode: 0)));
            return;
        }

        // Safety net: if the process has already exited, skip Stop/Terminate
        // which can block on a dead ICorDebug process object.
        var processAlive = IsProcessAlive();

        if (processAlive && _process != null)
        {
            try
            {
                await Task.Run(() =>
                {
                    _process.Stop(3000);
                    _process.Terminate(0);
                }).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch { }
        }

        _state = DebuggerState.Terminated;
        lock (_lock)
        {
            if (_exitedEventFired) return;
            _exitedEventFired = true;
        }
        Exited?.Invoke(this, new ExitedEventArgs(new ExitedNotification(ExitCode: 0)));
    }

    private bool IsProcessAlive()
    {
        if (_process == null) return false;
        try
        {
            using var osProcess = System.Diagnostics.Process.GetProcessById(_process.Id);
            return !osProcess.HasExited;
        }
        catch { return false; }
    }

    public Task ContinueAsync(ContinueRequest request)
    {
        EnsureState(DebuggerState.Stopped);
        ClearStopState();
        _state = DebuggerState.Running;
        _process!.Continue(false);
        return Task.CompletedTask;
    }

    public Task StepInAsync(StepInRequest request)
    {
        EnsureState(DebuggerState.Stopped);
        var thread = GetThread(request.ThreadId);
        var frame = thread.ActiveFrame as CorDebugILFrame ?? throw new InvalidOperationException("No IL frame available for stepping.");
        var stepper = thread.CreateStepper();
        stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
        stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);

        var ranges = GetStepRanges(frame);
        if (ranges.Length > 0)
            stepper.StepRange(true, ranges, ranges.Length);
        else
            stepper.Step(true);

        ClearStopState();
        _state = DebuggerState.Running;
        _process!.Continue(false);
        return Task.CompletedTask;
    }

    public Task StepOverAsync(StepOverRequest request)
    {
        EnsureState(DebuggerState.Stopped);
        var thread = GetThread(request.ThreadId);
        var frame = thread.ActiveFrame as CorDebugILFrame ?? throw new InvalidOperationException("No IL frame available for stepping.");
        var stepper = thread.CreateStepper();
        stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
        stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);

        var ranges = GetStepRanges(frame);
        if (ranges.Length > 0)
            stepper.StepRange(false, ranges, ranges.Length);
        else
            stepper.Step(false);

        ClearStopState();
        _state = DebuggerState.Running;
        _process!.Continue(false);
        return Task.CompletedTask;
    }

    public Task StepOutAsync(StepOutRequest request)
    {
        EnsureState(DebuggerState.Stopped);
        var thread = GetThread(request.ThreadId);

        var stepper = thread.CreateStepper();
        stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
        stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
        stepper.StepOut();

        ClearStopState();
        _state = DebuggerState.Running;
        _process!.Continue(false);
        return Task.CompletedTask;
    }

    public Task<SetBreakpointResponse> SetBreakpointAsync(SetBreakpointRequest request)
    {
        EnsureNotTerminatedForBreakpoints();
        return Task.FromResult(_breakpointManager.SetBreakpoint(request.File, request.Line));
    }

    public Task RemoveBreakpointAsync(RemoveBreakpointRequest request)
    {
        EnsureNotTerminatedForBreakpoints();
        _breakpointManager.RemoveBreakpoint(request.BreakpointId);
        return Task.CompletedTask;
    }

    public Task<GetBreakpointsResponse> GetBreakpointsAsync()
    {
        EnsureNotTerminatedForBreakpoints();
        return Task.FromResult(_breakpointManager.GetBreakpoints());
    }

    public Task<GetStackTraceResponse> GetStackTraceAsync(GetStackTraceRequest request)
    {
        EnsureState(DebuggerState.Stopped);
        var thread = GetThread(request.ThreadId);
        var frames = new List<Protocol.StackFrame>();

        _frameMap.Clear();
        _nextFrameId = 0;

        foreach (var chain in thread.EnumerateChains())
        {
            if (!chain.IsManaged) continue;

            foreach (var frame in chain.EnumerateFrames())
            {
                if (frame is not CorDebugILFrame ilFrame) continue;

                var function = ilFrame.Function;
                var module = function.Module;

                string? file = null;
                int? line = null;
                int? column = null;
                int ilOffset = 0;

                try
                {
                    var ipResult = ilFrame.IP;
                    ilOffset = (int)ipResult.pnOffset;

                    var reader = GetSymbolReader(module);
                    if (reader != null)
                    {
                        var sp = reader.ResolveSourceLocation((int)function.Token, ilOffset);
                        if (sp != null)
                        {
                            file = sp.FilePath;
                            line = sp.StartLine;
                            column = sp.StartColumn;
                        }
                    }
                }
                catch { }

                var name = GetMethodName(module, (int)function.Token);
                var frameId = _nextFrameId++;
                _frameMap[frameId] = ilFrame;

                frames.Add(new Protocol.StackFrame(
                    Id: frameId,
                    Name: name,
                    File: file,
                    Line: line,
                    Column: column,
                    ModuleId: module.Name));
            }
        }

        return Task.FromResult(new GetStackTraceResponse(frames.ToArray()));
    }

    public Task<GetVariablesResponse> GetVariablesAsync(GetVariablesRequest request)
    {
        EnsureState(DebuggerState.Stopped);

        var variables = new List<Variable>();
        var valueReader = new Inspection.ValueReader();

        if (request.VariablesReference == 0)
        {
            if (_frameMap.TryGetValue(0, out var topFrame))
                variables.AddRange(GetFrameVariables(topFrame, valueReader));
        }
        else if (_frameMap.TryGetValue(request.VariablesReference - 1000000, out var frame))
        {
            variables.AddRange(GetFrameVariables(frame, valueReader));
        }
        else
        {
            var parentValue = _variableStore.Get(request.VariablesReference);
            if (parentValue != null)
                variables.AddRange(valueReader.ExpandChildren(parentValue, _variableStore));
        }

        return Task.FromResult(new GetVariablesResponse(variables.ToArray()));
    }

    public async Task<EvaluateResponse> EvaluateAsync(EvaluateRequest request)
    {
        EnsureState(DebuggerState.Stopped);

        var frame = (request.FrameId.HasValue && _frameMap.TryGetValue(request.FrameId.Value, out var f)
            ? f
            : _frameMap.GetValueOrDefault(0)) ?? throw new LocalRpcException("No frame available for evaluation")
            { ErrorCode = ErrorCodes.EvaluationFailed };
        var module = frame.Function.Module;
        var reader = GetSymbolReader(module);

        // Try SimpleEvaluator first (fast path: variable names and field access)
        try
        {
            var evaluator = new Inspection.SimpleEvaluator();
            return evaluator.Evaluate(request.Expression, frame, reader, _variableStore);
        }
        catch (LocalRpcException) when (_stoppedThread != null)
        {
            // SimpleEvaluator failed — try FuncEvalEvaluator for property/method/arithmetic
        }

        // Parse expression AST and evaluate with func-eval
        try
        {
            var ast = Inspection.ExpressionParser.Parse(request.Expression);
            var thread = _stoppedThread ?? throw new LocalRpcException("No thread available for evaluation")
            { ErrorCode = ErrorCodes.EvaluationFailed };
            var funcEval = new Inspection.FuncEvalEvaluator(this, thread, frame, reader, _variableStore);
            return await funcEval.EvaluateAsync(ast);
        }
        catch (FormatException ex)
        {
            throw new LocalRpcException($"Invalid expression: {ex.Message}")
            { ErrorCode = ErrorCodes.EvaluationFailed };
        }
    }

    /// <summary>
    /// Executes a func-eval on the debuggee by calling ICorDebugEval methods,
    /// then waits for the EvalComplete/EvalException callback.
    /// </summary>
    public async Task<CorDebugValue> ExecuteEvalAsync(
        Action<CorDebugEval> setup, CorDebugThread thread, TimeSpan? timeout = null)
    {
        var actualTimeout = timeout ?? TimeSpan.FromSeconds(5);

        var eval = thread.CreateEval();
        _evalTcs = new TaskCompletionSource<(CorDebugEval, bool)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        setup(eval);
        _process!.Continue(false);

        using var cts = new CancellationTokenSource(actualTimeout);
        await using var registration = cts.Token.Register(() =>
        {
            try { eval.Abort(); } catch { }
            _evalTcs?.TrySetException(
                new TimeoutException($"Func-eval timed out after {actualTimeout.TotalSeconds}s"));
        });

        var (resultEval, success) = await _evalTcs.Task;
        _evalTcs = null;

        // Func-eval resumes and re-stops the process, neutering old ICorDebug objects.
        // Refresh the frame map so subsequent evaluations use valid frames.
        RefreshFrameMap();

        if (!success)
        {
            // Resume process after failed eval
            _process!.Continue(false);
            throw new LocalRpcException("Func-eval threw an exception in the debuggee")
            { ErrorCode = ErrorCodes.EvaluationFailed };
        }

        return resultEval.Result;
    }

    private void WireCallbackEvents()
    {
        _callbackHandler!.OnStopped += (thread, reason, description) =>
        {
            lock (_lock)
            {
                _stoppedThread = thread;
                _state = DebuggerState.Stopped;
                _variableStore.Clear();
                _frameMap.Clear();
                _nextFrameId = 0;
            }

            var threadId = 0;
            try { threadId = thread?.Id ?? 0; }
            catch { }

            Stopped?.Invoke(this, new StoppedEventArgs(
                new StoppedNotification(reason, threadId, description)));
        };

        _callbackHandler.OnProcessExited += (exitCode) =>
        {
            lock (_lock)
            {
                _state = DebuggerState.Terminated;
                if (_exitedEventFired) return;
                _exitedEventFired = true;
            }
            Exited?.Invoke(this, new ExitedEventArgs(new ExitedNotification(exitCode)));
        };

        _callbackHandler.OnModuleLoaded += (module) =>
        {
            var moduleName = module.Name;
            if (string.IsNullOrEmpty(moduleName)) return;

            var reader = Symbols.SymbolReaderFactory.Create(moduleName);
            _modules[moduleName] = (module, reader);
            _breakpointManager?.OnModuleLoaded(moduleName, module, reader);

            // Handle stopAtEntry: set entry breakpoint when the target module loads
            if (_stopAtEntry && _callbackHandler!.EntryBreakpoint == null && _programPath != null)
            {
                try
                {
                    var normalizedModule = Path.GetFullPath(moduleName);
                    if (string.Equals(normalizedModule, _programPath, StringComparison.OrdinalIgnoreCase))
                    {
                        SetEntryPointBreakpoint(module);
                    }
                }
                catch { }
            }
        };

        _callbackHandler.OnEvalCompleted += (thread, eval, success) =>
        {
            // After eval completes, we're back in stopped state
            lock (_lock) { _state = DebuggerState.Stopped; }
            _evalTcs?.TrySetResult((eval, success));
        };
    }

    private CorDebugThread GetThread(int? threadId)
    {
        if (threadId.HasValue && _process != null)
        {
            try { return _process.GetThread(threadId.Value); }
            catch { }
        }
        return _stoppedThread ?? throw new InvalidOperationException("No thread available.");
    }

    private COR_DEBUG_STEP_RANGE[] GetStepRanges(CorDebugILFrame frame)
    {
        try
        {
            var ipResult = frame.IP;
            var ilOffset = (int)ipResult.pnOffset;

            var function = frame.Function;
            var module = function.Module;
            var reader = GetSymbolReader(module);
            if (reader == null) return [];

            var sequencePoints = reader.GetSequencePoints((int)function.Token);
            if (sequencePoints.Count == 0) return [];

            for (int i = 0; i < sequencePoints.Count; i++)
            {
                var sp = sequencePoints[i];
                var nextOffset = i + 1 < sequencePoints.Count
                    ? sequencePoints[i + 1].ILOffset
                    : sp.ILOffset + 1;

                if (ilOffset >= sp.ILOffset && ilOffset < nextOffset)
                {
                    return [new COR_DEBUG_STEP_RANGE
                    {
                        startOffset = sp.ILOffset,
                        endOffset = nextOffset
                    }];
                }
            }
        }
        catch { }

        return [];
    }

    private Symbols.ISymbolReader? GetSymbolReader(CorDebugModule module)
    {
        var name = module.Name;
        if (string.IsNullOrEmpty(name)) return null;
        return _modules.TryGetValue(name, out var entry) ? entry.Reader : null;
    }

    private string GetMethodName(CorDebugModule module, int methodToken)
    {
        try
        {
            var import = module.GetMetaDataInterface<MetaDataImport>();
            var methodProps = import.GetMethodProps((mdMethodDef)methodToken);
            var typeName = "";
            try
            {
                var typeProps = import.GetTypeDefProps(methodProps.pClass);
                typeName = typeProps.szTypeDef + ".";
            }
            catch { }
            return typeName + methodProps.szMethod;
        }
        catch
        {
            return $"<unknown>@0x{methodToken:X}";
        }
    }

    private List<Variable> GetFrameVariables(CorDebugILFrame frame, Inspection.ValueReader valueReader)
    {
        var variables = new List<Variable>();
        var module = frame.Function.Module;
        var reader = GetSymbolReader(module);

        int ilOffset;
        try
        {
            var ipResult = frame.IP;
            ilOffset = (int)ipResult.pnOffset;
        }
        catch { return variables; }

        IReadOnlyList<Symbols.LocalVariableInfo>? localInfos = null;
        if (reader != null)
            localInfos = reader.GetLocalVariables((int)frame.Function.Token, ilOffset);

        if (localInfos != null)
        {
            foreach (var localInfo in localInfos)
            {
                try
                {
                    var value = frame.GetLocalVariable(localInfo.SlotIndex);
                    var (displayValue, type, varRef) = valueReader.Read(value, _variableStore);
                    variables.Add(new Variable(localInfo.Name, displayValue, varRef, type));
                }
                catch { }
            }
        }

        try
        {
            var import = module.GetMetaDataInterface<MetaDataImport>();
            var methodToken = (mdMethodDef)frame.Function.Token;
            var paramNames = GetParameterNames(import, methodToken);

            for (int i = 0; ; i++)
            {
                try
                {
                    var value = frame.GetArgument(i);
                    var name = i < paramNames.Count ? paramNames[i] : $"arg{i}";
                    var (displayValue, type, varRef) = valueReader.Read(value, _variableStore);
                    variables.Add(new Variable(name, displayValue, varRef, type));
                }
                catch { break; }
            }
        }
        catch { }

        return variables;
    }

    private static List<string> GetParameterNames(MetaDataImport import, mdMethodDef methodToken)
    {
        var names = new List<string>();
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
                    names.Add(props.szName);
                }
                count = import.EnumParams(ref enumHandle, methodToken, paramTokens);
            }
            if (enumHandle != IntPtr.Zero)
                import.CloseEnum(enumHandle);
        }
        catch
        {
            try { if (enumHandle != IntPtr.Zero) import.CloseEnum(enumHandle); } catch { }
        }
        return names;
    }

    private void EnsureState(DebuggerState expectedState)
    {
        if (_state != expectedState)
        {
            var errorCode = _state switch
            {
                DebuggerState.NotStarted => ErrorCodes.NotAttached,
                DebuggerState.Running => expectedState == DebuggerState.Stopped
                    ? ErrorCodes.ProcessNotStopped : ErrorCodes.ProcessRunning,
                DebuggerState.Stopped => ErrorCodes.ProcessRunning,
                DebuggerState.Terminated => ErrorCodes.NotAttached,
                _ => ErrorCodes.NotAttached
            };
            throw new LocalRpcException($"Invalid state: expected {expectedState}, got {_state}")
            { ErrorCode = errorCode };
        }
    }

    private void EnsureNotTerminated()
    {
        if (_state == DebuggerState.Terminated)
            throw new LocalRpcException("Process has terminated") { ErrorCode = ErrorCodes.NotAttached };
        if (_state == DebuggerState.NotStarted)
            throw new LocalRpcException("No process attached") { ErrorCode = ErrorCodes.NotAttached };
    }

    private void EnsureNotTerminatedForBreakpoints()
    {
        if (_state == DebuggerState.Terminated)
            throw new LocalRpcException("Process has terminated") { ErrorCode = ErrorCodes.NotAttached };
    }

    private void ClearStopState()
    {
        _variableStore.Clear();
        _frameMap.Clear();
        _nextFrameId = 0;
    }

    private void RefreshFrameMap()
    {
        _frameMap.Clear();
        _nextFrameId = 0;
        _variableStore.Clear();

        if (_stoppedThread == null) return;

        try
        {
            foreach (var chain in _stoppedThread.EnumerateChains())
            {
                if (!chain.IsManaged) continue;
                foreach (var frame in chain.EnumerateFrames())
                {
                    if (frame is not CorDebugILFrame ilFrame) continue;
                    _frameMap[_nextFrameId++] = ilFrame;
                }
            }
        }
        catch { }
    }

    private void SetEntryPointBreakpoint(CorDebugModule module)
    {
        try
        {
            var modulePath = module.Name;

            // Read the entry point token from the PE header
            using var fs = File.OpenRead(modulePath);
            using var peReader = new PEReader(fs);
            var corHeader = peReader.PEHeaders.CorHeader;
            if (corHeader == null) return;

            var entryPointToken = corHeader.EntryPointTokenOrRelativeVirtualAddress;
            if (entryPointToken == 0) return;

            // Find the first non-hidden sequence point so the breakpoint has source info
            int ilOffset = 0;
            var reader = GetSymbolReader(module);
            if (reader != null)
            {
                var seqPoints = reader.GetSequencePoints(entryPointToken);
                if (seqPoints.Count > 0)
                    ilOffset = seqPoints[0].ILOffset;
            }

            var function = module.GetFunctionFromToken((mdMethodDef)entryPointToken);
            var code = function.ILCode;
            var bp = code.CreateBreakpoint(ilOffset);
            bp.Activate(true);

            _callbackHandler!.EntryBreakpoint = bp;
        }
        catch { }
    }

    private void StartOutputReader(Stream stream, OutputCategory category)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = new StreamReader(stream);
                while (true)
                {
                    var line = await reader.ReadLineAsync();
                    if (line == null) break; // stream closed
                    Output?.Invoke(this, new OutputEventArgs(
                        new OutputNotification(category, line)));
                }
            }
            catch { }
        });
    }

    public void Dispose()
    {
        try
        {
            if (_state != DebuggerState.Terminated && _state != DebuggerState.NotStarted)
            {
                _process?.Stop(1000);
                _process?.Terminate(0);
            }
        }
        catch { }

        foreach (var (_, (_, reader)) in _modules)
            reader?.Dispose();
        _modules.Clear();

        try { _corDebug?.Terminate(); }
        catch { }
    }
}
