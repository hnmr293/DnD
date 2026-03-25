namespace DnD.Core;

using ClrDebug;
using DnD.Protocol;
using StreamJsonRpc;

/// <summary>
/// Context interface through which state objects access debugger engine internals.
/// Implemented by <see cref="DebuggerEngine"/>.
/// </summary>
public interface ISessionContext
{
    /// <summary>Access the breakpoint manager (persistent across sessions).</summary>
    BreakpointManager BreakpointManager { get; }

    // ── Session lifecycle ──

    /// <summary>Create a new debug session for Launch, returning the session object.</summary>
    DebugSession CreateLaunchSession(LaunchRequest request);

    /// <summary>Create a new debug session for Attach, returning the session object.</summary>
    DebugSession CreateAttachSession(AttachRequest request);

    /// <summary>Get the active session. Throws if no session exists.</summary>
    DebugSession GetSession();

    /// <summary>Dispose the current session and set it to null.</summary>
    void EndSession();

    // ── Process termination ──

    /// <summary>Async Stop + Terminate with timeout safety net.</summary>
    Task TerminateProcessAsync();

    // ── Events ──

    /// <summary>Raise the Stopped event.</summary>
    void RaiseStoppedEvent(StoppedNotification notification);

    /// <summary>Raise the Exited event. Returns false if already fired (idempotent).</summary>
    bool RaiseExitedEvent(int exitCode);
}

/// <summary>
/// State interface for the debugger state machine (State pattern).
/// Each concrete state defines which operations are valid and implements
/// the transition behavior.
/// </summary>
public interface IDebuggerState
{
    DebuggerState Id { get; }

    IDebuggerState Launch(ISessionContext ctx, LaunchRequest request);
    IDebuggerState Attach(ISessionContext ctx, AttachRequest request);
    IDebuggerState Detach(ISessionContext ctx);
    Task<IDebuggerState> TerminateAsync(ISessionContext ctx);
    IDebuggerState Resume(ISessionContext ctx);
    IDebuggerState Pause(ISessionContext ctx);

    IDebuggerState OnBreak(ISessionContext ctx);
    IDebuggerState OnProcessExited(ISessionContext ctx, int exitCode);

    IDebuggerState StartEval(ISessionContext ctx);
    IDebuggerState OnEvalComplete(ISessionContext ctx);

    void EnsureStopped();
    void EnsureNotTerminated();
    void EnsureNotTerminatedForBreakpoints();
}

/// <summary>
/// Base class providing default implementations that throw appropriate errors
/// for operations not supported in a given state.
/// </summary>
public abstract class DebuggerStateBase : IDebuggerState
{
    public abstract DebuggerState Id { get; }

    public virtual IDebuggerState Launch(ISessionContext ctx, LaunchRequest request)
        => throw CreateError(DebuggerState.NotStarted);

    public virtual IDebuggerState Attach(ISessionContext ctx, AttachRequest request)
        => throw CreateError(DebuggerState.NotStarted);

    public virtual IDebuggerState Detach(ISessionContext ctx)
        => throw CreateError("Cannot detach in current state");

    public virtual Task<IDebuggerState> TerminateAsync(ISessionContext ctx)
        => throw CreateError("Cannot terminate in current state");

    public virtual IDebuggerState Resume(ISessionContext ctx)
        => throw CreateError(DebuggerState.Stopped);

    public virtual IDebuggerState Pause(ISessionContext ctx)
        => throw CreateError(DebuggerState.Running);

    public virtual IDebuggerState OnBreak(ISessionContext ctx)
        => throw new InvalidOperationException($"Unexpected break callback in {Id} state");

    public virtual IDebuggerState OnProcessExited(ISessionContext ctx, int exitCode)
        => throw new InvalidOperationException($"Unexpected process exit callback in {Id} state");

    public virtual IDebuggerState StartEval(ISessionContext ctx)
        => throw new InvalidOperationException($"Cannot start eval in {Id} state");

    public virtual IDebuggerState OnEvalComplete(ISessionContext ctx)
        => throw new InvalidOperationException($"Unexpected eval completion in {Id} state");

