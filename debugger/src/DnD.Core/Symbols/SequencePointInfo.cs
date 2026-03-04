namespace DnD.Core.Symbols;

public record SequencePointInfo(
    int MethodToken,
    int ILOffset,
    string FilePath,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn);
