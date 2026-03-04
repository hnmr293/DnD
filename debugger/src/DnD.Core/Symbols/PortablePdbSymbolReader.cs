namespace DnD.Core.Symbols;

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

public class PortablePdbSymbolReader : ISymbolReader
{
    private readonly MetadataReaderProvider _pdbReaderProvider;
    private readonly MetadataReader _pdbReader;
    private readonly PEReader _peReader;

    public PortablePdbSymbolReader(string modulePath)
    {
        _peReader = new PEReader(File.OpenRead(modulePath));

        // Try embedded PDB first
        var embeddedEntries = _peReader.ReadDebugDirectory()
            .Where(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
            .ToList();

        if (embeddedEntries.Count > 0)
        {
            _pdbReaderProvider = _peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedEntries[0]);
            _pdbReader = _pdbReaderProvider.GetMetadataReader();
            return;
        }

        // Try external PDB
        var pdbPath = Path.ChangeExtension(modulePath, ".pdb");
        if (!File.Exists(pdbPath))
            throw new FileNotFoundException($"PDB not found: {pdbPath}");

        _pdbReaderProvider = MetadataReaderProvider.FromPortablePdbStream(File.OpenRead(pdbPath));
        _pdbReader = _pdbReaderProvider.GetMetadataReader();
    }

    public SequencePointInfo? ResolveBreakpoint(string filePath, int line)
    {
        var normalizedPath = NormalizePath(filePath);
        SequencePointInfo? bestMatch = null;
        int bestDistance = int.MaxValue;

        foreach (var methodDebugInfoHandle in _pdbReader.MethodDebugInformation)
        {
            var methodDebugInfo = _pdbReader.GetMethodDebugInformation(methodDebugInfoHandle);
            if (methodDebugInfo.SequencePointsBlob.IsNil) continue;

            foreach (var sp in methodDebugInfo.GetSequencePoints())
            {
                if (sp.IsHidden) continue;

                var doc = _pdbReader.GetDocument(sp.Document);
                var docName = _pdbReader.GetString(doc.Name);
                var normalizedDoc = NormalizePath(docName);

                if (!string.Equals(normalizedDoc, normalizedPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                var distance = Math.Abs(sp.StartLine - line);
                if (sp.StartLine >= line && distance < bestDistance)
                {
                    bestDistance = distance;
                    var methodDef = MetadataTokens.MethodDefinitionHandle(
                        MetadataTokens.GetRowNumber(methodDebugInfoHandle));
                    bestMatch = new SequencePointInfo(
                        MethodToken: MetadataTokens.GetToken(methodDef),
                        ILOffset: sp.Offset,
                        FilePath: docName,
                        StartLine: sp.StartLine,
                        StartColumn: sp.StartColumn,
                        EndLine: sp.EndLine,
                        EndColumn: sp.EndColumn);

                    if (distance == 0) return bestMatch;
                }
            }
        }

        return bestMatch;
    }

    public SequencePointInfo? ResolveSourceLocation(int methodToken, int ilOffset)
    {
        var handle = MetadataTokens.MethodDefinitionHandle(
            MetadataTokens.GetRowNumber(MetadataTokens.EntityHandle(methodToken)));
        var debugInfoHandle = ((MethodDefinitionHandle)handle).ToDebugInformationHandle();

        try
        {
            var debugInfo = _pdbReader.GetMethodDebugInformation(debugInfoHandle);
            if (debugInfo.SequencePointsBlob.IsNil) return null;

            SequencePointInfo? bestMatch = null;
            int bestOffset = -1;

            foreach (var sp in debugInfo.GetSequencePoints())
            {
                if (sp.IsHidden) continue;

                if (sp.Offset <= ilOffset && sp.Offset > bestOffset)
                {
                    bestOffset = sp.Offset;
                    var doc = _pdbReader.GetDocument(sp.Document);
                    bestMatch = new SequencePointInfo(
                        MethodToken: methodToken,
                        ILOffset: sp.Offset,
                        FilePath: _pdbReader.GetString(doc.Name),
                        StartLine: sp.StartLine,
                        StartColumn: sp.StartColumn,
                        EndLine: sp.EndLine,
                        EndColumn: sp.EndColumn);
                }
            }

            return bestMatch;
        }
        catch { return null; }
    }

    public IReadOnlyList<SequencePointInfo> GetSequencePoints(int methodToken)
    {
        var result = new List<SequencePointInfo>();

        try
        {
            var handle = MetadataTokens.MethodDefinitionHandle(
                MetadataTokens.GetRowNumber(MetadataTokens.EntityHandle(methodToken)));
            var debugInfoHandle = ((MethodDefinitionHandle)handle).ToDebugInformationHandle();
            var debugInfo = _pdbReader.GetMethodDebugInformation(debugInfoHandle);

            if (debugInfo.SequencePointsBlob.IsNil) return result;

            foreach (var sp in debugInfo.GetSequencePoints())
            {
                if (sp.IsHidden) continue;

                var doc = _pdbReader.GetDocument(sp.Document);
                result.Add(new SequencePointInfo(
                    MethodToken: methodToken,
                    ILOffset: sp.Offset,
                    FilePath: _pdbReader.GetString(doc.Name),
                    StartLine: sp.StartLine,
                    StartColumn: sp.StartColumn,
                    EndLine: sp.EndLine,
                    EndColumn: sp.EndColumn));
            }
        }
        catch { }

        return result;
    }

    public IReadOnlyList<LocalVariableInfo> GetLocalVariables(int methodToken, int ilOffset)
    {
        var result = new List<LocalVariableInfo>();

        try
        {
            var handle = MetadataTokens.MethodDefinitionHandle(
                MetadataTokens.GetRowNumber(MetadataTokens.EntityHandle(methodToken)));
            var debugInfoHandle = ((MethodDefinitionHandle)handle).ToDebugInformationHandle();

            foreach (var scopeHandle in _pdbReader.GetLocalScopes(debugInfoHandle))
            {
                var scope = _pdbReader.GetLocalScope(scopeHandle);
                if (ilOffset < scope.StartOffset || ilOffset >= scope.EndOffset)
                    continue;

                foreach (var varHandle in scope.GetLocalVariables())
                {
                    var variable = _pdbReader.GetLocalVariable(varHandle);
                    if (variable.Attributes.HasFlag(LocalVariableAttributes.DebuggerHidden))
                        continue;

                    result.Add(new LocalVariableInfo(
                        Name: _pdbReader.GetString(variable.Name),
                        SlotIndex: variable.Index));
                }
            }
        }
        catch { }

        return result;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('/', '\\');
    }

    public void Dispose()
    {
        _pdbReaderProvider?.Dispose();
        _peReader?.Dispose();
    }
}
