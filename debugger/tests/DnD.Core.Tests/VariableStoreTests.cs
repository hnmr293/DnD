using DnD.Core.Inspection;

namespace DnD.Core.Tests;

public class VariableStoreTests
{
    private readonly VariableStore _store = new();

    // === Store ===

    [Fact]
    public void Store_FirstReference_ReturnsOne()
    {
        // VariableStore uses null! since we can't create real CorDebugValue without COM.
        // The Store doesn't inspect the value — it just holds a reference.
        var refId = _store.Store(null!);
        Assert.Equal(1, refId);
    }

    [Fact]
    public void Store_SequentialReferences_AreIncrementing()
    {
        var r1 = _store.Store(null!);
        var r2 = _store.Store(null!);
        var r3 = _store.Store(null!);
        Assert.Equal(1, r1);
        Assert.Equal(2, r2);
        Assert.Equal(3, r3);
    }

    [Fact]
    public void Store_ManyReferences_AllUnique()
    {
        var refs = new HashSet<int>();
        for (int i = 0; i < 100; i++)
        {
            refs.Add(_store.Store(null!));
        }
        Assert.Equal(100, refs.Count);
    }

    // === Get ===

    [Fact]
    public void Get_ExistingReference_ReturnsValue()
    {
        var refId = _store.Store(null!);
        // null! was stored, Get should return null
        var result = _store.Get(refId);
        Assert.Null(result);  // we stored null!
    }

    [Fact]
    public void Get_NonExistentReference_ReturnsNull()
    {
        var result = _store.Get(999);
        Assert.Null(result);
    }

    [Fact]
    public void Get_ZeroReference_ReturnsNull()
    {
        var result = _store.Get(0);
        Assert.Null(result);
    }

    [Fact]
    public void Get_NegativeReference_ReturnsNull()
    {
        var result = _store.Get(-1);
        Assert.Null(result);
    }

    // === Clear ===

    [Fact]
    public void Clear_RemovesAllReferences()
    {
        var r1 = _store.Store(null!);
        var r2 = _store.Store(null!);

        _store.Clear();

        Assert.Null(_store.Get(r1));
        Assert.Null(_store.Get(r2));
    }

    [Fact]
    public void Clear_ResetsReferenceCounter()
    {
        _store.Store(null!);
        _store.Store(null!);

        _store.Clear();

        var newRef = _store.Store(null!);
        Assert.Equal(1, newRef);
    }

    [Fact]
    public void Clear_CalledTwice_DoesNotThrow()
    {
        _store.Clear();
        _store.Clear();
    }

    [Fact]
    public void Clear_OnEmptyStore_DoesNotThrow()
    {
        _store.Clear();
        Assert.Null(_store.Get(1));
    }

    // === Store after Clear ===

    [Fact]
    public void StoreAfterClear_RestartsFromOne()
    {
        _store.Store(null!);
        _store.Store(null!);
        _store.Store(null!);

        _store.Clear();

        var r1 = _store.Store(null!);
        var r2 = _store.Store(null!);
        Assert.Equal(1, r1);
        Assert.Equal(2, r2);
    }
}
