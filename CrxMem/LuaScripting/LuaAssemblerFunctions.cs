using System;
using System.Collections.Generic;
using System.Text;
using IcedDecoder = Iced.Intel.Decoder;
using Iced.Intel;
using NLua;
using CrxMem.Core;

namespace CrxMem.LuaScripting
{
    /// <summary>
    /// Assembler and disassembler functions for Lua scripts.
    /// Uses Iced library for x86/x64 assembly.
    /// </summary>
    public class LuaAssemblerFunctions
    {
        private readonly LuaEngine _engine;

        public LuaAssemblerFunctions(LuaEngine engine)
        {
            _engine = engine;
        }

        public void Register(Lua lua)
        {
            lua.RegisterFunction("disassemble", this, GetType().GetMethod(nameof(Disassemble)));
            lua.RegisterFunction("getInstructionSize", this, GetType().GetMethod(nameof(GetInstructionSize)));
            lua.RegisterFunction("assemble", this, GetType().GetMethod(nameof(Assemble)));
            lua.RegisterFunction("autoAssemble", this, GetType().GetMethod(nameof(AutoAssemble)));
        }

        /// <summary>
        /// Disassemble an instruction at the given address.
        /// Returns the disassembled instruction string.
        /// </summary>
        public string Disassemble(object address)
        {
            var process = _engine.ProcessAccess;
            if (process == null) return "";

            IntPtr addr = ToAddress(address);
            if (addr == IntPtr.Zero) return "";

            try
            {
                // Read bytes for disassembly (max instruction is 15 bytes)
                byte[]? buffer = process.Read(addr, 15);
                if (buffer == null || buffer.Length == 0)
                    return "";

                // Disassemble
                int bitness = process.Is64Bit ? 64 : 32;
                var decoder = IcedDecoder.Create(bitness, buffer, (ulong)addr.ToInt64());
                var instruction = decoder.Decode();

                if (instruction.IsInvalid)
                    return "";

                // Format the instruction
                var formatter = new NasmFormatter();
                var output = new StringOutput();
                formatter.Format(instruction, output);

                return output.ToStringAndReset();
            }
            catch { return ""; }
        }

