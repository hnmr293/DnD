namespace DnD.Core.Tests;

using ClrDebug;
using DnD.Core.Runtime;
using DnD.Protocol;

/// <summary>
/// Deterministic tests for the race condition between Launch() and _session assignment.
/// Module loads fired during Launch() must be buffered and replayed after _session is set.
/// </summary>
public class DebuggerEngineRaceConditionTests : IDisposable
{
    // Use the test assembly's own DLL as a file that definitely exists
    private static readonly string ExistingFile =
        typeof(DebuggerEngineRaceConditionTests).Assembly.Location;

    private readonly DebuggerEngine _engine;
    private readonly ModuleLoadDuringLaunchLauncher _launcher;

    public DebuggerEngineRaceConditionTests()
    {
        _launcher = new ModuleLoadDuringLaunchLauncher();
        _engine = new DebuggerEngine(_launcher);
    }

    public void Dispose() => _engine.Dispose();

    /// <summary>
    /// Verifies that a module loaded during Launch() (before _session assignment)
    /// is properly buffered and replayed into session.Modules.
    ///
    /// Without the fix: OnModuleLoaded fires during Launch(), _session is null,
    /// handler returns early → module is lost.
    /// With the fix: module is buffered, replayed after _session assignment → registered.
    /// </summary>
    [Fact]
    public async Task ModuleLoadDuringLaunch_IsBufferedAndReplayed()
    {
        const string moduleName = @"C:\test\TestModule.dll";
        _launcher.ModulesToLoadDuringLaunch.Add(moduleName);

        await _engine.LaunchAsync(new LaunchRequest(Program: ExistingFile));

        var session = ((ISessionContext)_engine).GetSession();
        Assert.True(session.Modules.ContainsKey(moduleName),
            "Module loaded during Launch() should be registered in session.Modules");
    }

    /// <summary>
    /// Verifies that multiple modules loaded during Launch() are all buffered and replayed.
    /// Simulates corlib + framework + user modules loading before _session is set.
    /// </summary>
    [Fact]
    public async Task MultipleModuleLoadsDuringLaunch_AllBufferedAndReplayed()
    {
        var moduleNames = new[]
        {
            @"C:\dotnet\corlib.dll",
            @"C:\dotnet\System.Runtime.dll",
            @"C:\app\UserApp.dll",
        };
        foreach (var name in moduleNames)
            _launcher.ModulesToLoadDuringLaunch.Add(name);

        await _engine.LaunchAsync(new LaunchRequest(Program: ExistingFile));

        var session = ((ISessionContext)_engine).GetSession();
        foreach (var name in moduleNames)
            Assert.True(session.Modules.ContainsKey(name),
                $"Module '{name}' loaded during Launch() should be registered");
    }

    /// <summary>
    /// Verifies that pending breakpoints are resolved for modules loaded during Launch().
    ///
    /// Without the fix: module dropped → _breakpointManager.OnModuleLoaded never called
    /// → breakpoint stays Pending.
    /// With the fix: module replayed → OnModuleLoaded called → breakpoint resolution attempted.
    /// </summary>
    [Fact]
    public async Task PendingBreakpoint_ResolvedByBufferedModuleLoad()
    {
        const string moduleName = @"C:\test\TestModule.dll";
        _launcher.ModulesToLoadDuringLaunch.Add(moduleName);

        // Set a breakpoint before launch — it becomes pending
        await _engine.SetBreakpointAsync(
            new SetBreakpointRequest(File: @"C:\test\Program.cs", Line: 10));

        await _engine.LaunchAsync(new LaunchRequest(Program: ExistingFile));

        // Verify OnModuleLoaded was called for the buffered module
        // (breakpoint won't verify without a real PDB, but the module must be registered)
        var session = ((ISessionContext)_engine).GetSession();
        Assert.True(session.Modules.ContainsKey(moduleName));
    }

    // ── Mock IProcessLauncher ───────────────────────────────────────────

    private class ModuleLoadDuringLaunchLauncher : IProcessLauncher
    {
        public List<string> ModulesToLoadDuringLaunch { get; } = new();

        public LaunchResult Launch(
            string program, string[]? args, string? cwd,
            Dictionary<string, string>? env,
            CorDebugManagedCallback callback)
        {
            // Fire LoadModule for each module DURING Launch(), before _session is assigned.
            // This deterministically reproduces the race condition.
            var mcb = (ICorDebugManagedCallback)callback;
            var mockAD = new MockCorDebugAppDomain();
            foreach (var name in ModulesToLoadDuringLaunch)
                mcb.LoadModule(mockAD, new MockCorDebugModule(name));

            return new LaunchResult(
                new CorDebug(new MockCorDebug()),
                new CorDebugProcess(new MockCorDebugProcess()));
        }

        public LaunchResult Attach(int processId, CorDebugManagedCallback callback)
            => throw new NotImplementedException();
    }

    // ── Mock COM implementations ────────────────────────────────────────

    private class MockCorDebugModule : ICorDebugModule
    {
        private readonly string _name;
        public MockCorDebugModule(string name) => _name = name;

        public HRESULT GetName(int cchName, out int pcchName, char[]? szName)
        {
            pcchName = _name.Length + 1;
            if (szName != null)
                _name.AsSpan().CopyTo(szName.AsSpan(0, Math.Min(_name.Length, szName.Length)));
            return HRESULT.S_OK;
        }

        public HRESULT GetProcess(out ICorDebugProcess ppProcess) { ppProcess = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetBaseAddress(out CORDB_ADDRESS pAddress) { pAddress = default; return HRESULT.E_NOTIMPL; }
        public HRESULT GetAssembly(out ICorDebugAssembly ppAssembly) { ppAssembly = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT EnableJITDebugging(bool bTrackJITInfo, bool bAllowJitOpts) => HRESULT.E_NOTIMPL;
        public HRESULT EnableClassLoadCallbacks(bool bClassLoadCallbacks) => HRESULT.E_NOTIMPL;
        public HRESULT GetFunctionFromToken(mdMethodDef methodDef, out ICorDebugFunction ppFunction) { ppFunction = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetFunctionFromRVA(long rva, out ICorDebugFunction ppFunction) { ppFunction = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetClassFromToken(mdTypeDef typeDef, out ICorDebugClass ppClass) { ppClass = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT CreateBreakpoint(out ICorDebugModuleBreakpoint ppBreakpoint) { ppBreakpoint = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetEditAndContinueSnapshot(out ICorDebugEditAndContinueSnapshot ppEditAndContinueSnapshot) { ppEditAndContinueSnapshot = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetMetaDataInterface(in Guid riid, out object ppObj) { ppObj = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetToken(out mdModule pToken) { pToken = default; return HRESULT.E_NOTIMPL; }
        public HRESULT IsDynamic(out int pDynamic) { pDynamic = 0; return HRESULT.E_NOTIMPL; }
        public HRESULT GetGlobalVariableValue(mdFieldDef fieldDef, out ICorDebugValue ppValue) { ppValue = null!; return HRESULT.E_NOTIMPL; }
        public HRESULT GetSize(out int pcBytes) { pcBytes = 0; return HRESULT.E_NOTIMPL; }
        public HRESULT IsInMemory(out int pInMemory) { pInMemory = 0; return HRESULT.E_NOTIMPL; }
    }

    private class MockCorDebugAppDomain : ICorDebugAppDomain
    {
        // ICorDebugAppDomain
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
        // ICorDebugController
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
        // ICorDebugProcess
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
        // ICorDebugController
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
