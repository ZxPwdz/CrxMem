using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using CrxMem.Core;
using CrxMem.MemoryView.Wpf.Models;

namespace CrxMem.MemoryView.Wpf.Services;

public interface IHexMemorySource : IReadOnlyList<HexPreviewRow>, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable
{
    ulong BaseAddress { get; }
    ulong EndAddress { get; }
    ulong FocusAddress { get; }
    string Status { get; }
    int IndexOfAddress(ulong address);
    void Invalidate(ulong address, int length);
}

public interface IHexMemorySourceFactory
{
    IHexMemorySource Create(ulong address, CancellationToken cancellationToken = default);
}

public sealed class HexMemorySourceFactory : IHexMemorySourceFactory
{
    private readonly IProcessMemoryService _memory;
    public HexMemorySourceFactory(IProcessMemoryService memory) =>
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));

    public IHexMemorySource Create(ulong address, CancellationToken cancellationToken = default) =>
        new PagedHexMemorySource(_memory, address, cancellationToken: cancellationToken);
}

public sealed class PagedHexMemorySource : IHexMemorySource
{
    public const int BytesPerRow = 16;
    public const int PageSize = 4096;
    private readonly IProcessMemoryService _memory;
    private readonly int _maximumPages;
    private readonly SynchronizationContext? _context;
    private readonly CancellationTokenSource _lifetime;
    private readonly object _sync = new();
    private readonly Dictionary<ulong, PageEntry> _pages = new();
    private readonly Dictionary<(ulong Page, long Version), Task> _loads = new();
    private readonly Dictionary<ulong, long> _pageVersions = new();
    private readonly LinkedList<ulong> _lru = new();
    private bool _disposed;
    private string _status;

    public PagedHexMemorySource(
        IProcessMemoryService memory,
        ulong focusAddress,
        int maximumPages = 128,
        CancellationToken cancellationToken = default)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _maximumPages = Math.Clamp(maximumPages, 4, 1024);
        _context = SynchronizationContext.Current;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        FocusAddress = focusAddress;
        MemoryRegionDescriptor? region = memory.QueryRegion(focusAddress);
        if (region == null || region.Size == 0)
        {
            BaseAddress = focusAddress & ~0xFUL;
            EndAddress = BaseAddress + BytesPerRow;
            _status = "Memory region unavailable.";
        }
        else
        {
            BaseAddress = region.BaseAddress;
            EndAddress = region.BaseAddress > ulong.MaxValue - region.Size
                ? ulong.MaxValue
                : region.BaseAddress + region.Size;
            _status = (region.Protection & MemoryRegion.PAGE_GUARD) != 0
                ? "Guarded memory region."
                : $"Virtual hex view · {region.Size:N0} bytes · pages load on demand";
        }