        /// <summary>
        /// Get the size of the instruction at the given address.
        /// </summary>
        public int GetInstructionSize(object address)
        {
            var process = _engine.ProcessAccess;
            if (process == null) return 0;

            IntPtr addr = ToAddress(address);
            if (addr == IntPtr.Zero) return 0;

            try
            {
                byte[]? buffer = process.Read(addr, 15);
                if (buffer == null || buffer.Length == 0)
                    return 0;

                int bitness = process.Is64Bit ? 64 : 32;
                var decoder = IcedDecoder.Create(bitness, buffer, (ulong)addr.ToInt64());
                var instruction = decoder.Decode();

                return instruction.IsInvalid ? 0 : instruction.Length;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Assemble an instruction at the given address.
        /// Returns the assembled bytes.
        /// </summary>
        public byte[] Assemble(string instruction, object address)
        {
            var process = _engine.ProcessAccess;
            if (process == null) return Array.Empty<byte>();

            IntPtr addr = ToAddress(address);
            if (addr == IntPtr.Zero) return Array.Empty<byte>();

            try
            {
                // Use Iced's assembler
                int bitness = process.Is64Bit ? 64 : 32;
                var assembler = new Assembler(bitness);

                // Parse and assemble the instruction
                // This is a simplified version - full implementation would parse the instruction string
                var bytes = AssembleInstruction(instruction, (ulong)addr.ToInt64(), bitness);
                return bytes;
            }
            catch { return Array.Empty<byte>(); }
        }

        /// <summary>
        /// Simple instruction assembler - handles common instructions.
        /// </summary>
        private byte[] AssembleInstruction(string instruction, ulong address, int bitness)
        {
            instruction = instruction.Trim().ToLower();

            // Handle NOP
            if (instruction == "nop")
                return new byte[] { 0x90 };

            // Handle RET
            if (instruction == "ret" || instruction == "retn")
                return new byte[] { 0xC3 };

            // Handle INT3
            if (instruction == "int3" || instruction == "int 3")
                return new byte[] { 0xCC };

            // Handle simple JMP/CALL patterns
            if (instruction.StartsWith("jmp ") || instruction.StartsWith("call "))
            {
                // Parse target address
                string targetStr = instruction.Substring(instruction.IndexOf(' ') + 1).Trim();
                targetStr = targetStr.Replace("0x", "").Replace("h", "");

                if (long.TryParse(targetStr, System.Globalization.NumberStyles.HexNumber, null, out long target))
                {
                    long offset = target - (long)address - 5; // 5 byte instruction

                    if (instruction.StartsWith("jmp"))
                    {
                        // JMP rel32
                        var bytes = new byte[5];
                        bytes[0] = 0xE9;
                        BitConverter.GetBytes((int)offset).CopyTo(bytes, 1);
                        return bytes;
                    }
                    else
                    {
                        // CALL rel32
                        var bytes = new byte[5];
                        bytes[0] = 0xE8;
                        BitConverter.GetBytes((int)offset).CopyTo(bytes, 1);
                        return bytes;
                    }
                }
            }

            // For more complex instructions, we'd need a full assembler parser
            // This is a placeholder - returning empty for unsupported instructions
            return Array.Empty<byte>();
        }

        /// <summary>
        /// Auto Assembler execution is intentionally unavailable in the unrestricted legacy Lua state.
        /// Phase 19 routes this through the restricted, transactional AA host.
        /// </summary>
        public bool AutoAssemble(string script)
        {
            _engine.LuaPrint("autoAssemble is unavailable in the unrestricted Lua engine. Open Memory View > Tools > Auto Assembler; restricted Lua compatibility is provided by Phase 19.");
            return false;
        }

        private byte[] ParseByteDefinition(string line)
        {
            var bytes = new List<byte>();
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            for (int i = 1; i < parts.Length; i++)
            {
                string part = parts[i].Trim(',').Replace("0x", "").Replace("h", "");
                if (byte.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                {
                    bytes.Add(b);
                }
            }

            return bytes.ToArray();
        }

        private long ParseAddress(string addressStr)
        {
            var process = _engine.ProcessAccess;
            addressStr = addressStr.Trim();

            // Check for module+offset
            if (addressStr.Contains('+') && process != null)
            {
                var parts = addressStr.Split('+');
                string moduleName = parts[0].Trim();
                string offsetStr = parts[1].Trim().Replace("0x", "").Replace("h", "");

                if (long.TryParse(offsetStr, System.Globalization.NumberStyles.HexNumber, null, out long offset))
                {
                    var modules = process.GetModules();
                    foreach (var module in modules)
                    {
                        if (module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase) ||
                            module.ModuleName.Equals(moduleName + ".exe", StringComparison.OrdinalIgnoreCase) ||
                            module.ModuleName.Equals(moduleName + ".dll", StringComparison.OrdinalIgnoreCase))
                        {
                            return module.BaseAddress.ToInt64() + offset;
                        }
                    }
                }
            }

            // Plain hex
            string clean = addressStr.Replace("0x", "").Replace("h", "");
            if (long.TryParse(clean, System.Globalization.NumberStyles.HexNumber, null, out long addr))
            {
                return addr;
            }

            return 0;
        }

        private IntPtr ToAddress(object addressObj)
        {
            if (addressObj == null) return IntPtr.Zero;

            if (addressObj is string addressStr)
            {
                long parsed = ParseAddress(addressStr);
                return new IntPtr(parsed);
            }

            if (addressObj is long l) return new IntPtr(l);
            if (addressObj is int i) return new IntPtr(i);
            if (addressObj is double d) return new IntPtr((long)d);

            return IntPtr.Zero;
        }
    }
}
