namespace CrxMem.MemoryView.Wpf.Services;

public sealed record NavigationEntry(
    ulong Address,
    string Expression,
    ulong? SelectedAddress = null,
    double VerticalOffset = 0);

public interface INavigationHistoryService
{
    bool CanGoBack { get; }
    bool CanGoForward { get; }
    NavigationEntry? Current { get; }
    void Push(NavigationEntry entry);
    NavigationEntry? GoBack();
    NavigationEntry? GoForward();
    void Clear();
}

public sealed class NavigationHistoryService : INavigationHistoryService
{
    private readonly int _capacity;
    private readonly List<NavigationEntry> _entries = new();
    private int _index = -1;

    public NavigationHistoryService(int capacity = 100)
    {
        if (capacity < 2) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;
    public NavigationEntry? Current => _index >= 0 ? _entries[_index] : null;

    public void Push(NavigationEntry entry)
    {
        if (Current?.Address == entry.Address)
        {
            _entries[_index] = entry;
            return;
        }

        if (CanGoForward) _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(entry);
        if (_entries.Count > _capacity) _entries.RemoveAt(0);
        _index = _entries.Count - 1;
    }

    public NavigationEntry? GoBack()
    {
        if (!CanGoBack) return null;
        return _entries[--_index];
    }

    public NavigationEntry? GoForward()
    {
        if (!CanGoForward) return null;
        return _entries[++_index];
    }

    public void Clear()
    {
        _entries.Clear();
        _index = -1;
    }
}
