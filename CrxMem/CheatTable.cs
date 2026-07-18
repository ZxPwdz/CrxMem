using System;
using System.Collections.Generic;

namespace CrxMem
{
    public class CheatTable
    {
        public const string NativeFormat = "CrxMemTable";
        public const string CurrentVersion = "2.0";

        public string Format { get; set; } = NativeFormat;
        public string Version { get; set; } = CurrentVersion;
        public string ProcessName { get; set; } = "";
        public List<CheatEntry> Entries { get; set; } = new();
    }

    public class CheatEntry
    {
        public string Address { get; set; } = "";
        public string Description { get; set; } = "";
        public string Type { get; set; } = "4 Bytes";
        public bool Frozen { get; set; }
        public string FrozenValue { get; set; } = "";
        public bool Active { get; set; } = true;
        public bool ShowAsHex { get; set; } = false;
    }
}
