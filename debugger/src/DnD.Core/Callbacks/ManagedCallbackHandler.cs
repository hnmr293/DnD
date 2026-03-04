namespace DnD.Core.Callbacks;

using ClrDebug;
using DnD.Protocol;

public class ManagedCallbackHandler
{
    private readonly CorDebugManagedCallback _callback;
    private CorDebugProcess? _process;

    public event Action<CorDebugThread, StopReason, string?>? OnStopped;
    public event Action<int>? OnProcessExited;
    public event Action<CorDebugModule>? OnModuleLoaded;
    public event Action<CorDebugThread, CorDebugEval, bool>? OnEvalCompleted;

    public ManagedCallbackHandler()
    {
        _callback = new CorDebugManagedCallback();
        WireEvents();
    }

    public CorDebugManagedCallback Callback => _callback;

    private void ContinueProcess()
    {
        try { _process?.Continue(false); }
        catch { }
    }

    private void WireEvents()
    {
        _callback.OnCreateProcess += (s, e) =>
        {
            _process = e.Process;
            ContinueProcess();
        };

        _callback.OnBreakpoint += (s, e) =>
        {
            OnStopped?.Invoke(e.Thread, StopReason.Breakpoint, null);
        };

        _callback.OnStepComplete += (s, e) =>
        {
            OnStopped?.Invoke(e.Thread, StopReason.Step, null);
        };

        _callback.OnBreak += (s, e) =>
        {
            OnStopped?.Invoke(e.Thread, StopReason.Pause, "Debugger.Break()");
        };

        _callback.OnException += (s, e) =>
        {
            ContinueProcess();
        };

        _callback.OnException2 += (s, e) =>
        {
            if (e.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE)
            {
                ContinueProcess();
                return;
            }
            OnStopped?.Invoke(null!, StopReason.Exception, "Unhandled exception");
        };

        _callback.OnExitProcess += (s, e) =>
        {
            OnProcessExited?.Invoke(0);
        };

        _callback.OnLoadModule += (s, e) =>
        {
            OnModuleLoaded?.Invoke(e.Module);
            ContinueProcess();
        };

        _callback.OnCreateAppDomain += (s, e) =>
        {
            e.AppDomain.Attach();
            ContinueProcess();
        };
        _callback.OnLoadClass += (s, e) => ContinueProcess();
        _callback.OnUnloadClass += (s, e) => ContinueProcess();
        _callback.OnUnloadModule += (s, e) => ContinueProcess();
        _callback.OnCreateThread += (s, e) => ContinueProcess();
        _callback.OnExitThread += (s, e) => ContinueProcess();
        _callback.OnExitAppDomain += (s, e) => ContinueProcess();
        _callback.OnLoadAssembly += (s, e) => ContinueProcess();
        _callback.OnUnloadAssembly += (s, e) => ContinueProcess();
        _callback.OnNameChange += (s, e) => ContinueProcess();
        _callback.OnLogMessage += (s, e) => ContinueProcess();

        _callback.OnEvalComplete += (s, e) =>
        {
            // Do NOT continue — the evaluator needs to read the result first
            OnEvalCompleted?.Invoke(e.Thread, e.Eval, true);
        };

        _callback.OnEvalException += (s, e) =>
        {
            // Do NOT continue — the evaluator needs to handle the error
            OnEvalCompleted?.Invoke(e.Thread, e.Eval, false);
        };
    }
}
