using System.Text;
using CrxMem.MemoryView.Wpf.Models;
using Iced.Intel;
using IcedDecoder = Iced.Intel.Decoder;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed record DisassemblyRequest(
    ulong Address,
    byte[] Code,
    bool Is64Bit,
    ModuleDescriptor? Module,
    int MaximumInstructions = 300);

public interface IDisassemblyService
{
    Task<IReadOnlyList<DisassemblyPreviewRow>> DecodeAsync(
        DisassemblyRequest request,
        CancellationToken cancellationToken);
}

public sealed class IcedDisassemblyService : IDisassemblyService
{
    private readonly IMemoryMetadataService _metadata;

    public IcedDisassemblyService(IMemoryMetadataService metadata) => _metadata = metadata;

    public Task<IReadOnlyList<DisassemblyPreviewRow>> DecodeAsync(
        DisassemblyRequest request,
        CancellationToken cancellationToken) =>
        Task.Run(() => Decode(request, cancellationToken), cancellationToken);

    private IReadOnlyList<DisassemblyPreviewRow> Decode(
        DisassemblyRequest request,
        CancellationToken cancellationToken)
    {
        var rows = new List<DisassemblyPreviewRow>();
        if (request.Code.Length == 0) return rows;

        var decoder = IcedDecoder.Create(request.Is64Bit ? 64 : 32, request.Code);
        decoder.IP = request.Address;
        var formatter = new NasmFormatter();
        formatter.Options.HexPrefix = "0x";
        ulong decodeEnd = request.Address + (ulong)request.Code.Length;

        while (decoder.IP < decodeEnd && rows.Count < request.MaximumInstructions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            decoder.Decode(out Instruction instruction);
            int offset = checked((int)(instruction.IP - request.Address));
            int length = instruction.IsInvalid ? 1 : Math.Max(1, instruction.Length);
            if (offset < 0 || offset >= request.Code.Length) break;
            length = Math.Min(length, request.Code.Length - offset);

            byte[] instructionBytes = request.Code.AsSpan(offset, length).ToArray();
            string instructionText;
            IReadOnlyList<DisassemblyToken> tokens;
            if (instruction.IsInvalid)
            {
                instructionText = $"db 0x{instructionBytes[0]:X2}";
                tokens = new[]
                {
                    new DisassemblyToken("db", DisassemblyTokenKind.Keyword),
                    new DisassemblyToken(" ", DisassemblyTokenKind.Text),
                    new DisassemblyToken($"0x{instructionBytes[0]:X2}", DisassemblyTokenKind.Number)
                };
            }
            else
            {
                var output = new TokenFormatterOutput();
                formatter.Format(instruction, output);
                instructionText = output.ToString();
                tokens = output.Tokens;
            }

            ulong runtimeAddress = instruction.IsInvalid
                ? request.Address + (ulong)offset
                : instruction.IP;
            MemoryLocation location = MemoryLocation.Create(runtimeAddress, request.Module);
            ulong? flowTarget = GetFlowTarget(instruction);
            string comment = _metadata.GetComment(location) ?? ResolveFlowTarget(flowTarget, request.Module);
            string marker = _metadata.HasBookmark(location) ? "◆" : string.Empty;
            rows.Add(new DisassemblyPreviewRow(
                marker,
                runtimeAddress.ToString(request.Is64Bit ? "X16" : "X8"),
                string.Join(" ", Convert.ToHexString(instructionBytes).Chunk(2).Select(chars => new string(chars))),
                instructionText,
                comment,
                runtimeAddress,
                tokens,
                flowTarget));

            if (instruction.IsInvalid) decoder.IP = runtimeAddress + 1;
        }

        return rows;
    }

    private static ulong? GetFlowTarget(Instruction instruction)
    {
        ulong target = instruction.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch
            ? instruction.NearBranchTarget
            : instruction.IsIPRelativeMemoryOperand ? instruction.IPRelativeMemoryAddress : 0;
        return target == 0 ? null : target;
    }

    private static string ResolveFlowTarget(ulong? flowTarget, ModuleDescriptor? module)
    {
        ulong target = flowTarget ?? 0;
        if (target == 0 || module == null) return string.Empty;
        return module.Contains(target)
            ? $"{module.Name}+{target - module.BaseAddress:X}"
            : $"0x{target:X}";
    }

    private sealed class TokenFormatterOutput : FormatterOutput
    {
        private readonly StringBuilder _builder = new();
        private readonly List<DisassemblyToken> _tokens = new();
        public IReadOnlyList<DisassemblyToken> Tokens => _tokens;

        public override void Write(string text, FormatterTextKind kind)
        {
            _builder.Append(text);
            _tokens.Add(new DisassemblyToken(text, Map(kind)));
        }

        public override string ToString() => _builder.ToString();

        private static DisassemblyTokenKind Map(FormatterTextKind kind) => kind switch
        {
            FormatterTextKind.Mnemonic => DisassemblyTokenKind.Mnemonic,
            FormatterTextKind.Register => DisassemblyTokenKind.Register,
            FormatterTextKind.Number => DisassemblyTokenKind.Number,
            FormatterTextKind.LabelAddress or FormatterTextKind.FunctionAddress => DisassemblyTokenKind.Address,
            FormatterTextKind.Keyword => DisassemblyTokenKind.Keyword,
            FormatterTextKind.Punctuation => DisassemblyTokenKind.Punctuation,
            _ => DisassemblyTokenKind.Text
        };
    }
}
