namespace DnD.Core;

using ClrDebug;
using DnD.Core.Callbacks;
using DnD.Core.Inspection;

/// <summary>
/// Encapsulates all resources and state that belong to a single debug session.
/// Created on Launch/Attach, disposed on Detach/Exit/Terminate.
/// The invariant is simple: session exists ↔ debugging is active.
/// </summary>
public class DebugSession : IDisposable
{
    // ── COM resources (created at Launch/Attach, destroyed at Dispose) ──

    public CorDebug CorDebug { get; }
    public CorDebugProcess Process { get; }
    public ManagedCallbackHandler CallbackHandler { get; }
    public Dictionary<string, (CorDebugModule Module, Symbols.ISymbolReader? Reader)> Modules { get; } = new();

    // ── Stop-time transient state (set on stop, cleared on resume) ──

    public CorDebugThread? StoppedThread { get; internal set; }
    public Dictionary<int, CorDebugILFrame> FrameMap { get; } = new();
    public int NextFrameId { get; set; }
    public VariableStore VariableStore { get; } = new();
    public CorDebugValue? LastReturnValue { get; set; }
    public bool SuppressNextStepCallback { get; set; }

    // ── Session-lifetime state (set at Launch, used throughout) ──

    public bool StopAtEntry { get; set; }
    public string? ProgramPath { get; set; }
    public bool ExitedEventFired { get; set; }
    public TaskCompletionSource<(CorDebugEval Eval, bool Success)>? EvalTcs { get; set; }
    public bool PendingStepOut { get; set; }
    public int ReturnValueCallILOffset { get; set; }
    public List<CorDebugFunctionBreakpoint>? ReturnValueBreakpoints { get; set; }

    public DebugSession(CorDebug corDebug, CorDebugProcess process, ManagedCallbackHandler callbackHandler)
    {
        CorDebug = corDebug;
        Process = process;
        CallbackHandler = callbackHandler;
    }

    public void SetStopState(CorDebugThread? thread)
    {
        StoppedThread = thread;
        VariableStore.Clear();
        FrameMap.Clear();
        NextFrameId = 0;
    }

    public void ClearStopState()
    {
        VariableStore.Clear();
        FrameMap.Clear();
        NextFrameId = 0;
        LastReturnValue = null;
        SuppressNextStepCallback = false;
    }

    public int GetStoppedThreadId()
    {
        try { return StoppedThread?.Id ?? 0; }
        catch { return 0; }
    }

    public bool IsProcessAlive()
    {
        try
        {
            using var osProcess = System.Diagnostics.Process.GetProcessById(Process.Id);
            return !osProcess.HasExited;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        // Release symbol readers (unlocks PDB files)
        foreach (var (_, (_, reader)) in Modules)
            reader?.Dispose();
        Modules.Clear();

        // Terminate ICorDebug
        try { CorDebug.Terminate(); } catch { }

        // Release COM RCW references
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }
}
