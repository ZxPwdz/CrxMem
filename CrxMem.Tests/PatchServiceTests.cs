using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class PatchServiceTests
{
    [Fact]
    public async Task ApplyEqualLengthPatchVerifiesAndRecordsHistory()
    {
        var memory = new FakePatchMemory(0x1000, 0x48, 0x89);
        using var service = new PatchService(memory);

        PatchResult result = await service.ApplyAsync(Request(new byte[] { 0x90, 0x90 }));

        Assert.True(result.Succeeded);
        Assert.Equal(PatchStatus.Applied, result.Status);
        Assert.Equal(new byte[] { 0x90, 0x90 }, memory.Bytes);
        Assert.True(result.Record!.IsApplied);
        Assert.Single(service.History);
        Assert.Equal(1, memory.FlushCalls);
    }

    [Fact]
    public async Task ShortPatchRequiresExplicitNopPadding()
    {
        var memory = new FakePatchMemory(0x1000, 0x48, 0x89);
        using var service = new PatchService(memory);

        PatchResult rejected = await service.ApplyAsync(Request(new byte[] { 0xCC }));
        PatchResult applied = await service.ApplyAsync(Request(new byte[] { 0xCC }, pad: true));

        Assert.Equal(PatchStatus.Rejected, rejected.Status);
        Assert.Equal(new byte[] { 0xCC, 0x90 }, memory.Bytes);
        Assert.True(applied.Succeeded);
    }

    [Fact]
    public async Task LongerPatchIsRejectedBeforeMemoryAccess()
    {
        var memory = new FakePatchMemory(0x1000, 0x48, 0x89);
        using var service = new PatchService(memory);

        PatchResult result = await service.ApplyAsync(Request(new byte[] { 1, 2, 3 }));

        Assert.Equal(PatchStatus.Rejected, result.Status);
        Assert.Equal(0, memory.ReadCalls);
    }

    [Fact]
    public async Task CancelledRequestDoesNotTouchTargetMemory()
    {
        var memory = new FakePatchMemory(0x1000, 0x48, 0x89);
        using var service = new PatchService(memory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        PatchResult result = await service.ApplyAsync(Request(new byte[] { 0x90, 0x90 }), cancellation.Token);

        Assert.Equal(PatchStatus.Cancelled, result.Status);
        Assert.Equal(0, memory.ReadCalls);
        Assert.Equal(new byte[] { 0x48, 0x89 }, memory.Bytes);
    }

    [Fact]
    public async Task ChangedOriginalBytesAreReportedAsStale()
    {
        var memory = new FakePatchMemory(0x1000, 0x48, 0x88);
        using var service = new PatchService(memory);

        PatchResult result = await service.ApplyAsync(Request(new byte[] { 0x90, 0x90 }));

        Assert.Equal(PatchStatus.Stale, result.Status);
        Assert.Equal(PatchFailureStage.ReadOriginal, result.FailureStage);
        Assert.Equal(0, memory.WriteCalls);
    }

    [Theory]
    [InlineData(Failure.ChangeProtection, PatchFailureStage.ChangeProtection)]
    [InlineData(Failure.Write, PatchFailureStage.Write)]
    public async Task PreCommitFailuresDoNotCreateHistory(Failure failure, PatchFailureStage expectedStage)
    {
        var memory = new FakePatchMemory(0x1000, 0x48, 0x89) { Fail = failure };
        using var service = new PatchService(memory);

        PatchResult result = await service.ApplyAsync(Request(new byte[] { 0x90, 0x90 }));

        Assert.False(result.Succeeded);
        Assert.Equal(expectedStage, result.FailureStage);
        Assert.Empty(service.History);
    }

    [Theory]
    [InlineData(Failure.RestoreProtection, PatchFailureStage.RestoreProtection)]
    [InlineData(Failure.Flush, PatchFailureStage.FlushInstructionCache)]
    [InlineData(Failure.ReadBack, PatchFailureStage.VerifyReadBack)]
    public async Task PostWriteFailuresRollbackOriginalBytes(Failure failure, PatchFailureStage expectedStage)
    {
        var memory = new FakePatchMemory(0x1000, 0x48, 0x89) { Fail = failure };
        using var service = new PatchService(memory);

        PatchResult result = await service.ApplyAsync(Request(new byte[] { 0x90, 0x90 }));

        Assert.False(result.Succeeded);
        Assert.Equal(expectedStage, result.FailureStage);
        Assert.True(result.RollbackSucceeded);
        Assert.Equal(new byte[] { 0x48, 0x89 }, memory.Bytes);
        Assert.Empty(service.History);
    }

    [Fact]
    public async Task FailedRollbackIsReportedAsTheTerminalFailure()
    {
        var memory = new FakePatchMemory(0x1000, 0x48, 0x89)
        {
            Fail = Failure.Flush,
            FailRollbackWrite = true
        };
        using var service = new PatchService(memory);

        PatchResult result = await service.ApplyAsync(Request(new byte[] { 0x90, 0x90 }));

        Assert.Equal(PatchFailureStage.Rollback, result.FailureStage);
        Assert.False(result.RollbackSucceeded);
    }

    [Fact]
    public async Task UndoAndRedoUseExpectedByteVerification()
    {
        var memory = new FakePatchMemory(0x1000, 0x48, 0x89);
        using var service = new PatchService(memory);
        await service.ApplyAsync(Request(new byte[] { 0x90, 0x90 }));

        PatchResult undo = await service.UndoAsync();
        PatchResult redo = await service.RedoAsync();

        Assert.Equal(PatchStatus.Restored, undo.Status);
        Assert.Equal(PatchStatus.Applied, redo.Status);
        Assert.Equal(new byte[] { 0x90, 0x90 }, memory.Bytes);
        Assert.True(service.History.Single().IsApplied);
    }

    [Fact]
    public async Task RestoreMarksHistoryStaleWhenPatchedBytesChangedExternally()
    {
        var memory = new FakePatchMemory(0x1000, 0x48, 0x89);
        using var service = new PatchService(memory);
        PatchResult applied = await service.ApplyAsync(Request(new byte[] { 0x90, 0x90 }));
        memory.Bytes[1] = 0xCC;

        PatchResult restored = await service.RestoreAsync(applied.Record!.Id);

        Assert.Equal(PatchStatus.Stale, restored.Status);
        Assert.True(service.History.Single().IsStale);
    }

    private static PatchRequest Request(byte[] replacement, bool pad = false) =>
        new(0x1000, new byte[] { 0x48, 0x89 }, replacement, pad, "test patch", "target.exe", 0x20);

    public enum Failure
    {
        None,
        ChangeProtection,
        Write,
        RestoreProtection,
        Flush,
        ReadBack
    }

    private sealed class FakePatchMemory : IPatchMemoryAdapter
    {
        private readonly ulong _address;
        private int _protectionCalls;
        private bool _hasWritten;

        public FakePatchMemory(ulong address, params byte[] bytes)
        {
            _address = address;
            Bytes = bytes;
        }

        public byte[] Bytes { get; }
        public Failure Fail { get; set; }
        public bool FailRollbackWrite { get; set; }
        public bool IsAvailable { get; set; } = true;
        public int ReadCalls { get; private set; }
        public int WriteCalls { get; private set; }
        public int FlushCalls { get; private set; }

        public byte[]? Read(ulong address, int size)
        {
            ReadCalls++;
            if (address != _address || size > Bytes.Length) return null;
            if (Fail == Failure.ReadBack && _hasWritten)
            {
                Fail = Failure.None;
                return Enumerable.Repeat((byte)0xEE, size).ToArray();
            }
            return Bytes.Take(size).ToArray();
        }

        public bool ChangeProtection(ulong address, int size, uint protection, out uint previousProtection)
        {
            _protectionCalls++;
            previousProtection = 0x20;
            if (Fail == Failure.ChangeProtection && _protectionCalls == 1) return false;
            if (Fail == Failure.RestoreProtection && _protectionCalls == 2)
            {
                Fail = Failure.None;
                return false;
            }
            return true;
        }

        public bool Write(ulong address, byte[] bytes)
        {
            WriteCalls++;
            if (Fail == Failure.Write && WriteCalls == 1) return false;
            if (FailRollbackWrite && WriteCalls > 1) return false;
            bytes.CopyTo(Bytes, 0);
            _hasWritten = true;
            return true;
        }

        public bool FlushInstructionCache(ulong address, int size)
        {
            FlushCalls++;
            if (Fail == Failure.Flush && FlushCalls == 1)
            {
                Fail = Failure.None;
                return false;
            }
            return true;
        }

        public void InvalidateReadCache() { }
    }
}
