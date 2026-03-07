namespace DnD.Core.Callbacks;

using System.Runtime.InteropServices;
using ClrDebug;
using DnD.Protocol;

public class ManagedCallbackHandler
{
    private readonly CorDebugManagedCallback _callback;
    private CorDebugProcess? _process;
    private IntPtr _processHandle;
    private bool _hadUnhandledException;

    internal CorDebugFunctionBreakpoint? EntryBreakpoint { get; set; }

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
            // Open a handle to query exit code later (survives process exit)
            const uint PROCESS_QUERY_INFORMATION = 0x0400;
            _processHandle = OpenProcess(PROCESS_QUERY_INFORMATION, false, _process.Id);
            ContinueProcess();
        };

        _callback.OnBreakpoint += (s, e) =>
        {
            var entryBp = EntryBreakpoint;
            if (entryBp != null)
            {
                EntryBreakpoint = null;
                try { entryBp.Activate(false); } catch { }
                OnStopped?.Invoke(e.Thread, StopReason.Entry, "Entry point");
                return;
            }
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
            if (e.EventType != CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED)
            {
                ContinueProcess();
                return;
            }
            _hadUnhandledException = true;
            var description = GetExceptionDescription(e.Thread);
            OnStopped?.Invoke(e.Thread, StopReason.Exception, description);
        };

        _callback.OnExitProcess += (s, e) =>
        {
            var exitCode = 0;
            if (_processHandle != IntPtr.Zero)
            {
                GetExitCodeProcess(_processHandle, out exitCode);
                CloseHandle(_processHandle);
                _processHandle = IntPtr.Zero;
            }
            // Under ICorDebug, continuing from an unhandled exception may produce
            // exit code 0. Use a fallback to ensure the crash is reflected.
            if (exitCode == 0 && _hadUnhandledException)
                exitCode = -1;
            OnProcessExited?.Invoke(exitCode);
            // Continue is required after every ICorDebug callback — even ExitProcess.
            // Without this, the callback thread stays blocked and subsequent calls
            // (like ICorDebugProcess::Stop) can hang indefinitely.
            ContinueProcess();
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

    private static string GetExceptionDescription(CorDebugThread thread)
    {
        try
        {
            var exValue = thread.CurrentException;
            if (exValue == null) return "Unhandled exception";

            // Dereference to get the actual object
            CorDebugValue value = exValue;
            if (value is CorDebugReferenceValue refVal)
            {
                if (refVal.IsNull) return "Unhandled exception";
                value = refVal.Dereference();
            }

            if (value is not CorDebugObjectValue objVal) return "Unhandled exception";

            // Get exception type name
            var cls = objVal.Class;
            var module = cls.Module;
            var import = module.GetMetaDataInterface<MetaDataImport>();
            var typeProps = import.GetTypeDefProps(cls.Token);
            var typeName = typeProps.szTypeDef;

            // Try to read the _message field from System.Exception
            string? message = ReadExceptionMessage(objVal, cls, import);

            if (!string.IsNullOrEmpty(message))
                return $"{typeName}: {message}";
            return typeName;
        }
        catch
        {
            return "Unhandled exception";
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static string? ReadExceptionMessage(
        CorDebugObjectValue objVal, CorDebugClass cls, MetaDataImport import)
    {
        try
        {
            var module = cls.Module;

            // Find System.Exception directly by name — more reliable than walking hierarchy
            var exceptionTypeDef = import.FindTypeDefByName("System.Exception", 0);
            var exceptionClass = module.GetClassFromToken(exceptionTypeDef);

            // Enumerate fields on System.Exception to find _message
            var enumHandle = IntPtr.Zero;
            var fieldTokens = new mdFieldDef[64];

            var count = import.EnumFields(ref enumHandle, exceptionTypeDef, fieldTokens);
            if (enumHandle != IntPtr.Zero)
                import.CloseEnum(enumHandle);

            for (int i = 0; i < count; i++)
            {
                var fieldProps = import.GetFieldProps(fieldTokens[i]);
                if (fieldProps.szField == "_message")
                {
                    var fieldValue = objVal.GetFieldValue(exceptionClass.Raw, fieldTokens[i]);

                    if (fieldValue is CorDebugReferenceValue fieldRef)
                    {
                        if (fieldRef.IsNull) return null;
                        fieldValue = fieldRef.Dereference();
                    }

                    if (fieldValue is CorDebugStringValue strVal)
                        return strVal.GetString(strVal.Length);

                    return null;
                }
            }
        }
        catch { }
        return null;
    }
}
