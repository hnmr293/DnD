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

    public SetBreakpointResponse SetBreakpoint(string file, int line)
    {
        var normalizedFile = file.Replace('/', '\\');

        foreach (var (moduleName, (module, reader)) in _modules)
        {
            if (reader == null) continue;

            var sp = reader.ResolveBreakpoint(normalizedFile, line);
            if (sp == null) continue;

            var bp = CreateBreakpoint(module, sp, file, line);
            if (bp != null) return bp;
        }

        var id = _nextId++;
        _pending.Add(new PendingBreakpoint(id, file, line));
        return new SetBreakpointResponse(
            new Breakpoint(id, file, line, Verified: false));
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
            bps.Add(new Breakpoint(id, entry.File, entry.Line, Verified: true));
        foreach (var pending in _pending)
            bps.Add(new Breakpoint(pending.Id, pending.File, pending.Line, Verified: false));
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
                    pending.File, sp.StartLine, corBp);
                resolved.Add(pending);
            }
            catch { }
        }
        foreach (var r in resolved)
            _pending.Remove(r);
    }

    private SetBreakpointResponse? CreateBreakpoint(CorDebugModule module, SequencePointInfo sp, string file, int line)
    {
        try
        {
            var function = module.GetFunctionFromToken((mdMethodDef)sp.MethodToken);
            var code = function.ILCode;
            var corBp = code.CreateBreakpoint(sp.ILOffset);
            corBp.Activate(true);

            var id = _nextId++;
            _breakpoints[id] = new BreakpointEntry(file, sp.StartLine, corBp);
            return new SetBreakpointResponse(
                new Breakpoint(id, file, sp.StartLine, Verified: true));
        }
        catch { return null; }
    }

    /// <summary>
    /// Finds the user-assigned breakpoint ID for a given ICorDebug breakpoint
    /// by comparing COM RCW identity (casting to object gives the same RCW instance
    /// for the same underlying COM object, regardless of interface type).
    /// </summary>
    public int? FindBreakpointId(CorDebugBreakpoint breakpoint)
    {
        foreach (var (id, entry) in _breakpoints)
        {
            try
            {
                if (ReferenceEquals((object)entry.CorBreakpoint.Raw, (object)breakpoint.Raw))
                    return id;
            }
            catch { }
        }
        return null;
    }

    private record BreakpointEntry(string File, int Line, CorDebugFunctionBreakpoint CorBreakpoint);
    private record PendingBreakpoint(int Id, string File, int Line);
}