        ulong rowCount = (EndAddress - BaseAddress + BytesPerRow - 1) / BytesPerRow;
        Count = checked((int)Math.Min((ulong)int.MaxValue, Math.Max(1UL, rowCount)));
    }

    public ulong BaseAddress { get; }
    public ulong EndAddress { get; }
    public ulong FocusAddress { get; }
    public int Count { get; }
    public string Status { get => _status; private set { if (_status == value) return; _status = value; RaisePropertyChanged(nameof(Status)); } }
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public HexPreviewRow this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            ulong address = BaseAddress + checked((ulong)index * BytesPerRow);
            ulong pageAddress = address & ~((ulong)PageSize - 1);
            PageEntry? page;
            lock (_sync)
            {
                _pages.TryGetValue(pageAddress, out page);
                if (page != null) Touch(pageAddress);
                else QueueLoad(pageAddress);
            }
            return page == null ? Placeholder(address) : CreateRow(address, page);
        }
    }

    public int IndexOfAddress(ulong address)
    {
        if (address < BaseAddress || address >= EndAddress) return -1;
        ulong index = (address - BaseAddress) / BytesPerRow;
        return index >= (ulong)Count ? -1 : (int)index;
    }

    public void Invalidate(ulong address, int length)
    {
        if (length <= 0) return;
        ulong last = address > ulong.MaxValue - (ulong)(length - 1) ? ulong.MaxValue : address + (ulong)(length - 1);
        ulong firstPage = address & ~((ulong)PageSize - 1);
        ulong lastPage = last & ~((ulong)PageSize - 1);
        lock (_sync)
        {
            for (ulong page = firstPage; page <= lastPage; page += PageSize)
            {
                _pages.Remove(page);
                _lru.Remove(page);
                _pageVersions[page] = GetPageVersion(page) + 1;
                if (page == lastPage || page > ulong.MaxValue - PageSize) break;
            }
        }
        RaiseReset();
    }

    public IEnumerator<HexPreviewRow> GetEnumerator()
    {
        for (int index = 0; index < Count; index++) yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        lock (_sync) { _pages.Clear(); _loads.Clear(); _lru.Clear(); }
    }

    private void QueueLoad(ulong pageAddress)
    {
        long version = GetPageVersion(pageAddress);
        var key = (pageAddress, version);
        if (_disposed || _loads.ContainsKey(key)) return;
        _loads[key] = LoadPageAsync(pageAddress, version);
    }

    private async Task LoadPageAsync(ulong pageAddress, long version)
    {
        await Task.Yield();
        try
        {
            ulong start = Math.Max(pageAddress, BaseAddress);
            ulong available = EndAddress > start ? EndAddress - start : 0;
            int requested = checked((int)Math.Min((ulong)PageSize, available));
            if (requested <= 0) return;
            MemoryReadResult result = await _memory.ReadAsync(start, requested, _lifetime.Token).ConfigureAwait(false);
            var entry = new PageEntry(start, result.Data, result.Status, result.Error);
            lock (_sync)
            {
                if (_disposed || GetPageVersion(pageAddress) != version) return;
                _pages[pageAddress] = entry;
                Touch(pageAddress);
                while (_pages.Count > _maximumPages && _lru.Last is { } last)
                {
                    _pages.Remove(last.Value);
                    _lru.RemoveLast();
                }
            }
            Status = result.Status switch
            {
                MemoryReadStatus.Complete => $"Virtual hex view · {EndAddress - BaseAddress:N0} bytes · page 0x{pageAddress:X} loaded",
                MemoryReadStatus.Partial => result.Error ?? "Partial memory page read.",
                MemoryReadStatus.Unreadable => result.Error ?? "Memory page is unreadable.",
                MemoryReadStatus.ProcessUnavailable => result.Error ?? "The process terminated.",
                _ => "Hex page load cancelled."
            };
            RaiseReset();
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) Status = "Hex page load cancelled.";
        }
        finally
        {
            lock (_sync) _loads.Remove((pageAddress, version));
        }
    }

    private long GetPageVersion(ulong pageAddress) =>
        _pageVersions.TryGetValue(pageAddress, out long version) ? version : 0;

    private void Touch(ulong pageAddress)
    {
        _lru.Remove(pageAddress);
        _lru.AddFirst(pageAddress);
    }

    private static HexPreviewRow Placeholder(ulong address) =>
        new(address.ToString("X16"), string.Join(' ', Enumerable.Repeat("??", BytesPerRow)),
            new string('·', BytesPerRow), address, Array.Empty<byte>());

    private static HexPreviewRow CreateRow(ulong address, PageEntry page)
    {
        if (address < page.Address || address - page.Address >= (ulong)page.Data.Length)
            return Placeholder(address);
        int offset = checked((int)(address - page.Address));
        int count = Math.Min(BytesPerRow, page.Data.Length - offset);
        byte[] data = page.Data.AsSpan(offset, count).ToArray();
        var ascii = new StringBuilder(data.Length);
        foreach (byte value in data) ascii.Append(value is >= 32 and < 127 ? (char)value : '.');
        return new HexPreviewRow(address.ToString("X16"), string.Join(" ", data.Select(value => value.ToString("X2"))),
            ascii.ToString(), address, data);
    }

    private void RaiseReset() => Post(() => CollectionChanged?.Invoke(this,
        new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)));

    private void RaisePropertyChanged(string propertyName) =>
        Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName)));

    private void Post(Action action)
    {
        if (_context == null || ReferenceEquals(SynchronizationContext.Current, _context)) action();
        else _context.Post(_ => action(), null);
    }

    private sealed record PageEntry(ulong Address, byte[] Data, MemoryReadStatus Status, string? Error);
}
