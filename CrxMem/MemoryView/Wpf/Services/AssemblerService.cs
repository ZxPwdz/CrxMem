using System.Globalization;
using System.IO;
using Iced.Intel;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed record AssemblyRequest(string Source, ulong InstructionPointer, int Bitness);
public sealed record AssemblyResult(bool Succeeded, byte[] Bytes, string? Error = null)
{
    public static AssemblyResult Failed(string error) => new(false, Array.Empty<byte>(), error);
}

public interface IAssemblerService
{
    AssemblyResult Assemble(AssemblyRequest request);
}

/// <summary>
/// A deliberately bounded single-instruction NASM-like parser backed by Iced's typed assembler.
/// Unsupported syntax fails explicitly and never reaches the patch service.
/// </summary>
public sealed class IcedAssemblerService : IAssemblerService
{
    private static readonly IReadOnlyDictionary<string, AssemblerRegister64> Registers64 =
        new Dictionary<string, AssemblerRegister64>(StringComparer.OrdinalIgnoreCase)
        {
            ["rax"] = AssemblerRegisters.rax, ["rbx"] = AssemblerRegisters.rbx,
            ["rcx"] = AssemblerRegisters.rcx, ["rdx"] = AssemblerRegisters.rdx,
            ["rsi"] = AssemblerRegisters.rsi, ["rdi"] = AssemblerRegisters.rdi,
            ["rsp"] = AssemblerRegisters.rsp, ["rbp"] = AssemblerRegisters.rbp,
            ["r8"] = AssemblerRegisters.r8, ["r9"] = AssemblerRegisters.r9,
            ["r10"] = AssemblerRegisters.r10, ["r11"] = AssemblerRegisters.r11,
            ["r12"] = AssemblerRegisters.r12, ["r13"] = AssemblerRegisters.r13,
            ["r14"] = AssemblerRegisters.r14, ["r15"] = AssemblerRegisters.r15
        };

    private static readonly IReadOnlyDictionary<string, AssemblerRegister32> Registers32 =
        new Dictionary<string, AssemblerRegister32>(StringComparer.OrdinalIgnoreCase)
        {
            ["eax"] = AssemblerRegisters.eax, ["ebx"] = AssemblerRegisters.ebx,
            ["ecx"] = AssemblerRegisters.ecx, ["edx"] = AssemblerRegisters.edx,
            ["esi"] = AssemblerRegisters.esi, ["edi"] = AssemblerRegisters.edi,
            ["esp"] = AssemblerRegisters.esp, ["ebp"] = AssemblerRegisters.ebp,
            ["r8d"] = AssemblerRegisters.r8d, ["r9d"] = AssemblerRegisters.r9d,
            ["r10d"] = AssemblerRegisters.r10d, ["r11d"] = AssemblerRegisters.r11d,
            ["r12d"] = AssemblerRegisters.r12d, ["r13d"] = AssemblerRegisters.r13d,
            ["r14d"] = AssemblerRegisters.r14d, ["r15d"] = AssemblerRegisters.r15d
        };

    public AssemblyResult Assemble(AssemblyRequest request)
    {
        if (request.Bitness is not (32 or 64)) return AssemblyResult.Failed("Bitness must be 32 or 64.");
        string source = request.Source?.Trim() ?? string.Empty;
        if (source.Length == 0) return AssemblyResult.Failed("Enter one instruction to assemble.");
        if (source.Contains('\n') || source.Contains('\r') || source.Contains(';'))
            return AssemblyResult.Failed("Only one instruction is accepted per patch transaction.");

        try
        {
            var assembler = new Assembler(request.Bitness);
            if (!TryEmit(assembler, source, out string error)) return AssemblyResult.Failed(error);
            using var stream = new MemoryStream();
            assembler.Assemble(new StreamCodeWriter(stream), request.InstructionPointer);
            byte[] bytes = stream.ToArray();
            return bytes.Length == 0 ? AssemblyResult.Failed("The instruction produced no bytes.") : new(true, bytes);
        }
        catch (Exception ex)
        {
            return AssemblyResult.Failed($"Assembly failed: {ex.Message}");
        }
    }

