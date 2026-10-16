namespace DnD.Host.Tests;

using System.Text;
using DnD.Core.Symbols;
using DnD.Protocol;

/// <summary>
/// Breakpoints in .NET Framework modules whose symbols are a Windows (full) PDB.
/// The ClassicPdbTest fixture is built with DebugType=full, so symbol loading goes
/// through ClassicPdbSymbolReader instead of PortablePdbSymbolReader.
/// </summary>
[Collection("DebugSession")]
[Trait("Category", "Net48")]
public class ClassicPdbTests : DebugTestBase
{
    protected override string TargetFramework => "net48";

    /// <summary>
    /// Precondition guard: without a Windows PDB, the breakpoint test would pass via
    /// the portable PDB path and no longer cover ClassicPdbSymbolReader.
    /// </summary>
    [Fact]
    public void ClassicPdbTest_FixtureHasWindowsPdb()
    {
        var program = FindFixture("ClassicPdbTest");
        var pdbPath = Path.ChangeExtension(program, ".pdb");

        var signature = Encoding.ASCII.GetBytes("Microsoft C/C++ MSF 7.00");
        var header = new byte[signature.Length];
        using (var stream = File.OpenRead(pdbPath))
        {
            stream.ReadExactly(header);
        }

        Assert.Equal(signature, header);
    }

    [Fact]
    public void ClassicPdbTest_ClassicPdbSymbolReader_ResolvesBreakpoint()
    {
        var program = FindFixture("ClassicPdbTest");
        var sourceFile = FindFixtureSrc("ClassicPdbTest", "Program.cs");

        var reader = new ClassicPdbSymbolReader(program);
        var sp = reader.ResolveBreakpoint(sourceFile, 7);

        Assert.NotNull(sp);
        Assert.Equal(7, sp.StartLine);
    }

    [Fact]
    public async Task ClassicPdbTest_SetBreakpointAndHit()
    {
        var program = FindFixture("ClassicPdbTest");
        var sourceFile = FindFixtureSrc("ClassicPdbTest", "Program.cs");
        var rpc = Rpc ?? throw new InvalidOperationException("JSON-RPC connection is not initialized");

        await rpc.InvokeWithParameterObjectAsync<SetBreakpointResponse>(
            "setBreakpoint", new SetBreakpointRequest(File: sourceFile, Line: 7));

        await rpc.InvokeWithParameterObjectAsync<LaunchResponse>(
            "launch", new LaunchRequest(Program: program));

        var stopped = WaitForStopped();
        Assert.Equal(StopReason.Breakpoint, stopped.Reason);
    }
}
