namespace DnD.Core;

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ClrDebug;
using DnD.Core.Callbacks;
using DnD.Core.Inspection;
using DnD.Core.Runtime;
using DnD.Protocol;
using StreamJsonRpc;

public class DebuggerEngine : IDebuggerEngine, ISessionContext, IEvalExecutor, IDisposable
{
    private IDebuggerState _currentState = NotStartedState.Instance;
    private DebugSession? _session;
    private readonly IProcessLauncher _launcher;
    private readonly object _lock = new();

    // Module-load buffering for race between Launch() and _session assignment.
    // Stores (Module, Name, Reader) so that pending breakpoints can be applied
    // immediately during buffering, before ContinueProcess() resumes the debuggee.
    private readonly object _moduleBufferLock = new();
    private List<(CorDebugModule Module, string Name, Symbols.ISymbolReader? Reader)>? _moduleBuffer;

    // Session-crossing state
    private readonly BreakpointManager _breakpointManager = new();

    // Exception breakpoint settings (stored here so they survive pre-launch configuration)
    private bool _exceptionStopOnThrown;
    private bool _exceptionStopOnUncaught = true;
    private string[]? _exceptionTypeFilter;

    public event EventHandler<StoppedEventArgs>? Stopped;
    public event EventHandler<ExitedEventArgs>? Exited;
    public event EventHandler<OutputEventArgs>? Output;

    public DebuggerEngine(IProcessLauncher launcher)
    {
        _launcher = launcher;
    }

    // ── ISessionContext implementation ─────────────────────────────────

    BreakpointManager ISessionContext.BreakpointManager => _breakpointManager;

    DebugSession ISessionContext.CreateLaunchSession(LaunchRequest request)
    {
        var handler = new ManagedCallbackHandler();
        ApplyExceptionBreakpointSettings(handler);

        lock (_moduleBufferLock) { _moduleBuffer = new(); }

        WireCallbackEvents(handler);

        var result = _launcher.Launch(
            request.Program, request.Args, request.Cwd, request.Env,
            handler.Callback);

        var session = new DebugSession(result.CorDebug, result.Process, handler)
        {
            StopAtEntry = request.StopAtEntry,
            ProgramPath = Path.GetFullPath(request.Program)
        };
        Volatile.Write(ref _session, session);

        // Replay buffered module loads, then switch to direct mode.
        // Breakpoints were already applied during buffering; replay only
        // registers modules with the session (symbols, optimization warnings, entry BP).
        List<(CorDebugModule Module, string Name, Symbols.ISymbolReader? Reader)> buffered;
        lock (_moduleBufferLock)
        {
            buffered = _moduleBuffer ?? new();
            _moduleBuffer = null;
        }
        foreach (var (module, name, reader) in buffered)
            RegisterModule(session, module, name, reader, handler);

        if (result.StandardOutput != null)
            StartOutputReader(result.StandardOutput, OutputCategory.Stdout);
        if (result.StandardError != null)
            StartOutputReader(result.StandardError, OutputCategory.Stderr);

        return session;
    }

    DebugSession ISessionContext.CreateAttachSession(AttachRequest request)
    {
        var handler = new ManagedCallbackHandler();
        ApplyExceptionBreakpointSettings(handler);

        lock (_moduleBufferLock) { _moduleBuffer = new(); }

        WireCallbackEvents(handler);

        var result = _launcher.Attach(request.ProcessId, handler.Callback);

        var session = new DebugSession(result.CorDebug, result.Process, handler);
        Volatile.Write(ref _session, session);

        // Replay buffered module loads (breakpoints already applied during buffering)
        List<(CorDebugModule Module, string Name, Symbols.ISymbolReader? Reader)> buffered;
        lock (_moduleBufferLock)
        {
            buffered = _moduleBuffer ?? new();
            _moduleBuffer = null;
        }
        foreach (var (module, name, reader) in buffered)
            RegisterModule(session, module, name, reader, handler);

        return session;
    }

    DebugSession ISessionContext.GetSession()
        => _session ?? throw new InvalidOperationException("No active debug session");

    void ISessionContext.EndSession()
    {
        var session = Interlocked.Exchange(ref _session, null);
        if (session != null)
        {
            _breakpointManager.RevertAllToPending();
            session.Dispose();
        }
    }