    private static bool TryEmit(Assembler assembler, string source, out string error)
    {
        string[] parts = source.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
        string mnemonic = parts[0].ToLowerInvariant();
        string operandsText = parts.Length == 2 ? parts[1].Trim() : string.Empty;
        error = $"Unsupported instruction or operand form: {source}";

        if (operandsText.Length == 0 && EmitNoOperand(assembler, mnemonic)) return true;

        if (mnemonic is "ret" or "retn" && TryNumber(operandsText, out ulong retValue) && retValue <= ushort.MaxValue)
        {
            assembler.ret((ushort)retValue);
            return true;
        }
        if (mnemonic == "int" && TryNumber(operandsText, out ulong vector) && vector <= byte.MaxValue)
        {
            assembler.@int((byte)vector);
            return true;
        }
        if (TryNumber(RemoveBranchQualifier(operandsText), out ulong target) && EmitBranch(assembler, mnemonic, target)) return true;
        if (EmitUnary(assembler, mnemonic, operandsText)) return true;

        string[] operands = operandsText.Split(',', StringSplitOptions.TrimEntries);
        if (operands.Length == 2 && EmitBinary(assembler, mnemonic, operands[0], operands[1])) return true;
        return false;
    }

    private static bool EmitNoOperand(Assembler a, string mnemonic)
    {
        switch (mnemonic)
        {
            case "nop": a.nop(); break; case "int3": a.int3(); break; case "pause": a.pause(); break;
            case "hlt": a.hlt(); break; case "clc": a.clc(); break; case "stc": a.stc(); break;
            case "cmc": a.cmc(); break; case "cld": a.cld(); break; case "std": a.std(); break;
            case "pushfq": a.pushfq(); break; case "popfq": a.popfq(); break;
            case "pushfd": a.pushfd(); break; case "popfd": a.popfd(); break;
            case "ret": case "retn": a.ret(); break; case "leave": a.leave(); break;
            case "cdq": a.cdq(); break; case "cqo": a.cqo(); break; case "cbw": a.cbw(); break;
            case "cwde": a.cwde(); break; case "cdqe": a.cdqe(); break;
            default: return false;
        }
        return true;
    }

    private static bool EmitBranch(Assembler a, string mnemonic, ulong target)
    {
        switch (mnemonic)
        {
            case "jmp": a.jmp(target); break; case "call": a.call(target); break;
            case "je": case "jz": a.je(target); break; case "jne": case "jnz": a.jne(target); break;
            case "ja": case "jnbe": a.ja(target); break; case "jae": case "jnb": case "jnc": a.jae(target); break;
            case "jb": case "jnae": case "jc": a.jb(target); break; case "jbe": case "jna": a.jbe(target); break;
            case "jg": case "jnle": a.jg(target); break; case "jge": case "jnl": a.jge(target); break;
            case "jl": case "jnge": a.jl(target); break; case "jle": case "jng": a.jle(target); break;
            case "jo": a.jo(target); break; case "jno": a.jno(target); break;
            case "jp": case "jpe": a.jp(target); break; case "jnp": case "jpo": a.jnp(target); break;
            case "js": a.js(target); break; case "jns": a.jns(target); break;
            case "jecxz": a.jecxz(target); break; case "jrcxz": a.jrcxz(target); break;
            case "loop": a.loop(target); break; case "loope": case "loopz": a.loope(target); break;
            case "loopne": case "loopnz": a.loopne(target); break;
            case "push" when target <= int.MaxValue: a.push((int)target); break;
            default: return false;
        }
        return true;
    }

    private static bool EmitUnary(Assembler a, string mnemonic, string operand)
    {
        if (Registers64.TryGetValue(operand, out AssemblerRegister64 r64))
        {
            switch (mnemonic)
            {
                case "push": a.push(r64); break; case "pop": a.pop(r64); break; case "inc": a.inc(r64); break;
                case "dec": a.dec(r64); break; case "neg": a.neg(r64); break; case "not": a.not(r64); break;
                case "mul": a.mul(r64); break; case "imul": a.imul(r64); break; case "div": a.div(r64); break;
                case "idiv": a.idiv(r64); break; case "jmp": a.jmp(r64); break; case "call": a.call(r64); break;
                default: return false;
            }
            return true;
        }
        if (!Registers32.TryGetValue(operand, out AssemblerRegister32 r32)) return false;
        switch (mnemonic)
        {
            case "push": a.push(r32); break; case "pop": a.pop(r32); break; case "inc": a.inc(r32); break;
            case "dec": a.dec(r32); break; case "neg": a.neg(r32); break; case "not": a.not(r32); break;
            case "mul": a.mul(r32); break; case "imul": a.imul(r32); break; case "div": a.div(r32); break;
            case "idiv": a.idiv(r32); break; case "jmp": a.jmp(r32); break; case "call": a.call(r32); break;
            default: return false;
        }
        return true;
    }

