using CrxMem.MemoryView.Wpf.Services;
using CrxMem.MemoryView.Wpf.ViewModels;
using Iced.Intel;
using Xunit;

namespace CrxMem.Tests;

public sealed class AssemblerServiceTests
{
    private readonly IAssemblerService _assembler = new IcedAssemblerService();

    [Theory]
    [InlineData("nop", "90")]
    [InlineData("mov rax, rbx", "4889D8")]
    [InlineData("sub rsp, 28h", "4883EC28")]
    [InlineData("xor eax, eax", "31C0")]
    public void Assemble_CommonInstructions_ProducesExpectedBytes(string source, string expected)
    {
        AssemblyResult result = _assembler.Assemble(new AssemblyRequest(source, 0x140001000, 64));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(expected, Convert.ToHexString(result.Bytes));
    }

    [Fact]
    public void Assemble_RelativeBranch_ResolvesRequestedTarget()
    {
        const ulong ip = 0x140001000;
        const ulong target = 0x140001020;
        AssemblyResult result = _assembler.Assemble(new AssemblyRequest($"jmp {target:X}h", ip, 64));
        Assert.True(result.Succeeded, result.Error);

        var decoder = Decoder.Create(64, result.Bytes, ip);
        Instruction instruction = decoder.Decode();
        Assert.Equal(Mnemonic.Jmp, instruction.Mnemonic);
        Assert.Equal(target, instruction.NearBranchTarget);
    }

    [Theory]
    [InlineData(16, "Bitness")]
    [InlineData(64, "Unsupported")]
    public void Assemble_InvalidInput_FailsExplicitly(int bitness, string expectedError)
    {
        string source = bitness == 16 ? "nop" : "mov rax, [rbx]";
        AssemblyResult result = _assembler.Assemble(new AssemblyRequest(source, 0x1000, bitness));

        Assert.False(result.Succeeded);
        Assert.Contains(expectedError, result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Bytes);
    }

    [Fact]
    public void Assemble_MultipleInstructions_IsRejected()
    {
        AssemblyResult result = _assembler.Assemble(new AssemblyRequest("nop\nint3", 0x1000, 64));
        Assert.False(result.Succeeded);
        Assert.Contains("one instruction", result.Error, StringComparison.OrdinalIgnoreCase);
    }
}
