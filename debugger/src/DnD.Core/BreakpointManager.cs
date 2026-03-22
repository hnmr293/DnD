namespace DnD.Core;

using ClrDebug;
using DnD.Core.Symbols;
using DnD.Protocol;

public class BreakpointManager
{
    private int _nextId = 1;
    private readonly Dictionary<int, BreakpointEntry> _breakpoints = new();
    private readonly List<PendingBreakpoint> _pending = new();
    private readonly Dictionary<string, (CorDebugModule Module, ISymbolReader? Reader)> _modules;

    public BreakpointManager(Dictionary<string, (CorDebugModule Module, ISymbolReader? Reader)> modules)
    {
        _modules = modules;
    }

    public SetBreakpointResponse SetBreakpoint(string file, int line,
        string? condition = null, int? hitCount = null)
    {
        var normalizedFile = file.Replace('/', '\\');

        foreach (var (moduleName, (module, reader)) in _modules)
        {
            if (reader == null) continue;

            var sp = reader.ResolveBreakpoint(normalizedFile, line);
            if (sp == null) continue;

            var bp = CreateBreakpoint(module, sp, file, line, condition, hitCount);
            if (bp != null) return bp;
        }

        var id = _nextId++;
        _pending.Add(new PendingBreakpoint(id, file, line, condition, hitCount));
        return new SetBreakpointResponse(
            new Breakpoint(id, file, line, Verified: false,
                Condition: condition, HitCount: hitCount));
    }

    /// <summary>
    /// Deactivate all breakpoints without removing them from tracking.
    /// Used before detach to ensure the debuggee can run cleanly.
    /// </summary>
    public void DeactivateAll()
    {
        foreach (var (_, entry) in _breakpoints)
        {
            try { entry.CorBreakpoint.Activate(false); }
            catch { }
        }
    }

    public void RemoveBreakpoint(int breakpointId)
    {
        if (_breakpoints.TryGetValue(breakpointId, out var entry))
        {
            try { entry.CorBreakpoint.Activate(false); }
            catch { }
            _breakpoints.Remove(breakpointId);
            return;
        }
        _pending.RemoveAll(p => p.Id == breakpointId);
    }

    public GetBreakpointsResponse GetBreakpoints()
    {
        var bps = new List<Breakpoint>();
        foreach (var (id, entry) in _breakpoints)
            bps.Add(new Breakpoint(id, entry.File, entry.Line, Verified: true,
                Condition: entry.Condition, HitCount: entry.HitCount));
        foreach (var pending in _pending)
            bps.Add(new Breakpoint(pending.Id, pending.File, pending.Line, Verified: false,
                Condition: pending.Condition, HitCount: pending.HitCount));
        return new GetBreakpointsResponse(bps.ToArray());
    }

    public void OnModuleLoaded(string moduleName, CorDebugModule module, ISymbolReader? reader)
    {
        if (reader == null) return;

        var resolved = new List<PendingBreakpoint>();
        foreach (var pending in _pending)
        {
            var normalizedFile = pending.File.Replace('/', '\\');
            var sp = reader.ResolveBreakpoint(normalizedFile, pending.Line);
            if (sp == null) continue;

            try
            {
                var function = module.GetFunctionFromToken((mdMethodDef)sp.MethodToken);
                var code = function.ILCode;
                var corBp = code.CreateBreakpoint(sp.ILOffset);
                corBp.Activate(true);

                _breakpoints[pending.Id] = new BreakpointEntry(
                    pending.File, sp.StartLine, corBp,
                    pending.Condition, pending.HitCount);
                resolved.Add(pending);
            }
            catch { }
        }
        foreach (var r in resolved)
            _pending.Remove(r);
    }

    private SetBreakpointResponse? CreateBreakpoint(CorDebugModule module, SequencePointInfo sp,
        string file, int line, string? condition = null, int? hitCount = null)
    {
        try
        {
            var function = module.GetFunctionFromToken((mdMethodDef)sp.MethodToken);
            var code = function.ILCode;
            var corBp = code.CreateBreakpoint(sp.ILOffset);
            corBp.Activate(true);

            var id = _nextId++;
            _breakpoints[id] = new BreakpointEntry(file, sp.StartLine, corBp, condition, hitCount);
            return new SetBreakpointResponse(
                new Breakpoint(id, file, sp.StartLine, Verified: true,
                    Condition: condition, HitCount: hitCount));
        }
        catch { return null; }
    }

    /// <summary>
    /// Check if a breakpoint hit should stop execution.
    /// Increments hit counter and checks hit count threshold.
    /// Returns (shouldStop, breakpointId, condition).
    /// </summary>
    public (bool ShouldStop, int? BreakpointId, string? Condition) CheckBreakpointHit(
        CorDebugBreakpoint breakpoint)
    {
        foreach (var (id, entry) in _breakpoints)
        {
            try
            {
                if (!ReferenceEquals((object)entry.CorBreakpoint.Raw, (object)breakpoint.Raw))
                    continue;

                // Hit count check
                if (entry.HitCount.HasValue)
                {
                    entry.CurrentHits++;
                    if (entry.CurrentHits < entry.HitCount.Value)
                        return (false, id, null);
                }

                // Condition will be evaluated by DebuggerEngine
                return (true, id, entry.Condition);
            }
            catch { }
        }
        return (true, null, null);
    }

    private class BreakpointEntry
    {
        public string File { get; }
        public int Line { get; }
        public CorDebugFunctionBreakpoint CorBreakpoint { get; }
        public string? Condition { get; }
        public int? HitCount { get; }
        public int CurrentHits { get; set; }

        public BreakpointEntry(string file, int line, CorDebugFunctionBreakpoint corBreakpoint,
            string? condition = null, int? hitCount = null)
        {
            File = file;
            Line = line;
            CorBreakpoint = corBreakpoint;
            Condition = condition;
            HitCount = hitCount;
        }
    }

    private record PendingBreakpoint(int Id, string File, int Line,
        string? Condition = null, int? HitCount = null);
}