    private static bool EmitBinary(Assembler a, string mnemonic, string destination, string source)
    {
        if (Registers64.TryGetValue(destination, out AssemblerRegister64 d64))
        {
            if (Registers64.TryGetValue(source, out AssemblerRegister64 s64))
            {
                switch (mnemonic)
                {
                    case "mov": a.mov(d64, s64); break; case "add": a.add(d64, s64); break;
                    case "sub": a.sub(d64, s64); break; case "xor": a.xor(d64, s64); break;
                    case "and": a.and(d64, s64); break; case "or": a.or(d64, s64); break;
                    case "cmp": a.cmp(d64, s64); break; case "test": a.test(d64, s64); break;
                    case "xchg": a.xchg(d64, s64); break; case "imul": a.imul(d64, s64); break;
                    default: return false;
                }
                return true;
            }
            if (TryNumber(source, out ulong immediate)) return EmitImmediate64(a, mnemonic, d64, immediate);
        }
        if (Registers32.TryGetValue(destination, out AssemblerRegister32 d32))
        {
            if (Registers32.TryGetValue(source, out AssemblerRegister32 s32))
            {
                switch (mnemonic)
                {
                    case "mov": a.mov(d32, s32); break; case "add": a.add(d32, s32); break;
                    case "sub": a.sub(d32, s32); break; case "xor": a.xor(d32, s32); break;
                    case "and": a.and(d32, s32); break; case "or": a.or(d32, s32); break;
                    case "cmp": a.cmp(d32, s32); break; case "test": a.test(d32, s32); break;
                    case "xchg": a.xchg(d32, s32); break; case "imul": a.imul(d32, s32); break;
                    default: return false;
                }
                return true;
            }
            if (TryNumber(source, out ulong immediate)) return EmitImmediate32(a, mnemonic, d32, immediate);
        }
        return false;
    }

    private static bool EmitImmediate64(Assembler a, string mnemonic, AssemblerRegister64 register, ulong value)
    {
        int signed = unchecked((int)value);
        switch (mnemonic)
        {
            case "mov": a.mov(register, value); break; case "add": a.add(register, signed); break;
            case "sub": a.sub(register, signed); break; case "xor": a.xor(register, signed); break;
            case "and": a.and(register, signed); break; case "or": a.or(register, signed); break;
            case "cmp": a.cmp(register, signed); break; case "test": a.test(register, signed); break;
            case "shl": case "sal": a.shl(register, (byte)value); break; case "shr": a.shr(register, (byte)value); break;
            case "sar": a.sar(register, (byte)value); break; case "rol": a.rol(register, (byte)value); break;
            case "ror": a.ror(register, (byte)value); break; default: return false;
        }
        return true;
    }

    private static bool EmitImmediate32(Assembler a, string mnemonic, AssemblerRegister32 register, ulong value)
    {
        int signed = unchecked((int)value);
        switch (mnemonic)
        {
            case "mov": a.mov(register, (uint)value); break; case "add": a.add(register, signed); break;
            case "sub": a.sub(register, signed); break; case "xor": a.xor(register, signed); break;
            case "and": a.and(register, signed); break; case "or": a.or(register, signed); break;
            case "cmp": a.cmp(register, signed); break; case "test": a.test(register, signed); break;
            case "shl": case "sal": a.shl(register, (byte)value); break; case "shr": a.shr(register, (byte)value); break;
            case "sar": a.sar(register, (byte)value); break; case "rol": a.rol(register, (byte)value); break;
            case "ror": a.ror(register, (byte)value); break; default: return false;
        }
        return true;
    }

    private static string RemoveBranchQualifier(string value)
    {
        string trimmed = value.Trim();
        foreach (string prefix in new[] { "near ", "short " })
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return trimmed[prefix.Length..].Trim();
        return trimmed;
    }

    private static bool TryNumber(string text, out ulong value)
    {
        string candidate = text.Trim();
        if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) candidate = candidate[2..];
        else if (candidate.EndsWith("h", StringComparison.OrdinalIgnoreCase)) candidate = candidate[..^1];
        return ulong.TryParse(candidate, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
