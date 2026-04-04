namespace DnD.Core.Tests;

using ClrDebug;
using DnD.Core.Runtime;
using DnD.Protocol;
using StreamJsonRpc;

/// <summary>
/// Reproduces Issue #18: func-eval timeout in Phase 4 (unabortable eval) leaves the
/// state machine stuck in Evaluating, preventing session recovery.
///
/// The test uses mock COM objects to deterministically reach Phase 4 (no eval callback
/// ever fires), then verifies the state after the timeout exception.
/// </summary>
public class EvalTimeoutRecoveryTests : IDisposable
{
    private static readonly string ExistingFile =
        typeof(EvalTimeoutRecoveryTests).Assembly.Location;

    private readonly DebuggerEngine _engine;
    private readonly EvalTimeoutLauncher _launcher;

    public EvalTimeoutRecoveryTests()
    {
        _launcher = new EvalTimeoutLauncher();
        _engine = new DebuggerEngine(_launcher);
    }

    public void Dispose() => _engine.Dispose();

    /// <summary>
    /// After Phase 4 timeout, state recovers to Stopped and a subsequent eval attempt
    /// passes the state check (StartEval succeeds). The second eval also times out
    /// (no callback fires), proving the session remains usable.
    /// </summary>
    [Fact]
    public async Task Phase4Timeout_StateRecoversToStopped_SubsequentEvalWorks()
    {
        // Arrange: Launch and transition to Stopped state
        await _engine.LaunchAsync(new LaunchRequest(Program: ExistingFile));
        _launcher.SimulateBreak();

        // Act: Execute eval that will time out through all phases (no callback fires)
        var thread = new CorDebugThread(_launcher.MockThread);
        var ex = await Assert.ThrowsAsync<LocalRpcException>(
            () => _engine.ExecuteEvalAsync(_ => { }, thread, TimeSpan.FromMilliseconds(10)));

        // Assert: Phase 4 error message
        Assert.Contains("could not be aborted", ex.Message);

        // FIX: State recovered to Stopped — a second eval passes StartEval and
        // enters the timeout cycle again (proving the session is still alive).
        var secondThread = new CorDebugThread(_launcher.MockThread);
        var secondEx = await Assert.ThrowsAsync<LocalRpcException>(
            () => _engine.ExecuteEvalAsync(_ => { }, secondThread, TimeSpan.FromMilliseconds(10)));
        Assert.Contains("could not be aborted", secondEx.Message);
    }