    public virtual void EnsureStopped()
        => throw new LocalRpcException($"Invalid state: expected {DebuggerState.Stopped}, got {Id}")
        {
            ErrorCode = Id == DebuggerState.Running
                ? ErrorCodes.ProcessNotStopped
                : ErrorCodes.NotAttached
        };

    public virtual void EnsureNotTerminated() { }

    public virtual void EnsureNotTerminatedForBreakpoints() { }

    protected LocalRpcException CreateError(DebuggerState expected)
    {
        var errorCode = Id switch
        {
            DebuggerState.NotStarted => ErrorCodes.NotAttached,
            DebuggerState.Running => expected == DebuggerState.Stopped
                ? ErrorCodes.ProcessNotStopped : ErrorCodes.ProcessRunning,
            DebuggerState.Stopped => ErrorCodes.ProcessRunning,
            DebuggerState.Evaluating => ErrorCodes.ProcessRunning,
            DebuggerState.Terminated => ErrorCodes.NotAttached,
            _ => ErrorCodes.NotAttached
        };
        return new LocalRpcException($"Invalid state: expected {expected}, got {Id}")
        { ErrorCode = errorCode };
    }

    protected static LocalRpcException CreateError(string message)
        => new(message) { ErrorCode = ErrorCodes.NotAttached };
}

public sealed class NotStartedState : DebuggerStateBase
{
    public static readonly NotStartedState Instance = new();
    private NotStartedState() { }

    public override DebuggerState Id => DebuggerState.NotStarted;

    public override IDebuggerState Launch(ISessionContext ctx, LaunchRequest request)
    {
        if (!File.Exists(request.Program))
            throw new FileNotFoundException($"Program not found: {request.Program}");

        ctx.CreateLaunchSession(request);
        return RunningState.Instance;
    }

    public override IDebuggerState Attach(ISessionContext ctx, AttachRequest request)
    {
        ctx.CreateAttachSession(request);
        return RunningState.Instance;
    }

    public override IDebuggerState Detach(ISessionContext ctx)
        => this; // Already not attached — no-op

    public override Task<IDebuggerState> TerminateAsync(ISessionContext ctx)
    {
        ctx.RaiseExitedEvent(0);
        return Task.FromResult<IDebuggerState>(TerminatedState.Instance);
    }

    public override void EnsureNotTerminated()
        => throw new LocalRpcException("No process attached") { ErrorCode = ErrorCodes.NotAttached };
}

public sealed class RunningState : DebuggerStateBase
{
    public static readonly RunningState Instance = new();
    private RunningState() { }

    public override DebuggerState Id => DebuggerState.Running;

    public override IDebuggerState Pause(ISessionContext ctx)
    {
        var session = ctx.GetSession();
        session.Process.Stop(0);

        CorDebugThread? thread = null;
        try
        {
            foreach (var t in session.Process.EnumerateThreads())
            {
                thread = t;
                break;
            }
        }
        catch { }

        session.SetStopState(thread);

        var threadId = 0;
        try { threadId = thread?.Id ?? 0; } catch { }
        ctx.RaiseStoppedEvent(new StoppedNotification(
            StopReason.Pause, threadId, "Paused by user"));

        return StoppedState.Instance;
    }

    public override IDebuggerState Detach(ISessionContext ctx)
    {
        ctx.BreakpointManager.DeactivateAll();
        var session = ctx.GetSession();
        try { session.Process.Stop(5000); } catch { }
        try { session.Process.Detach(); } catch { }
        ctx.BreakpointManager.RevertAllToPending();
        ctx.EndSession();
        return NotStartedState.Instance;
    }

    public override Task<IDebuggerState> TerminateAsync(ISessionContext ctx)
        => TerminateImplAsync(ctx);

    public override IDebuggerState OnBreak(ISessionContext ctx)
        => StoppedState.Instance;

    public override IDebuggerState OnProcessExited(ISessionContext ctx, int exitCode)
    {
        ctx.EndSession();
        ctx.RaiseExitedEvent(exitCode);
        return TerminatedState.Instance;
    }

    internal static async Task<IDebuggerState> TerminateImplAsync(ISessionContext ctx)
    {
        await ctx.TerminateProcessAsync();
        ctx.EndSession();
        ctx.RaiseExitedEvent(0);
        return TerminatedState.Instance;
    }
}

