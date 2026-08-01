namespace CrxMem.MemoryView.Wpf.Models;

public enum DisassemblyTokenKind
{
    Text,
    Mnemonic,
    Register,
    Number,
    Address,
    Keyword,
    Punctuation
}

public sealed record DisassemblyToken(string Text, DisassemblyTokenKind Kind);

public sealed record DisassemblyPreviewRow(
    string Marker,
    string Address,
    string Bytes,
    string Instruction,
    string Comment,
    ulong RuntimeAddress,
    IReadOnlyList<DisassemblyToken>? Tokens = null,
    ulong? FlowTarget = null,
    Guid? AnnotationId = null,
    string? BookmarkLabel = null,
    string? UserComment = null,
    string? ResolvedSymbol = null,
    string? BookmarkColor = null);

public sealed record HexPreviewRow(
    string Address,
    string Bytes,
    string Ascii,
    ulong RuntimeAddress = 0,
    byte[]? Data = null,
    IReadOnlySet<int>? ChangedByteIndices = null);

public sealed record StackPreviewRow(
    string Address,
    string Value,
    string Label,
    ulong RuntimeAddress,
    ulong PointerValue,
    bool IsHeuristic);

public sealed record WatchPreviewRow(
    string Expression,
    string Address,
    string Value,
    string Status,
    ulong? RuntimeAddress,
    ulong? NumericValue,
    ulong? PreviousValue = null)
{
    public bool HasChanged => PreviousValue.HasValue && NumericValue.HasValue && PreviousValue != NumericValue;
}

public sealed record MemoryViewSnapshot(
    ulong Address,
    string ProcessName,
    int ProcessId,
    string ModuleName,
    ulong ModuleBase,
    string Architecture,
    string Offset,
    string Protection,
    string Region,
    IReadOnlyList<DisassemblyPreviewRow> Instructions,
    IReadOnlyList<HexPreviewRow> HexRows,
    string Status);
