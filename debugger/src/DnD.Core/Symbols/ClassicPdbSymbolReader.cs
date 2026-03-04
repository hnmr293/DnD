namespace DnD.Core.Symbols;

using System.Runtime.InteropServices;
using ClrDebug;

public class ClassicPdbSymbolReader : ISymbolReader
{
    private readonly SymUnmanagedReader _reader;

    // CLSID for CorSymBinder_SxS
    private static readonly Guid CLSID_CorSymBinder =
        new("0A29FF9E-7F9C-4437-8B11-F424491E3931");

    public ClassicPdbSymbolReader(string modulePath)
    {
        var pdbPath = Path.ChangeExtension(modulePath, ".pdb");
        if (!File.Exists(pdbPath))
            throw new FileNotFoundException($"PDB not found: {pdbPath}");

        // Create the metadata dispenser and open the module's metadata
        var dispenser = new MetaDataDispenserEx();
        var riid = typeof(IMetaDataImport).GUID;
        var importObj = dispenser.OpenScope(modulePath, CorOpenFlags.ofRead, riid);
        var rawImport = (IMetaDataImport)importObj;

        // Create the symbol binder via COM activation
        var binderType = Type.GetTypeFromCLSID(CLSID_CorSymBinder, throwOnError: true)!;
        var rawBinder = (ISymUnmanagedBinder)Activator.CreateInstance(binderType)!;
        var binder = new SymUnmanagedBinder(rawBinder);

        _reader = binder.GetReaderForFile(rawImport, modulePath,
            Path.GetDirectoryName(modulePath));
    }

    public SequencePointInfo? ResolveBreakpoint(string filePath, int line)
    {
        var normalizedPath = NormalizePath(filePath);

        foreach (var doc in _reader.Documents)
        {
            var docUrl = doc.URL;
            if (!string.Equals(NormalizePath(docUrl), normalizedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var methods = _reader.GetMethodsFromDocumentPosition(doc.Raw, line, 0);
                if (methods.Length == 0) continue;

                var method = methods[0];
                var sequencePoints = GetMethodSequencePoints(method);

                SequencePointInfo? bestMatch = null;
                int bestDistance = int.MaxValue;

                foreach (var sp in sequencePoints)
                {
                    var distance = Math.Abs(sp.StartLine - line);
                    if (sp.StartLine >= line && distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestMatch = sp;
                        if (distance == 0) return bestMatch;
                    }
                }

                return bestMatch;
            }
            catch { continue; }
        }

        return null;
    }

    public SequencePointInfo? ResolveSourceLocation(int methodToken, int ilOffset)
    {
        try
        {
            var method = _reader.GetMethod((mdMethodDef)methodToken);
            var sequencePoints = GetMethodSequencePoints(method);

            SequencePointInfo? bestMatch = null;
            int bestOffset = -1;

            foreach (var sp in sequencePoints)
            {
                if (sp.ILOffset <= ilOffset && sp.ILOffset > bestOffset)
                {
                    bestOffset = sp.ILOffset;
                    bestMatch = sp;
                }
            }

            return bestMatch;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyList<SequencePointInfo> GetSequencePoints(int methodToken)
    {
        try
        {
            var method = _reader.GetMethod((mdMethodDef)methodToken);
            return GetMethodSequencePoints(method);
        }
        catch
        {
            return [];
        }
    }

    public IReadOnlyList<LocalVariableInfo> GetLocalVariables(int methodToken, int ilOffset)
    {
        var result = new List<LocalVariableInfo>();

        try
        {
            var method = _reader.GetMethod((mdMethodDef)methodToken);
            var rootScope = method.RootScope;
            CollectLocals(rootScope, ilOffset, result);
        }
        catch { }

        return result;
    }

    private void CollectLocals(SymUnmanagedScope scope, int ilOffset, List<LocalVariableInfo> result)
    {
        if (ilOffset < scope.StartOffset || ilOffset >= scope.EndOffset)
            return;

        foreach (var local in scope.Locals)
        {
            result.Add(new LocalVariableInfo(
                Name: local.Name,
                SlotIndex: (int)local.AddressField1));
        }

        foreach (var child in scope.Children)
        {
            CollectLocals(child, ilOffset, result);
        }
    }

    private List<SequencePointInfo> GetMethodSequencePoints(SymUnmanagedMethod method)
    {
        var result = new List<SequencePointInfo>();
        var count = method.SequencePointCount;
        if (count == 0) return result;

        var sps = method.GetSequencePoints(count);
        var token = (int)method.Token;

        for (int i = 0; i < sps.offsets.Length; i++)
        {
            if (sps.lines[i] == 0xFEEFEE) continue; // Hidden sequence point

            var doc = new SymUnmanagedDocument(sps.documents[i]);
            result.Add(new SequencePointInfo(
                MethodToken: token,
                ILOffset: sps.offsets[i],
                FilePath: doc.URL,
                StartLine: sps.lines[i],
                StartColumn: sps.columns[i],
                EndLine: sps.endLines[i],
                EndColumn: sps.endColumns[i]));
        }

        return result;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('/', '\\');
    }

    public void Dispose()
    {
        // COM-based, released by GC/ref counting
    }
}