    async Task ISessionContext.TerminateProcessAsync()
    {
        var session = _session;
        if (session != null && session.IsProcessAlive())
        {
            try
            {
                var process = session.Process;
                await Task.Run(() =>
                {
                    process.Stop(3000);
                    process.Terminate(0);
                }).WaitAsync(TimeSpan.FromSeconds(5));

                // Wait for the debuggee process to actually exit.
                // ICorDebug contract: do not call ICorDebug::Terminate
                // before all debugged processes have exited.
                try
                {
                    using var osProcess = Process.GetProcessById(process.Id);
                    await osProcess.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (ArgumentException) { } // already exited
            }
            catch { }
        }
    }

    void ISessionContext.RaiseStoppedEvent(StoppedNotification notification)
    {
        Stopped?.Invoke(this, new StoppedEventArgs(notification));
    }

    bool ISessionContext.RaiseExitedEvent(int exitCode)
    {
        lock (_lock)
        {
            var session = _session;
            // Use session's flag if session exists, otherwise use a local guard
            if (session != null)
            {
                if (session.ExitedEventFired) return false;
                session.ExitedEventFired = true;
            }
        }
        Exited?.Invoke(this, new ExitedEventArgs(new ExitedNotification(ExitCode: exitCode)));
        return true;
    }

    // ── DbC: Design by Contract assertions ────────────────────────────

    [Conditional("DEBUG")]
    private void AssertStateSessionConsistency()
    {
        var id = _currentState.Id;
        switch (id)
        {
            case DebuggerState.NotStarted:
            case DebuggerState.Terminated:
                Debug.Assert(_session == null,
                    $"State is {id} but session exists");
                break;
            case DebuggerState.Running:
            case DebuggerState.Stopped:
            case DebuggerState.Evaluating:
                Debug.Assert(_session != null,
                    $"State is {id} but session is null");
                break;
        }
    }

    [Conditional("DEBUG")]
    private void AssertPrecondition(DebuggerState expected)
    {
        Debug.Assert(_currentState.Id == expected,
            $"Precondition failed: expected {expected}, got {_currentState.Id}");
        AssertStateSessionConsistency();
    }

    [Conditional("DEBUG")]
    private void AssertPostcondition(DebuggerState expected)
    {
        Debug.Assert(_currentState.Id == expected,
            $"Postcondition failed: expected {expected}, got {_currentState.Id}");
        AssertStateSessionConsistency();
    }

    // ── Process control ────────────────────────────────────────────────

    public Task<LaunchResponse> LaunchAsync(LaunchRequest request)
    {
        AssertPrecondition(DebuggerState.NotStarted);
        _currentState = _currentState.Launch(this, request);
        AssertPostcondition(DebuggerState.Running);
        var session = _session ?? throw new InvalidOperationException("Session not created");
        return Task.FromResult(new LaunchResponse(ProcessId: session.Process.Id));
    }

    public Task<AttachResponse> AttachAsync(AttachRequest request)
    {
        AssertPrecondition(DebuggerState.NotStarted);
        _currentState = _currentState.Attach(this, request);
        AssertPostcondition(DebuggerState.Running);
        var session = _session ?? throw new InvalidOperationException("Session not created");
        return Task.FromResult(new AttachResponse(ProcessId: session.Process.Id));
    }

    public Task DetachAsync()
    {
        AssertStateSessionConsistency();
        _currentState = _currentState.Detach(this);
        AssertStateSessionConsistency();
        return Task.CompletedTask;
    }

    public async Task TerminateAsync()
    {
        AssertStateSessionConsistency();
        _currentState = await _currentState.TerminateAsync(this);
        AssertStateSessionConsistency();
    }

    // ── Execution control ──────────────────────────────────────────────

    public Task PauseAsync()
    {
        _currentState = _currentState.Pause(this);
        return Task.CompletedTask;
    }

    public Task ContinueAsync(ContinueRequest request)
    {
        _currentState = _currentState.Resume(this);
        return Task.CompletedTask;
    }

    public Task StepInAsync(StepInRequest request)
    {
        _currentState.EnsureStopped();
        var session = RequireSession();
        var thread = GetThread(request.ThreadId, session);
        var frame = thread.ActiveFrame as CorDebugILFrame ?? throw new InvalidOperationException("No IL frame available for stepping.");
        var stepper = thread.CreateStepper();
        stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
        stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);

        var ranges = GetStepRanges(frame, session);
        if (ranges.Length > 0)
            stepper.StepRange(true, ranges, ranges.Length);
        else
            stepper.Step(true);

        _currentState = _currentState.Resume(this);
        return Task.CompletedTask;
    }

    public Task StepOverAsync(StepOverRequest request)
    {
        _currentState.EnsureStopped();
        var session = RequireSession();
        var thread = GetThread(request.ThreadId, session);
        var frame = thread.ActiveFrame as CorDebugILFrame ?? throw new InvalidOperationException("No IL frame available for stepping.");
        var stepper = thread.CreateStepper();
        stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
        stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);

        var ranges = GetStepRanges(frame, session);
        if (ranges.Length > 0)
            stepper.StepRange(false, ranges, ranges.Length);
        else
            stepper.Step(false);

