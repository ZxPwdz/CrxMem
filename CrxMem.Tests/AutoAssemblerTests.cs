using System.Globalization;
using CrxMem.MemoryView.Wpf.Services;
using CrxMem.Core;
using Xunit;

namespace CrxMem.Tests;

public sealed class AutoAssemblerTests
{
    [Fact]
    public void Parser_SeparatesEnableDisableAndBitness()
    {
        var diagnostics = new List<AutoAssemblerDiagnostic>();
        AutoAssemblerDocument document = new AutoAssemblerParser().Parse(
            "[64-BIT]\n[ENABLE]\n1000:\ndb 90\n[DISABLE]\n1000:\ndb CC", diagnostics);

        Assert.Equal(64, document.ForcedBitness);
        Assert.Equal(2, document.Lines.Count(line => line.Section == AutoAssemblerSection.Enable));
        Assert.Equal(2, document.Lines.Count(line => line.Section == AutoAssemblerSection.Disable));
        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task Compiler_PreviewsDefinesAssertionsDataAndInstructionsWithoutMutation()
    {
        var compiler = new AutoAssemblerCompiler(new FakeAddresses(), new IcedAssemblerService(), new FakePlanner());
        const string script = "[ENABLE]\ndefine(HOOK,1000)\nassert(HOOK,90 90)\nHOOK:\ndb CC,90\nnop\n[DISABLE]\nHOOK:\ndb 90 90 90";

        AutoAssemblerCompileResult result = await compiler.CompileAsync(
            new(script, true, 64, 7, DateTime.UnixEpoch), CancellationToken.None);

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        Assert.Single(result.Plan!.Assertions);
        AutoAssemblerWrite write = Assert.Single(result.Plan.Writes);
        Assert.Equal(0x1000UL, write.Address);
        Assert.Equal(new byte[] { 0xCC, 0x90, 0x90 }, write.Bytes);
    }

    [Fact]
    public async Task Compiler_RecognizesAobButRejectsWhenBoundedScanSourceIsUnavailable()
    {
        var compiler = new AutoAssemblerCompiler(new FakeAddresses(), new IcedAssemblerService(), new FakePlanner());
        AutoAssemblerCompileResult result = await compiler.CompileAsync(new(
            "[ENABLE]\naobscanmodule(INJECT,test.exe,48 8B ?? ??)\nINJECT:\nnop", true, 64, 7, DateTime.UnixEpoch),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, item => item.Code == "AA2201");
        Assert.DoesNotContain(result.Plan!.Writes, write => write.Bytes.Length > 0);
    }

    [Fact]
    public async Task Compiler_ProducesExactNearAllocationProposal()
    {
        var compiler = new AutoAssemblerCompiler(new FakeAddresses(), new IcedAssemblerService(), new FakePlanner());
        AutoAssemblerCompileResult result = await compiler.CompileAsync(new(
            "[ENABLE]\nalloc(newmem,1000,2000)\nnewmem:\ndb 90", true, 64, 7, DateTime.UnixEpoch),
            CancellationToken.None);

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(item => item.Message)));
        AutoAssemblerAllocation allocation = Assert.Single(result.Plan!.Allocations);
        Assert.Equal(0x500000UL, allocation.Address);
        Assert.Equal(0x1000U, allocation.Size);
        Assert.Equal(0x2000UL, allocation.NearAddress);
    }

    [Fact]
    public async Task OptIn_TransactionalEnableDisableMutatesAndRestoresBenignProbe()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("CRXMEM_RUN_AA_TESTS"), "1", StringComparison.Ordinal)) return;
        string targetPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "CrxMem.DebugTarget", "bin", "Release", "net8.0-windows", "CrxMem.DebugTarget.exe"));
        using var target = new System.Diagnostics.Process
        {
            StartInfo = new(targetPath) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true }
        };
        Assert.True(target.Start());
        try
        {
            string ready = (await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!;
            string probeText = ready.Split(' ').Single(item => item.StartsWith("PROBE=", StringComparison.Ordinal))[6..];
            ulong probe = ulong.Parse(probeText[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            using var process = new ProcessAccess();
            Assert.True(process.Open(target.Id));
            var memory = new ProcessMemoryService(process);
            var modules = new ModuleService(process);
            var sessions = new InMemoryAutoAssemblerSessionStore();
            using var patches = new PatchService(new ProcessPatchMemoryAdapter(process));
            var compiler = new AutoAssemblerCompiler(new AddressExpressionService(memory, modules),
                new IcedAssemblerService(), new ProcessNearAllocationPlanner(process),
                aobScanner: new ProcessAobScanService(process, memory, modules), memory: memory);
            var service = new AutoAssemblerService(process, compiler,
                new AutoAssemblerTransactionExecutor(process, patches, sessions), sessions);
            string script = $"[ENABLE]\nassert({probe:X},FE 01 C3)\n{probe:X}:\ndb 90 90 C3\n[DISABLE]\n{probe:X}:\ndb FE 01 C3";

            AutoAssemblerExecutionResult enabled = await service.EnableAsync(script, CancellationToken.None);
            Assert.True(enabled.Succeeded, enabled.Message);
            Assert.Equal(new byte[] { 0x90, 0x90, 0xC3 }, process.Read(new IntPtr(unchecked((long)probe)), 3));
            AutoAssemblerExecutionResult disabled = await service.DisableAsync(script, CancellationToken.None);
            Assert.True(disabled.Succeeded, disabled.Message);
            Assert.Equal(new byte[] { 0xFE, 0x01, 0xC3 }, process.Read(new IntPtr(unchecked((long)probe)), 3));
        }
        finally
        {
            if (!target.HasExited) target.Kill(entireProcessTree: true);
            await target.WaitForExitAsync();
        }
    }

    private sealed class FakeAddresses : IAddressExpressionService
    {
        public Task<AddressExpressionResult> EvaluateAsync(string expression, CancellationToken cancellationToken)
        {
            string value = expression.Trim();
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value[2..];
            return Task.FromResult(ulong.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address)
                ? AddressExpressionResult.Resolved(address) : AddressExpressionResult.Failed($"Unknown address {expression}."));
        }
    }

    private sealed class FakePlanner : INearAllocationPlanner
    {
        public Task<AutoAssemblerAllocation?> ProposeAsync(string name, uint size, uint protection, ulong? nearAddress,
            CancellationToken cancellationToken) => Task.FromResult<AutoAssemblerAllocation?>(
                new(name, 0x500000, size, protection, nearAddress));
    }
}
