namespace DnD.Core.Symbols;

public static class SymbolReaderFactory
{
    public static ISymbolReader? Create(string modulePath)
    {
        // Try Portable PDB first
        try
        {
            var reader = new PortablePdbSymbolReader(modulePath);
            return reader;
        }
        catch { }

        // Try Classic PDB
        try
        {
            var reader = new ClassicPdbSymbolReader(modulePath);
            return reader;
        }
        catch { }

        return null;
    }
}
