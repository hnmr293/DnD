namespace DnD.Core.Symbols;

public interface ISymbolReader : IDisposable
{
    SequencePointInfo? ResolveBreakpoint(string filePath, int line);

    SequencePointInfo? ResolveSourceLocation(int methodToken, int ilOffset);

    IReadOnlyList<SequencePointInfo> GetSequencePoints(int methodToken);

    IReadOnlyList<LocalVariableInfo> GetLocalVariables(int methodToken, int ilOffset);
}