    /// <summary>
    /// If the process dies after Phase 4 recovery (due to Phase 3's process.Stop()
    /// destabilization), OnProcessExited transitions Stopped → Terminated.
    /// With the _lock fix, this transition is properly serialized.
    /// </summary>
    [Fact]
    public async Task Phase4Timeout_ProcessDies_TransitionsToTerminated()
    {
        // Arrange: Launch and transition to Stopped state
        ExitedNotification? exitedNotification = null;
        _engine.Exited += (_, e) => exitedNotification = e.Notification;

        await _engine.LaunchAsync(new LaunchRequest(Program: ExistingFile));
        _launcher.SimulateBreak();

        // Act: Execute eval that will time out
        var thread = new CorDebugThread(_launcher.MockThread);
        var ex = await Assert.ThrowsAsync<LocalRpcException>(
            () => _engine.ExecuteEvalAsync(_ => { }, thread, TimeSpan.FromMilliseconds(10)));
        Assert.Contains("could not be aborted", ex.Message);

        // Simulate process death — now runs with _lock, properly serialized.
        // State transitions: Stopped (recovered) → Terminated via OnProcessExited.
        _launcher.SimulateProcessExit(exitCode: -1);

        // Verify: Exited event was fired
        Assert.NotNull(exitedNotification);

        // Session is destroyed (expected for process death) — subsequent eval fails.
        var secondThread = new CorDebugThread(_launcher.MockThread);
        var sessionEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _engine.ExecuteEvalAsync(_ => { }, secondThread, TimeSpan.FromMilliseconds(10)));
        Assert.Contains("No active debug session", sessionEx.Message);
    }

    /// <summary>
    /// A late eval callback arriving after Phase 4 recovery (state = Stopped) is
    /// gracefully ignored by StoppedState.OnEvalComplete.
    /// </summary>
    [Fact]
    public async Task LateEvalCallback_AfterPhase4Recovery_IsIgnored()
    {
        // Arrange: Launch and transition to Stopped state
        await _engine.LaunchAsync(new LaunchRequest(Program: ExistingFile));
        _launcher.SimulateBreak();

        // Act: Execute eval that times out (Phase 4) — state recovers to Stopped
        var thread = new CorDebugThread(_launcher.MockThread);
        await Assert.ThrowsAsync<LocalRpcException>(
            () => _engine.ExecuteEvalAsync(_ => { }, thread, TimeSpan.FromMilliseconds(10)));

        // Simulate late eval callback arriving after Phase 4 recovery.
        // StoppedState.OnEvalComplete handles this as a no-op (returns this).
        var lateCallbackEx = Record.Exception(() => _launcher.SimulateEvalComplete());
        Assert.Null(lateCallbackEx);
    }

    /// <summary>
    /// A late eval callback arriving after Phase 4 + process death (state = Terminated)
    /// is gracefully ignored by TerminatedState.OnEvalComplete.
    /// This is the exact sequence observed in E2E testing with Release builds.
    /// </summary>
    [Fact]
    public async Task LateEvalCallback_AfterProcessDeath_IsIgnored()
    {
        // Arrange: Launch and transition to Stopped state
        await _engine.LaunchAsync(new LaunchRequest(Program: ExistingFile));
        _launcher.SimulateBreak();

        // Phase 4 timeout — state recovers to Stopped
        var thread = new CorDebugThread(_launcher.MockThread);
        await Assert.ThrowsAsync<LocalRpcException>(
            () => _engine.ExecuteEvalAsync(_ => { }, thread, TimeSpan.FromMilliseconds(10)));

        // Process dies — state transitions Stopped → Terminated
        _launcher.SimulateProcessExit(exitCode: -1);

        // Late eval callback arrives in Terminated state — must not throw
        var lateCallbackEx = Record.Exception(() => _launcher.SimulateEvalComplete());
        Assert.Null(lateCallbackEx);
    }

    // ── Mock IProcessLauncher ───────────────────────────────────────────

    private class EvalTimeoutLauncher : IProcessLauncher
    {
        private CorDebugManagedCallback? _savedCallback;
        public MockCorDebugThread MockThread { get; } = new();

        public LaunchResult Launch(
            string program, string[]? args, string? cwd,
            Dictionary<string, string>? env,
            CorDebugManagedCallback callback)
        {
            _savedCallback = callback;
            return new LaunchResult(
                new CorDebug(new MockCorDebug()),
                new CorDebugProcess(new MockCorDebugProcess()));
        }

        public LaunchResult Attach(int processId, CorDebugManagedCallback callback)
            => throw new NotImplementedException();

        /// <summary>Fire a Break callback to transition Running → Stopped.</summary>
        public void SimulateBreak()
        {
            var mcb = (ICorDebugManagedCallback)_savedCallback!;
            mcb.Break(new MockCorDebugAppDomain(), MockThread);
        }

        /// <summary>Fire an ExitProcess callback to simulate process death.</summary>
        public void SimulateProcessExit(int exitCode)
        {
            var mcb = (ICorDebugManagedCallback)_savedCallback!;
            mcb.ExitProcess(new MockCorDebugProcess());
        }

        /// <summary>Fire an EvalComplete callback to simulate late eval result.</summary>
        public void SimulateEvalComplete()
        {
            var mcb = (ICorDebugManagedCallback)_savedCallback!;
            mcb.EvalComplete(new MockCorDebugAppDomain(), new MockCorDebugThread(), new MockCorDebugEval());
        }
    }

    // ── Mock COM implementations ────────────────────────────────────────

    internal class MockCorDebugThread : ICorDebugThread
    {
        public HRESULT GetProcess(out ICorDebugProcess ppProcess) { ppProcess = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetID(out int pdwThreadId) { pdwThreadId = 1; return HRESULT.S_OK; }
        public HRESULT GetHandle(out IntPtr phThreadHandle) { phThreadHandle = IntPtr.Zero; return HRESULT.E_NOTIMPL; }
        public HRESULT GetAppDomain(out ICorDebugAppDomain ppAppDomain) { ppAppDomain = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT SetDebugState(CorDebugThreadState state) => HRESULT.E_NOTIMPL;
        public HRESULT GetDebugState(out CorDebugThreadState pState) { pState = default; return HRESULT.E_NOTIMPL; }
        public HRESULT GetUserState(out CorDebugUserState pState) { pState = default; return HRESULT.E_NOTIMPL; }
        public HRESULT GetCurrentException(out ICorDebugValue ppExceptionObject) { ppExceptionObject = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT ClearCurrentException() => HRESULT.E_NOTIMPL;
        public HRESULT CreateStepper(out ICorDebugStepper ppStepper) { ppStepper = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT EnumerateChains(out ICorDebugChainEnum ppChains) { ppChains = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetActiveChain(out ICorDebugChain ppChain) { ppChain = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetActiveFrame(out ICorDebugFrame ppFrame) { ppFrame = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetRegisterSet(out ICorDebugRegisterSet ppRegisters) { ppRegisters = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT CreateEval(out ICorDebugEval ppEval)
        {
            ppEval = new MockCorDebugEval();
            return HRESULT.S_OK;
        }
        public HRESULT GetObject(out ICorDebugValue ppObject) { ppObject = null!; return HRESULT.E_NOTIMPL; }
    }

    internal class MockCorDebugEval : ICorDebugEval
    {
        public HRESULT CallFunction(ICorDebugFunction pFunction, int nArgs, ICorDebugValue[] ppArgs) => HRESULT.E_NOTIMPL;
        public HRESULT NewObject(ICorDebugFunction pConstructor, int nArgs, ICorDebugValue[] ppArgs) => HRESULT.E_NOTIMPL;
        public HRESULT NewObjectNoConstructor(ICorDebugClass pClass) => HRESULT.E_NOTIMPL;
        public HRESULT NewString(string @string) => HRESULT.E_NOTIMPL;
        public HRESULT NewArray(CorElementType elementType, ICorDebugClass pElementClass, int rank, int[] dims, int[] lowBounds) => HRESULT.E_NOTIMPL;
        public HRESULT IsActive(out bool pbActive) { pbActive = false; return HRESULT.S_OK; }
        public HRESULT Abort() => HRESULT.S_OK;
        public HRESULT GetResult(out ICorDebugValue ppResult) { ppResult = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetThread(out ICorDebugThread ppThread) { ppThread = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT CreateValue(CorElementType elementType, ICorDebugClass pElementClass, out ICorDebugValue ppValue) { ppValue = null!; return HRESULT.E_NOTIMPL; }
    }

    private class MockCorDebugAppDomain : ICorDebugAppDomain
    {
        public HRESULT GetProcess(out ICorDebugProcess ppProcess) { ppProcess = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT EnumerateAssemblies(out ICorDebugAssemblyEnum ppAssemblies) { ppAssemblies = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetModuleFromMetaDataInterface(object pIMetaData, out ICorDebugModule ppModule) { ppModule = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT EnumerateBreakpoints(out ICorDebugBreakpointEnum ppBreakpoints) { ppBreakpoints = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT EnumerateSteppers(out ICorDebugStepperEnum ppSteppers) { ppSteppers = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT IsAttached(out bool pbAttached) { pbAttached = false; return HRESULT.E_NOTIMPL; }
        public HRESULT GetName(int cchName, out int pcchName, char[]? szName) { pcchName = 0; return HRESULT.E_NOTIMPL; }
        public HRESULT GetObject(out ICorDebugValue ppObject) { ppObject = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT Attach() => HRESULT.S_OK;
        public HRESULT GetID(out int pId) { pId = 1; return HRESULT.S_OK; }
        public HRESULT Stop(int dwTimeoutIgnored) => HRESULT.S_OK;
        public HRESULT Continue(bool fIsOutOfBand) => HRESULT.S_OK;
        public HRESULT IsRunning(out bool pbRunning) { pbRunning = false; return HRESULT.E_NOTIMPL; }
        public HRESULT HasQueuedCallbacks(ICorDebugThread? pThread, out bool pbQueued) { pbQueued = false; return HRESULT.E_NOTIMPL; }
        public HRESULT EnumerateThreads(out ICorDebugThreadEnum ppThreads) { ppThreads = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT SetAllThreadsDebugState(CorDebugThreadState state, ICorDebugThread? pExceptThisThread) => HRESULT.E_NOTIMPL;
        public HRESULT Detach() => HRESULT.S_OK;
        public HRESULT Terminate(int exitCode) => HRESULT.S_OK;
        public HRESULT CanCommitChanges(int cSnapshots, ref ICorDebugEditAndContinueSnapshot pSnapshots, out ICorDebugErrorInfoEnum pError) { pError = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT CommitChanges(int cSnapshots, ref ICorDebugEditAndContinueSnapshot pSnapshots, out ICorDebugErrorInfoEnum pError) { pError = null!; return HRESULT.E_NOTIMPL; }
    }

    private class MockCorDebug : ICorDebug
    {
        public HRESULT Initialize() => HRESULT.S_OK;
        public HRESULT Terminate() => HRESULT.S_OK;
        public HRESULT SetManagedHandler(ICorDebugManagedCallback pCallback) => HRESULT.S_OK;
        public HRESULT SetUnmanagedHandler(ICorDebugUnmanagedCallback pCallback) => HRESULT.E_NOTIMPL;
        public HRESULT CreateProcess(string lpApplicationName, string lpCommandLine, ref SECURITY_ATTRIBUTES lpProcessAttributes, ref SECURITY_ATTRIBUTES lpThreadAttributes, bool bInheritHandles, CreateProcessFlags dwCreationFlags, IntPtr lpEnvironment, string lpCurrentDirectory, ref STARTUPINFOW lpStartupInfo, ref PROCESS_INFORMATION lpProcessInformation, CorDebugCreateProcessFlags debuggingFlags, out ICorDebugProcess ppProcess) { ppProcess = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT DebugActiveProcess(int id, bool win32Attach, out ICorDebugProcess ppProcess) { ppProcess = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT EnumerateProcesses(out ICorDebugProcessEnum ppProcess) { ppProcess = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetProcess(int dwProcessId, out ICorDebugProcess ppProcess) { ppProcess = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT CanLaunchOrAttach(int dwProcessId, int win32DebuggingEnabled) => HRESULT.E_NOTIMPL;
    }

    private class MockCorDebugProcess : ICorDebugProcess
    {
        public HRESULT GetID(out int pdwProcessId) { pdwProcessId = 9999; return HRESULT.S_OK; }
        public HRESULT GetHandle(out IntPtr phProcessHandle) { phProcessHandle = IntPtr.Zero; return HRESULT.E_NOTIMPL; }
        public HRESULT GetThread(int dwThreadId, out ICorDebugThread ppThread) { ppThread = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT EnumerateObjects(out ICorDebugObjectEnum ppObjects) { ppObjects = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT IsTransitionStub(CORDB_ADDRESS address, out bool pbTransitionStub) { pbTransitionStub = false; return HRESULT.E_NOTIMPL; }
        public HRESULT IsOSSuspended(int threadID, out bool pbSuspended) { pbSuspended = false; return HRESULT.E_NOTIMPL; }
        public HRESULT GetThreadContext(int threadID, int contextSize, IntPtr context) => HRESULT.E_NOTIMPL;
        public HRESULT SetThreadContext(int threadID, int contextSize, IntPtr context) => HRESULT.E_NOTIMPL;
        public HRESULT ReadMemory(CORDB_ADDRESS address, int size, IntPtr buffer, out int read) { read = 0; return HRESULT.E_NOTIMPL; }
        public HRESULT WriteMemory(CORDB_ADDRESS address, int size, IntPtr buffer, out int written) { written = 0; return HRESULT.E_NOTIMPL; }
        public HRESULT ClearCurrentException(int threadID) => HRESULT.E_NOTIMPL;
        public HRESULT EnableLogMessages(bool fOnOff) => HRESULT.E_NOTIMPL;
        public HRESULT ModifyLogSwitch(string pLogSwitchName, int lLevel) => HRESULT.E_NOTIMPL;
        public HRESULT EnumerateAppDomains(out ICorDebugAppDomainEnum ppAppDomains) { ppAppDomains = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetObject(out ICorDebugValue ppObject) { ppObject = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT ThreadForFiberCookie(int fiberCookie, out ICorDebugThread ppThread) { ppThread = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetHelperThreadID(out int pThreadID) { pThreadID = 0; return HRESULT.E_NOTIMPL; }
        public HRESULT Stop(int dwTimeoutIgnored) => HRESULT.S_OK;
        public HRESULT Continue(bool fIsOutOfBand) => HRESULT.S_OK;
        public HRESULT IsRunning(out bool pbRunning) { pbRunning = false; return HRESULT.E_NOTIMPL; }
        public HRESULT HasQueuedCallbacks(ICorDebugThread? pThread, out bool pbQueued) { pbQueued = false; return HRESULT.E_NOTIMPL; }
        public HRESULT EnumerateThreads(out ICorDebugThreadEnum ppThreads) { ppThreads = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT SetAllThreadsDebugState(CorDebugThreadState state, ICorDebugThread? pExceptThisThread) => HRESULT.E_NOTIMPL;
        public HRESULT Detach() => HRESULT.S_OK;
        public HRESULT Terminate(int exitCode) => HRESULT.S_OK;
        public HRESULT CanCommitChanges(int cSnapshots, ref ICorDebugEditAndContinueSnapshot pSnapshots, out ICorDebugErrorInfoEnum pError) { pError = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT CommitChanges(int cSnapshots, ref ICorDebugEditAndContinueSnapshot pSnapshots, out ICorDebugErrorInfoEnum pError) { pError = null!; return HRESULT.E_NOTIMPL; }
    }
}
