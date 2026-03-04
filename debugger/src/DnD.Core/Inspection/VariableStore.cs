namespace DnD.Core.Inspection;

using ClrDebug;

public class VariableStore
{
    private int _nextRef = 1;
    private readonly Dictionary<int, CorDebugValue> _handles = new();

    public int Store(CorDebugValue value)
    {
        var reference = _nextRef++;
        _handles[reference] = value;
        return reference;
    }

    public CorDebugValue? Get(int reference)
    {
        return _handles.GetValueOrDefault(reference);
    }

    public void Clear()
    {
        _handles.Clear();
        _nextRef = 1;
    }
}
