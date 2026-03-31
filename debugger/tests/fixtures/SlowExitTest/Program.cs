using System;
using System.Diagnostics;
using System.Threading;

/// <summary>
/// Test fixture for Issue #15 regression tests.
/// Designed to maximize the race window between the debugger's
/// OnProcessExited callback and engine.Dispose/TerminateAsync.
///
/// After Debugger.Break(), the process stays alive for 10 seconds
/// (long enough for the test to call Continue then Dispose).
/// The ProcessExit handler adds additional delay to the CLR shutdown,
/// widening the window between "process starts exiting" and
/// "ExitProcess callback fires in the debugger."
/// </summary>
class Program
{
    static Program()
    {
        // Delay CLR shutdown to widen the race window between
        // OnProcessExited callback and engine.Dispose.
        // When the debugger calls Continue and Main returns,
        // ProcessExit fires BEFORE the ExitProcess callback.
        // This sleep delays the ExitProcess callback, giving
        // engine.Dispose time to start its cleanup.
        AppDomain.CurrentDomain.ProcessExit += (s, e) =>
        {
            Thread.Sleep(2000);
        };
    }

    static void Main()
    {
        Debugger.Break();
        // After Continue, keep the process alive.
        // The debugger's Dispose will call Process.Terminate(0)
        // to kill this sleep, triggering the ExitProcess callback
        // while session cleanup is in progress.
        Thread.Sleep(10000);
    }
}
