using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class DisassemblyServiceTests
{
    [Fact]
    public async Task DecodeAsync_X64ProducesTokensAndBranchTarget()
    {
        var service = new IcedDisassemblyService(new EmptyMetadataService());
        byte[] code = { 0x48, 0x89, 0xD8, 0xEB, 0x01, 0x90, 0xC3 };

        var rows = await service.DecodeAsync(
            new DisassemblyRequest(0x140001000, code, true,
                new ModuleDescriptor("Target.exe", 0x140000000, 0x100000)),
            CancellationToken.None);

        Assert.Equal("mov rax,rbx", rows[0].Instruction);
        Assert.Contains(rows[0].Tokens!, token => token.Kind == CrxMem.MemoryView.Wpf.Models.DisassemblyTokenKind.Mnemonic);
        Assert.Contains(rows[0].Tokens!, token => token.Kind == CrxMem.MemoryView.Wpf.Models.DisassemblyTokenKind.Register);
        Assert.Equal(0x140001006UL, rows[1].FlowTarget);
        Assert.Equal("Target.exe+1006", rows[1].Comment);
    }

    [Fact]
    public async Task DecodeAsync_InvalidTrailingOpcodeAdvancesAsData()
    {
        var rows = await new IcedDisassemblyService(new EmptyMetadataService()).DecodeAsync(
            new DisassemblyRequest(0x1000, new byte[] { 0x90, 0x0F }, true, null),
            CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal("nop", rows[0].Instruction);
        Assert.StartsWith("db 0x", rows[1].Instruction);
        Assert.Equal(0x1001UL, rows[1].RuntimeAddress);
    }

    [Fact]
    public async Task DecodeAsync_RespectsInstructionBoundAndCancellation()
    {
        var service = new IcedDisassemblyService(new EmptyMetadataService());
        var rows = await service.DecodeAsync(
            new DisassemblyRequest(0x1000, Enumerable.Repeat((byte)0x90, 100).ToArray(), true, null, 3),
            CancellationToken.None);
        Assert.Equal(3, rows.Count);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DecodeAsync(new DisassemblyRequest(0x1000, new byte[] { 0x90 }, true, null), cancellation.Token));
    }

    private sealed class EmptyMetadataService : IMemoryMetadataService
    {
        public string? GetComment(MemoryLocation location) => null;
        public bool HasBookmark(MemoryLocation location) => false;
    }
}
