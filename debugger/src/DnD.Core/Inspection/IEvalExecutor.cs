namespace DnD.Core.Inspection;

using ClrDebug;

/// <summary>
/// Interface for executing func-evals on the debuggee.
/// Decouples evaluators from DebuggerEngine.
/// </summary>
public interface IEvalExecutor
{
    Task<CorDebugValue> ExecuteEvalAsync(
        Action<CorDebugEval> setupAction, CorDebugThread thread, TimeSpan? timeout = null);
}
