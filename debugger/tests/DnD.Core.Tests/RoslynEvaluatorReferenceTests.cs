namespace DnD.Core.Tests;

using DnD.Core.Inspection;
using Microsoft.CodeAnalysis;

public class RoslynEvaluatorReferenceTests : IDisposable
{
    private readonly string _tempDir;

    public RoslynEvaluatorReferenceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DnDRefTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    /// <summary>
    /// Copies this test assembly to the given subdirectory, producing a DLL
    /// whose simple name equals this test assembly's simple name.
    /// </summary>
    private string CopySelfTo(string subDir)
    {
        var selfPath = typeof(RoslynEvaluatorReferenceTests).Assembly.Location;
        var dir = Path.Combine(_tempDir, subDir);
        Directory.CreateDirectory(dir);
        var copyPath = Path.Combine(dir, Path.GetFileName(selfPath));
        File.Copy(selfPath, copyPath);
        return copyPath;
    }

    private static string? FilePathOf(MetadataReference reference)
        => (reference as PortableExecutableReference)?.FilePath;

    [Fact]
    public void BuildReferences_DuplicateSimpleNames_KeepsOnlyFirstCopy()
    {
        var copyA = CopySelfTo("PluginA");
        var copyB = CopySelfTo("PluginB");

        var (references, assemblyNames) = RoslynEvaluator.BuildReferences(
            [copyA, copyB], frameModulePath: null, runtimeDir: null);

        var reference = Assert.Single(references);
        Assert.Equal(copyA, FilePathOf(reference));
        Assert.Single(assemblyNames);
    }

    [Fact]
    public void BuildReferences_DuplicateSimpleNames_FrameModuleWins()
    {
        var copyA = CopySelfTo("PluginA");
        var copyB = CopySelfTo("PluginB");

        var (references, assemblyNames) = RoslynEvaluator.BuildReferences(
            [copyA, copyB], frameModulePath: copyB, runtimeDir: null);

        var reference = Assert.Single(references);
        Assert.Equal(copyB, FilePathOf(reference));
        Assert.Single(assemblyNames);
    }

    [Fact]
    public void BuildReferences_DistinctSimpleNames_KeepsAll()
    {
        var selfPath = typeof(RoslynEvaluatorReferenceTests).Assembly.Location;
        var corePath = typeof(RoslynEvaluator).Assembly.Location;

        var (references, assemblyNames) = RoslynEvaluator.BuildReferences(
            [selfPath, corePath], frameModulePath: null, runtimeDir: null);

        Assert.Equal(2, references.Count);
        Assert.Equal(2, assemblyNames.Count);
    }

    [Fact]
    public void BuildReferences_RuntimeDirDuplicatesLoadedModule_LoadedModuleWins()
    {
        var loaded = CopySelfTo("Loaded");
        var runtimeCopy = CopySelfTo("RuntimeDir");
        var runtimeDir = Path.GetDirectoryName(runtimeCopy);

        var (references, _) = RoslynEvaluator.BuildReferences(
            [loaded], frameModulePath: null, runtimeDir: runtimeDir);

        var reference = Assert.Single(references);
        Assert.Equal(loaded, FilePathOf(reference));
    }

    [Fact]
    public void BuildReferences_SamePathListedTwice_AddedOnce()
    {
        var copyA = CopySelfTo("PluginA");

        var (references, assemblyNames) = RoslynEvaluator.BuildReferences(
            [copyA, copyA], frameModulePath: null, runtimeDir: null);

        Assert.Single(references);
        Assert.Single(assemblyNames);
    }
}
