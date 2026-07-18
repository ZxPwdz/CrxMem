namespace CrxMem.MemoryView.Wpf.Models;

public sealed record DisassemblyPreviewRow(
    string Marker,
    string Address,
    string Bytes,
    string Instruction,
    string Comment,
    ulong RuntimeAddress);

public sealed record HexPreviewRow(string Address, string Bytes, string Ascii);

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