        _currentState = _currentState.Resume(this);
        return Task.CompletedTask;
    }

    public Task StepOutAsync(StepOutRequest request)
    {
        _currentState.EnsureStopped();
        var session = RequireSession();
        var thread = GetThread(request.ThreadId, session);

        // Before stepping out, set hidden native breakpoints at the caller's
        // return-value live offsets so GetReturnValueForILOffset will work.
        SetupReturnValueBreakpoints(thread, session);

        var stepper = thread.CreateStepper();
        stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
        stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
        stepper.StepOut();

        session.PendingStepOut = true;
        _currentState = _currentState.Resume(this);
        return Task.CompletedTask;
    }

    // ── Breakpoints ────────────────────────────────────────────────────

    public Task<SetBreakpointResponse> SetBreakpointAsync(SetBreakpointRequest request)
    {
        _currentState.EnsureNotTerminatedForBreakpoints();
        var modules = _session?.Modules ?? new Dictionary<string, (CorDebugModule, Symbols.ISymbolReader?)>();
        return Task.FromResult(_breakpointManager.SetBreakpoint(
            request.File, request.Line, modules, request.Condition, request.HitCount));
    }

    public Task RemoveBreakpointAsync(RemoveBreakpointRequest request)
    {
        _currentState.EnsureNotTerminatedForBreakpoints();
        _breakpointManager.RemoveBreakpoint(request.BreakpointId);
        return Task.CompletedTask;
    }

    public Task<GetBreakpointsResponse> GetBreakpointsAsync()
    {
        _currentState.EnsureNotTerminatedForBreakpoints();
        return Task.FromResult(_breakpointManager.GetBreakpoints());
    }

    public Task<SetExceptionBreakpointsResponse> SetExceptionBreakpointsAsync(
        SetExceptionBreakpointsRequest request)
    {
        // Store on engine so settings survive pre-launch configuration
        _exceptionStopOnThrown = request.Thrown;
        _exceptionStopOnUncaught = request.Uncaught;
        _exceptionTypeFilter = request.Types;

        // Apply immediately if session already exists
        if (_session != null)
        {
            _session.CallbackHandler.ExceptionStopOnThrown = _exceptionStopOnThrown;
            _session.CallbackHandler.ExceptionStopOnUncaught = _exceptionStopOnUncaught;
            _session.CallbackHandler.ExceptionTypeFilter = _exceptionTypeFilter;
        }
        return Task.FromResult(new SetExceptionBreakpointsResponse(
            request.Thrown, request.Uncaught, request.Types));
    }

    // ── Inspection ─────────────────────────────────────────────────────

    public Task<GetStackTraceResponse> GetStackTraceAsync(GetStackTraceRequest request)
    {
        _currentState.EnsureStopped();
        var session = RequireSession();
        var thread = GetThread(request.ThreadId, session);
        var frames = new List<Protocol.StackFrame>();

        session.FrameMap.Clear();
        session.NextFrameId = 0;

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

                    var reader = GetSymbolReader(module, session);
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
                var frameId = session.NextFrameId++;
                session.FrameMap[frameId] = ilFrame;

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
        _currentState.EnsureStopped();
        var session = RequireSession();

        var variables = new List<Variable>();
        var valueReader = new ValueReader();
        var frameId = request.FrameId ?? 0;

        var frame = session.FrameMap.GetValueOrDefault(frameId)
            ?? throw new LocalRpcException("No frame available")
            { ErrorCode = ErrorCodes.EvaluationFailed };

        variables.AddRange(GetFrameVariables(frame, valueReader, session));

        // Add $exception pseudo-variable if there's a current exception
        var exVal = GetCurrentExceptionValue(session);
        if (exVal != null)
        {
            var (displayValue, type) = valueReader.Read(exVal);
            variables.Add(new Variable("$exception", displayValue, type));
        }

        // Add $returnValue pseudo-variable if we just stepped out
        if (session.LastReturnValue != null)
        {
            var (displayValue, type) = valueReader.Read(session.LastReturnValue);
            variables.Add(new Variable("$returnValue", displayValue, type));
        }

        return Task.FromResult(new GetVariablesResponse(variables.ToArray()));
    }

    public async Task<EvaluateResponse> EvaluateAsync(EvaluateRequest request)
    {
        _currentState.EnsureStopped();
        var session = RequireSession();

        var frameId = request.FrameId ?? 0;
        CorDebugILFrame GetCurrentFrame() =>
            (session.FrameMap.TryGetValue(frameId, out var current) ? current : null)
            ?? throw new LocalRpcException("No frame available for evaluation")
            { ErrorCode = ErrorCodes.EvaluationFailed };

        var frame = GetCurrentFrame();
        var module = frame.Function.Module;
        var reader = GetSymbolReader(module, session);
        var exVal = GetCurrentExceptionValue(session);

        // Try SimpleEvaluator first (fast path: variable names and field access)
        try
        {
            var evaluator = new SimpleEvaluator();
            return evaluator.Evaluate(request.Expression, frame, reader, exVal, session.LastReturnValue);
        }
        catch (LocalRpcException) when (session.StoppedThread != null)
        {
            // SimpleEvaluator failed — try RoslynEvaluator for complex expressions
        }

        // Roslyn-based evaluation for complex expressions (new, LINQ, lambda, etc.)
        try
        {
            var thread = session.StoppedThread ?? throw new LocalRpcException("No thread available for evaluation")
            { ErrorCode = ErrorCodes.EvaluationFailed };
            var roslynEval = new RoslynEvaluator(this, thread, session);
            return await roslynEval.EvaluateAsync(
                request.Expression, frame, reader, exVal, session.LastReturnValue);
        }
        catch (LocalRpcException) { throw; }
        catch (Exception ex)
        {
            throw new LocalRpcException($"Evaluation failed: {ex.Message}")
            { ErrorCode = ErrorCodes.EvaluationFailed };
        }
    }

    public Task<GetThreadsResponse> GetThreadsAsync()
    {
        _currentState.EnsureStopped();
        var session = RequireSession();
        var threads = new List<ThreadInfo>();
        try
        {
            var stoppedId = session.GetStoppedThreadId();

            foreach (var thread in session.Process.EnumerateThreads())
            {
                try
                {
                    var id = thread.Id;
                    threads.Add(new ThreadInfo(Id: id, Current: id == stoppedId));
                }
                catch { }
            }
        }
        catch { }
        return Task.FromResult(new GetThreadsResponse(threads.ToArray()));
    }

    public Task<GetExceptionResponse> GetExceptionAsync(GetExceptionRequest request)
    {
        _currentState.EnsureStopped();
        var session = RequireSession();
        var thread = GetThread(request.ThreadId, session);

        try
        {
            var exValue = thread.CurrentException ?? throw new LocalRpcException("No current exception") { ErrorCode = ErrorCodes.EvaluationFailed };
            CorDebugValue value = exValue;
            if (value is CorDebugReferenceValue refVal)
            {
                if (refVal.IsNull)
                    throw new LocalRpcException("No current exception") { ErrorCode = ErrorCodes.EvaluationFailed };
                value = refVal.Dereference();
            }
            if (value is not CorDebugObjectValue objVal)
                throw new LocalRpcException("Exception is not an object") { ErrorCode = ErrorCodes.EvaluationFailed };

            var cls = objVal.Class;
            var module = cls.Module;
            var import = module.GetMetaDataInterface<MetaDataImport>();
            var typeName = import.GetTypeDefProps(cls.Token).szTypeDef;

            // Resolve System.Exception fields
            var exTypeDef = import.FindTypeDefByName("System.Exception", 0);
            var exClass = module.GetClassFromToken(exTypeDef);
            var fields = EnumFieldsByName(import, exTypeDef, "_message", "_stackTraceString", "_innerException");

            string? message = ReadStringField(objVal, exClass, import, fields, "_message");
            string? stackTrace = ReadStringField(objVal, exClass, import, fields, "_stackTraceString");
            ExceptionInfo? inner = ReadInnerException(objVal, exClass, import, fields);

            return Task.FromResult(new GetExceptionResponse(typeName, message, stackTrace, inner));
        }
        catch (LocalRpcException) { throw; }
        catch (Exception ex)
        {
            throw new LocalRpcException($"Failed to read exception: {ex.Message}")
            { ErrorCode = ErrorCodes.EvaluationFailed };
        }
    }

    // ── Static helpers for exception field reading ─────────────────────

    private static Dictionary<string, mdFieldDef> EnumFieldsByName(
        MetaDataImport import, mdTypeDef typeDef, params string[] names)
    {
        var result = new Dictionary<string, mdFieldDef>();
        var nameSet = new HashSet<string>(names);
        var enumHandle = IntPtr.Zero;
        var tokens = new mdFieldDef[64];
        try
        {
            var count = import.EnumFields(ref enumHandle, typeDef, tokens);
            for (int i = 0; i < count; i++)
            {
                var props = import.GetFieldProps(tokens[i]);
                if (nameSet.Contains(props.szField))
                    result[props.szField] = tokens[i];
            }
        }
        finally { if (enumHandle != IntPtr.Zero) import.CloseEnum(enumHandle); }
        return result;
    }

    private static string? ReadStringField(
        CorDebugObjectValue objVal, CorDebugClass exClass, MetaDataImport import,
        Dictionary<string, mdFieldDef> fields, string fieldName)
    {
        if (!fields.TryGetValue(fieldName, out var token)) return null;
        try
        {
            var val = objVal.GetFieldValue(exClass.Raw, token);
            if (val is CorDebugReferenceValue r)
            {
                if (r.IsNull) return null;
                val = r.Dereference();
            }
            return val is CorDebugStringValue s ? s.GetString(s.Length) : null;
        }
        catch { return null; }
    }

    private static ExceptionInfo? ReadInnerException(
        CorDebugObjectValue objVal, CorDebugClass exClass, MetaDataImport import,
        Dictionary<string, mdFieldDef> fields)
    {
        if (!fields.TryGetValue("_innerException", out var token)) return null;
        try
        {
            var val = objVal.GetFieldValue(exClass.Raw, token);
            if (val is CorDebugReferenceValue r)
            {
                if (r.IsNull) return null;
                val = r.Dereference();
            }
            if (val is not CorDebugObjectValue innerObj) return null;

            var innerType = import.GetTypeDefProps(innerObj.Class.Token).szTypeDef;
            // Re-read _message from inner exception using same System.Exception class
            string? innerMsg = ReadStringField(innerObj, exClass, import, fields, "_message");
            return new ExceptionInfo(innerType, innerMsg);
        }
        catch { return null; }
    }

    // ── IEvalExecutor / Func-eval ─────────────────────────────────────

    /// <summary>
    /// Executes a func-eval on the debuggee by calling ICorDebugEval methods,
    /// then waits for the EvalComplete/EvalException callback.
    /// Uses escalating abort strategy on timeout: Abort → Process.Stop+Abort → give up.
    /// </summary>
    public async Task<CorDebugValue> ExecuteEvalAsync(
        Action<CorDebugEval> setup, CorDebugThread thread, TimeSpan? timeout = null)
    {
        var actualTimeout = timeout ?? TimeSpan.FromSeconds(5);
        var session = RequireSession();
        var process = session.Process;

        var eval = thread.CreateEval();
        session.EvalTcs = new TaskCompletionSource<(CorDebugEval, bool)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            setup(eval);
        }
        catch
        {
            session.EvalTcs = null;
            throw;
        }

        // Transition: Stopped → Evaluating
        lock (_lock) { _currentState = _currentState.StartEval(this); }
        process.Continue(false);

        try
        {
            var evalTask = session.EvalTcs.Task;

            // Phase 1: Wait for normal completion
            if (await Task.WhenAny(evalTask, Task.Delay(actualTimeout)) == evalTask)
                return HandleEvalResult(await evalTask, session);

            // Phase 2: Soft abort via ICorDebugEval.Abort()
            try { eval.Abort(); } catch { }
            if (await Task.WhenAny(evalTask, Task.Delay(TimeSpan.FromSeconds(1))) == evalTask)
            {
                // Abort triggered callback — discard result, throw timeout
                _ = await evalTask;
                RefreshFrameMap(session);
                throw new LocalRpcException(
                    $"Func-eval timed out after {actualTimeout.TotalSeconds}s")
                { ErrorCode = ErrorCodes.EvaluationFailed };
            }

            // Phase 3: Hard stop via ICorDebugProcess.Stop() + re-abort
            try { process.Stop(0); } catch { }
            try { eval.Abort(); } catch { }
            if (await Task.WhenAny(evalTask, Task.Delay(TimeSpan.FromSeconds(1))) == evalTask)
            {
                _ = await evalTask;
                RefreshFrameMap(session);
                throw new LocalRpcException(
                    $"Func-eval timed out after {actualTimeout.TotalSeconds}s (hard stop)")
                { ErrorCode = ErrorCodes.EvaluationFailed };
            }

            // Phase 4: Give up — force-fail the TCS so we don't hang
            session.EvalTcs.TrySetException(new TimeoutException(
                $"Func-eval could not be aborted after {actualTimeout.TotalSeconds}s"));
            // Recover: Evaluating → Stopped (no callback will fire)
            lock (_lock) { _currentState = _currentState.OnEvalComplete(this); }
            throw new LocalRpcException(
                $"Func-eval timed out and could not be aborted")
            { ErrorCode = ErrorCodes.EvaluationFailed };
        }
        finally
        {
            session.EvalTcs = null;
        }
    }

    private CorDebugValue HandleEvalResult((CorDebugEval Eval, bool Success) result, DebugSession session)
    {
        RefreshFrameMap(session);

        if (!result.Success)
        {
            // Try to include the exception type in the error message
            var errorMessage = "Func-eval threw an exception in the debuggee";
            try
            {
                var exValue = result.Eval.Result;
                if (exValue != null)
                {
                    var typeName = Inspection.TypeNameResolver.GetCSharpTypeName(exValue);
                    errorMessage = $"Func-eval threw {typeName} in the debuggee";
                }
            }
            catch { }

            throw new LocalRpcException(errorMessage)
            { ErrorCode = ErrorCodes.EvaluationFailed };
        }

        return result.Eval.Result;
    }

    // ── Callback wiring ────────────────────────────────────────────────

    private static void ApplyExceptionBreakpointSettings(ManagedCallbackHandler handler,
        bool stopOnThrown, bool stopOnUncaught, string[]? typeFilter)
    {
        handler.ExceptionStopOnThrown = stopOnThrown;
        handler.ExceptionStopOnUncaught = stopOnUncaught;
        handler.ExceptionTypeFilter = typeFilter;
    }

    private void ApplyExceptionBreakpointSettings(ManagedCallbackHandler handler)
    {
        ApplyExceptionBreakpointSettings(handler,
            _exceptionStopOnThrown, _exceptionStopOnUncaught, _exceptionTypeFilter);
    }

    private void WireCallbackEvents(ManagedCallbackHandler handler)
    {
        handler.OnStopped += (thread, reason, description) =>
        {
            var session = Volatile.Read(ref _session);
            if (session == null)
            {
                // Wait for _session assignment (Launch() return → assignment window, typically microseconds)
                var sw = new SpinWait();
                for (int i = 0; i < 200 && session == null; i++)
                {
                    sw.SpinOnce();
                    session = Volatile.Read(ref _session);
                }
                if (session == null) return;
            }

            // Quick hit-count check before full state setup
            if (reason == StopReason.Breakpoint && handler.LastHitBreakpoint != null)
            {
                var (shouldStop, bpId, condition) =
                    _breakpointManager.CheckBreakpointHit(handler.LastHitBreakpoint);
                handler.LastHitBreakpoint = null;

                if (!shouldStop)
                {
                    // Hit count not reached — auto-continue without stopping
                    session.Process.Continue(false);
                    return;
                }

                // Set up stopped state
                lock (_lock)
                {
                    session.SetStopState(thread);
                    _currentState = _currentState.OnBreak(this);
                }

                var threadId = 0;
                try { threadId = thread?.Id ?? 0; } catch { }

                if (condition != null)
                {
                    // Evaluate condition asynchronously
                    _ = EvaluateBreakpointConditionAsync(bpId, threadId, condition);
                    return;
                }

                Stopped?.Invoke(this, new StoppedEventArgs(
                    new StoppedNotification(reason, threadId, description, bpId)));
                return;
            }

            // Suppress duplicate Step callback from double StepComplete+Breakpoint
            if (reason == StopReason.Step && session.SuppressNextStepCallback)
            {
                session.SuppressNextStepCallback = false;
                session.Process.Continue(false);
                return;
            }

            // Non-breakpoint stops (step, pause, exception, entry)
            lock (_lock)
            {
                session.SetStopState(thread);
                _currentState = _currentState.OnBreak(this);
            }

            // Capture return value after StepOut
            if (reason == StopReason.Step && session.PendingStepOut)
            {
                session.PendingStepOut = false;
                TryCaptureReturnValue(thread, session);
                CleanupReturnValueBreakpoints(session);
                // Both StepComplete and Breakpoint callbacks may fire — suppress the second
                session.SuppressNextStepCallback = true;
            }
            else
            {
                session.PendingStepOut = false;
                CleanupReturnValueBreakpoints(session);
            }

            var tid = 0;
            try { tid = thread?.Id ?? 0; } catch { }

            Stopped?.Invoke(this, new StoppedEventArgs(
                new StoppedNotification(reason, tid, description)));
        };

        handler.OnProcessExited += (exitCode) =>
        {
            lock (_lock) { _currentState = _currentState.OnProcessExited(this, exitCode); }
        };

        handler.OnModuleLoaded += (module) =>
        {
            lock (_moduleBufferLock)
            {
                if (_moduleBuffer != null)
                {
                    // Apply pending breakpoints immediately, before the callback
                    // returns and ContinueProcess() resumes the debuggee. This
                    // prevents a race where the process executes past a breakpoint
                    // location before buffer replay can apply it.
                    var moduleName = module.Name;
                    if (!string.IsNullOrEmpty(moduleName))
                    {
                        var reader = Symbols.SymbolReaderFactory.Create(moduleName);
                        _breakpointManager.OnModuleLoaded(moduleName, module, reader);
                        _moduleBuffer.Add((module, moduleName, reader));
                    }
                    return;
                }
            }
            // Buffer disabled = _session is set
            var session = Volatile.Read(ref _session);
            if (session == null) return;
            HandleModuleLoaded(session, module, handler);
        };

        handler.OnEvalCompleted += (thread, eval, success) =>
        {
            var session = _session;
            // Transition: Evaluating → Stopped
            lock (_lock) { _currentState = _currentState.OnEvalComplete(this); }
            session?.EvalTcs?.TrySetResult((eval, success));
        };
    }

    // ── Private helpers ────────────────────────────────────────────────

    private void HandleModuleLoaded(DebugSession session, CorDebugModule module, ManagedCallbackHandler handler)
    {
        var moduleName = module.Name;
        if (string.IsNullOrEmpty(moduleName)) return;

        var reader = Symbols.SymbolReaderFactory.Create(moduleName);
        _breakpointManager.OnModuleLoaded(moduleName, module, reader);
        RegisterModule(session, module, moduleName, reader, handler);
    }

    /// <summary>
    /// Register a module with the session (symbols, optimization warnings, entry BP).
    /// Called both during direct module loads and during buffered replay.
    /// Breakpoint resolution is NOT done here — it must be done separately
    /// (either in HandleModuleLoaded or during buffered callback handling).
    /// </summary>
    private void RegisterModule(DebugSession session, CorDebugModule module,
        string moduleName, Symbols.ISymbolReader? reader, ManagedCallbackHandler handler)
    {
        session.Modules[moduleName] = (module, reader);

        // Warn if the module has symbols but was built with optimizations enabled
        if (reader != null && IsModuleOptimized(moduleName))
        {
            var fileName = Path.GetFileName(moduleName);
            Output?.Invoke(this, new OutputEventArgs(
                new OutputNotification(OutputCategory.Console,
                    $"Warning: Module '{fileName}' is optimized (Release build). " +
                    "Some local variables may be unavailable and expression evaluation may fail.")));
        }

        // Handle stopAtEntry: set entry breakpoint when the target module loads
        if (session.StopAtEntry && handler.EntryBreakpoint == null && session.ProgramPath != null)
        {
            try
            {
                var normalizedModule = Path.GetFullPath(moduleName);
                if (string.Equals(normalizedModule, session.ProgramPath, StringComparison.OrdinalIgnoreCase))
                {
                    SetEntryPointBreakpoint(module, handler);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Checks whether an assembly was compiled with optimizations enabled by reading
    /// the DebuggableAttribute from PE metadata.
    /// Returns true if the module is optimized (DisableOptimizations flag is NOT set).
    /// </summary>
    internal static bool IsModuleOptimized(string modulePath)
    {
        try
        {
            using var stream = File.OpenRead(modulePath);
            using var peReader = new PEReader(stream);
            if (!peReader.HasMetadata) return false;

            var metadata = peReader.GetMetadataReader();
            var assembly = metadata.GetAssemblyDefinition();

            foreach (var attrHandle in assembly.GetCustomAttributes())
            {
                var attr = metadata.GetCustomAttribute(attrHandle);
                if (attr.Constructor.Kind != HandleKind.MemberReference) continue;

                var memberRef = metadata.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (memberRef.Parent.Kind != HandleKind.TypeReference) continue;

                var typeRef = metadata.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                if (metadata.GetString(typeRef.Name) != "DebuggableAttribute" ||
                    metadata.GetString(typeRef.Namespace) != "System.Diagnostics")
                    continue;

                // Parse custom attribute blob: prolog (2 bytes) + DebuggingModes enum (4 bytes)
                var blob = metadata.GetBlobReader(attr.Value);
                if (blob.Length < 6) return true;
                blob.ReadUInt16(); // prolog 0x0001
                var modes = blob.ReadInt32();

                // DebuggingModes.DisableOptimizations = 0x100
                return (modes & 0x100) == 0;
            }

            // No DebuggableAttribute → treat as optimized
            return true;
        }
        catch
        {
            // Can't read metadata → don't warn
            return false;
        }
    }

    private DebugSession RequireSession()
        => _session ?? throw new InvalidOperationException("No active debug session");

    private CorDebugThread GetThread(int? threadId, DebugSession session)
    {
        if (threadId.HasValue)
        {
            try { return session.Process.GetThread(threadId.Value); }
            catch { }
        }
        return session.StoppedThread ?? throw new InvalidOperationException("No thread available.");
    }

    private COR_DEBUG_STEP_RANGE[] GetStepRanges(CorDebugILFrame frame, DebugSession session)
    {
        try
        {
            var ipResult = frame.IP;
            var ilOffset = (int)ipResult.pnOffset;

            var function = frame.Function;
            var module = function.Module;
            var reader = GetSymbolReader(module, session);
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

    private static Symbols.ISymbolReader? GetSymbolReader(CorDebugModule module, DebugSession session)
    {
        var name = module.Name;
        if (string.IsNullOrEmpty(name)) return null;
        return session.Modules.TryGetValue(name, out var entry) ? entry.Reader : null;
    }

    private static string GetMethodName(CorDebugModule module, int methodToken)
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

    private async Task EvaluateBreakpointConditionAsync(
        int? breakpointId, int threadId, string condition)
    {
        var session = _session;
        if (session == null) return;

        // Populate the top frame so EvaluateAsync can access locals
        try
        {
            var thread = session.StoppedThread;
            if (thread != null)
            {
                if (thread.ActiveFrame is CorDebugILFrame activeFrame && !session.FrameMap.ContainsKey(0))
                {
                    session.FrameMap[0] = activeFrame;
                    session.NextFrameId = 1;
                }
            }
        }
        catch { }

        try
        {
            var result = await EvaluateAsync(new EvaluateRequest(condition));
            // Condition is true if result equals "true" (boolean)
            if (result.Result.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                Stopped?.Invoke(this, new StoppedEventArgs(
                    new StoppedNotification(StopReason.Breakpoint, threadId,
                        null, breakpointId)));
                return;
            }
        }
        catch
        {
            // Condition evaluation failed — stop so user sees the issue
            Stopped?.Invoke(this, new StoppedEventArgs(
                new StoppedNotification(StopReason.Breakpoint, threadId,
                    $"Condition '{condition}' evaluation failed", breakpointId)));
            return;
        }

        // Condition was false — auto-continue via normal state transitions
        // EvaluateAsync already transitions back to Stopped (via OnEvalComplete or stays Stopped)
        lock (_lock)
        {
            session.StoppedThread = null;
            session.FrameMap.Clear();
            session.NextFrameId = 0;
            _currentState = _currentState.Resume(this);
        }
    }

    private static CorDebugValue? GetCurrentExceptionValue(DebugSession session)
    {
        try
        {
            var exValue = session.StoppedThread?.CurrentException;
            if (exValue == null) return null;
            // Verify it's not a null reference
            if (exValue is CorDebugReferenceValue refVal && refVal.IsNull) return null;
            return exValue;
        }
        catch { return null; }
    }

    /// <summary>
    /// Before StepOut, set hidden native breakpoints at the caller's return-value
    /// live offsets. This is required by the CLR: GetReturnValueForILOffset only
    /// works when a breakpoint is set at the native offset returned by
    /// GetReturnValueLiveOffset.
    /// </summary>
    private static void SetupReturnValueBreakpoints(CorDebugThread thread, DebugSession session)
    {
        session.ReturnValueBreakpoints = null;
        try
        {
            var currentFrame = thread.ActiveFrame;
            if (currentFrame == null) return;

            // Get the caller frame — this is where the call instruction is
            if (currentFrame.Caller is not CorDebugILFrame callerFrame) return;

            var callILOffset = (int)callerFrame.IP.pnOffset;

            // GetReturnValueLiveOffset is on ICorDebugCode3, which is only available
            // on the native code object (not the IL code).
            var callerNativeCode = callerFrame.Function.NativeCode;
            if (callerNativeCode == null) return;

            // Find the call instruction. The caller frame's IP may not point directly
            // at the call — probe forward from the IP to find the actual call site.
            int[]? nativeOffsets = null;
            bool found = false;
            for (int probe = callILOffset; probe <= callILOffset + 20; probe++)
            {
                var probeHr = callerNativeCode.TryGetReturnValueLiveOffset(probe, out var probeOffsets);
                if (probeHr == ClrDebug.HRESULT.S_OK && probeOffsets != null && probeOffsets.Length > 0)
                {
                    callILOffset = probe;
                    nativeOffsets = probeOffsets;
                    found = true;
                    break;
                }
            }
            if (!found || nativeOffsets == null) return;

            var breakpoints = new List<CorDebugFunctionBreakpoint>();
            foreach (var nativeOffset in nativeOffsets)
            {
                try
                {
                    var bp = callerNativeCode.CreateBreakpoint((int)nativeOffset);
                    bp.Activate(true);
                    breakpoints.Add(bp);
                }
                catch { }
            }

            if (breakpoints.Count > 0)
            {
                session.ReturnValueCallILOffset = callILOffset;
                session.ReturnValueBreakpoints = breakpoints;
                foreach (var bp in breakpoints)
                    session.CallbackHandler.ReturnValueBreakpoints.Add(bp);
            }
        }
        catch { }
    }

    /// <summary>
    /// After StepOut completes, capture the return value using
    /// ICorDebugILFrame3.GetReturnValueForILOffset with the pre-stored call site offset.
    /// </summary>
    private static void TryCaptureReturnValue(CorDebugThread? thread, DebugSession session)
    {
        if (session.ReturnValueBreakpoints == null) return;
        try
        {
            if (thread?.ActiveFrame is not CorDebugILFrame frame) return;

            var retHr = frame.TryGetReturnValueForILOffset(session.ReturnValueCallILOffset, out var retVal);
            if (retHr == ClrDebug.HRESULT.S_OK && retVal != null)
                session.LastReturnValue = retVal;
        }
        catch { }
    }

    /// <summary>
    /// Deactivate and remove hidden return-value native breakpoints.
    /// </summary>
    private static void CleanupReturnValueBreakpoints(DebugSession session)
    {
        var breakpoints = session.ReturnValueBreakpoints;
        session.ReturnValueBreakpoints = null;
        if (breakpoints == null) return;

        foreach (var bp in breakpoints)
            session.CallbackHandler.ReturnValueBreakpoints.Remove(bp);

        foreach (var bp in breakpoints)
        {
            try { bp.Activate(false); } catch { }
        }
    }

    private static List<Variable> GetFrameVariables(CorDebugILFrame frame, ValueReader valueReader, DebugSession session)
    {
        var variables = new List<Variable>();
        var module = frame.Function.Module;
        var reader = GetSymbolReader(module, session);

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
                    var (displayValue, type) = valueReader.Read(value);
                    variables.Add(new Variable(localInfo.Name, displayValue, type));
                }
                catch { }
            }
        }

        try
        {
            var import = module.GetMetaDataInterface<MetaDataImport>();
            var methodToken = (mdMethodDef)frame.Function.Token;
            var argNameMap = GetArgumentNameMap(import, methodToken);

            for (int i = 0; ; i++)
            {
                try
                {
                    var value = frame.GetArgument(i);
                    var name = argNameMap.GetValueOrDefault(i, $"arg{i}");
                    var (displayValue, type) = valueReader.Read(value);
                    variables.Add(new Variable(name, displayValue, type));
                }
                catch { break; }
            }
        }
        catch { }

        return variables;
    }

    private static Dictionary<int, string> GetArgumentNameMap(MetaDataImport import, mdMethodDef methodToken)
    {
        var map = new Dictionary<int, string>();
        var methodProps = import.GetMethodProps(methodToken);
        bool isStatic = methodProps.pdwAttr.HasFlag(CorMethodAttr.mdStatic);

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
                        int argIndex = isStatic
                            ? props.pulSequence - 1
                            : props.pulSequence;
                        map[argIndex] = props.szName;
                    }
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
        return map;
    }

    private static void RefreshFrameMap(DebugSession session)
    {
        session.FrameMap.Clear();
        session.NextFrameId = 0;

        if (session.StoppedThread == null) return;
        try
        {
            foreach (var chain in session.StoppedThread.EnumerateChains())
            {
                if (!chain.IsManaged) continue;
                foreach (var frame in chain.EnumerateFrames())
                {
                    if (frame is CorDebugILFrame ilFrame)
                    {
                        session.FrameMap[session.NextFrameId++] = ilFrame;
                    }
                }
            }
        }
        catch { }
    }

    private static void SetEntryPointBreakpoint(CorDebugModule module, ManagedCallbackHandler handler)
    {
        try
        {
            using var peStream = File.OpenRead(module.Name);
            var peReader = new PEReader(peStream);
            var corHeader = peReader.PEHeaders.CorHeader;
            if (corHeader == null) return;

            var entryPointToken = corHeader.EntryPointTokenOrRelativeVirtualAddress;
            if (entryPointToken == 0) return;

            var function = module.GetFunctionFromToken((mdMethodDef)entryPointToken);
            var code = function.ILCode;
            var bp = code.CreateBreakpoint(0);
            bp.Activate(true);
            handler.EntryBreakpoint = bp;
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
        var session = Interlocked.Exchange(ref _session, null);
        if (session == null)
            return;

        try
        {
            var stateId = _currentState.Id;
            if (stateId != DebuggerState.Terminated && stateId != DebuggerState.NotStarted)
            {
                session.Process.Stop(1000);
                session.Process.Terminate(0);
            }
        }
        catch { }

        session.Dispose();
    }
}