public sealed class StoppedState : DebuggerStateBase
{
    public static readonly StoppedState Instance = new();
    private StoppedState() { }

    public override DebuggerState Id => DebuggerState.Stopped;

    public override IDebuggerState Resume(ISessionContext ctx)
    {
        var session = ctx.GetSession();
        session.Process.Continue(false);
        session.ClearStopState();
        return RunningState.Instance;
    }

    public override IDebuggerState Pause(ISessionContext ctx)
    {
        // Already stopped — re-fire event so caller gets a response
        var session = ctx.GetSession();
        ctx.RaiseStoppedEvent(new StoppedNotification(
            StopReason.Pause, session.GetStoppedThreadId(), "Already stopped"));
        return this;
    }

    public override IDebuggerState Detach(ISessionContext ctx)
    {
        ctx.BreakpointManager.DeactivateAll();
        var session = ctx.GetSession();
        // Resume before detach — ICorDebug requires the process to not be
        // at a callback boundary. Continue + Stop drains queued callbacks.
        try { session.Process.Continue(false); } catch { }
        try { session.Process.Stop(5000); } catch { }
        try { session.Process.Detach(); } catch { }
        ctx.BreakpointManager.RevertAllToPending();
        ctx.EndSession();
        return NotStartedState.Instance;
    }

    public override Task<IDebuggerState> TerminateAsync(ISessionContext ctx)
        => RunningState.TerminateImplAsync(ctx);

    public override IDebuggerState OnProcessExited(ISessionContext ctx, int exitCode)
    {
        ctx.EndSession();
        ctx.RaiseExitedEvent(exitCode);
        return TerminatedState.Instance;
    }

    public override IDebuggerState StartEval(ISessionContext ctx)
        => EvaluatingState.Instance;

    public override void EnsureStopped() { } // OK — we are stopped
}

public sealed class EvaluatingState : DebuggerStateBase
{
    public static readonly EvaluatingState Instance = new();
    private EvaluatingState() { }

    public override DebuggerState Id => DebuggerState.Evaluating;

    public override IDebuggerState Resume(ISessionContext ctx)
        => throw new LocalRpcException("Cannot resume while func-eval is in progress")
        { ErrorCode = ErrorCodes.ProcessRunning };

    public override IDebuggerState Pause(ISessionContext ctx)
        => throw new LocalRpcException("Cannot pause while func-eval is in progress")
        { ErrorCode = ErrorCodes.ProcessRunning };

    public override IDebuggerState Detach(ISessionContext ctx)
        => throw new LocalRpcException("Cannot detach while func-eval is in progress")
        { ErrorCode = ErrorCodes.ProcessRunning };

    public override Task<IDebuggerState> TerminateAsync(ISessionContext ctx)
        => RunningState.TerminateImplAsync(ctx);

    public override IDebuggerState OnEvalComplete(ISessionContext ctx)
        => StoppedState.Instance;

    public override IDebuggerState OnProcessExited(ISessionContext ctx, int exitCode)
    {
        ctx.EndSession();
        ctx.RaiseExitedEvent(exitCode);
        return TerminatedState.Instance;
    }

    // Conceptually stopped — variable inspection is still valid
    public override void EnsureStopped() { }
}

public sealed class TerminatedState : DebuggerStateBase
{
    public static readonly TerminatedState Instance = new();
    private TerminatedState() { }

    public override DebuggerState Id => DebuggerState.Terminated;

    public override IDebuggerState Detach(ISessionContext ctx)
        => this; // Already terminated — no-op

    public override Task<IDebuggerState> TerminateAsync(ISessionContext ctx)
    {
        // Idempotent — fire exited event if not already
        ctx.RaiseExitedEvent(0);
        return Task.FromResult<IDebuggerState>(this);
    }

    public override void EnsureNotTerminated()
        => throw new LocalRpcException("Process has terminated") { ErrorCode = ErrorCodes.NotAttached };

    public override void EnsureNotTerminatedForBreakpoints()
        => throw new LocalRpcException("Process has terminated") { ErrorCode = ErrorCodes.NotAttached };
}
