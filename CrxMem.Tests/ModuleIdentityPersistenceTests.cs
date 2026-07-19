using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class ModuleIdentityPersistenceTests
{
    [Fact]
    public void FileIdentity_RejectsChangedBinary()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CrxMem.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "target.exe");
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            var modules = new FakeModuleService(new ModuleDescriptor("target.exe", 0x140000000, 0x1000, FilePath: path));
            var service = new FileModuleIdentityService(modules);
            ModuleIdentity original = Assert.IsType<ModuleIdentity>(service.GetCurrent("target.exe"));
            Assert.True(service.Resolve(original, 0x20).IsMatch);
            Assert.Equal(0x140000020UL, service.Resolve(original, 0x20).RuntimeAddress);

            File.WriteAllBytes(path, [4, 3, 2, 1, 0]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            ModuleIdentityResolution changed = service.Resolve(original, 0x20);

            Assert.False(changed.IsMatch);
            Assert.Null(changed.RuntimeAddress);
            Assert.Contains("fingerprint", changed.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void JsonStore_RoundTripsVersionedPortableEntries()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CrxMem.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "patches.json");
        try
        {
            var store = new JsonPatchHistoryStore(path);
            var identity = new ModuleIdentity("target.exe", 0x1000, 1234, 5678, new string('A', 64));
            var entry = new PersistedPatchEntry(Guid.NewGuid(), identity, 0x30, [0xCC], [0x90], "test", DateTimeOffset.UtcNow);

            store.Save([entry]);
            PersistedPatchEntry loaded = Assert.Single(store.Load());

            Assert.Equal(entry.Id, loaded.Id);
            Assert.Equal(entry.ModuleIdentity, loaded.ModuleIdentity);
            Assert.Equal(entry.ModuleRelativeAddress, loaded.ModuleRelativeAddress);
            Assert.Equal(entry.OriginalBytes, loaded.OriginalBytes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task PatchService_PersistsOnlyFingerprintBackedModuleRelativeRecords()
    {
        var memory = new FakeMemory(0x5000, [0xCC]);
        var store = new FakeStore();
        var identity = new ModuleIdentity("target.exe", 0x1000, 1234, 5678, new string('B', 64));
        var identities = new FakeIdentityService(identity, 0x4000);
        using var patches = new PatchService(memory, store, identities);

        PatchResult result = await patches.ApplyAsync(new PatchRequest(
            0x5000, [0xCC], [0x90], Description: "assembled nop",
            ModuleName: "target.exe", ModuleRelativeAddress: 0x1000));

        Assert.True(result.Succeeded, result.Message);
        PersistedPatchEntry persisted = Assert.Single(store.Entries);
        Assert.Equal(identity, persisted.ModuleIdentity);
        Assert.Equal(0x1000UL, persisted.ModuleRelativeAddress);
        Assert.Equal(new byte[] { 0x90 }, memory.Bytes);
    }

    [Fact]
    public void PatchService_LoadMarksWrongBinaryStaleWithoutUsingRuntimeAddress()
    {
        var expected = new ModuleIdentity("target.exe", 0x1000, 1234, 5678, new string('C', 64));
        var store = new FakeStore
        {
            Entries = [new PersistedPatchEntry(Guid.NewGuid(), expected, 0x20, [0xCC], [0x90], "old", DateTimeOffset.UtcNow)]
        };
        using var patches = new PatchService(new FakeMemory(0x4020, [0xCC]), store,
            new FakeIdentityService(expected, 0x4000, match: false));

        PatchRecord record = Assert.Single(patches.History);
        Assert.True(record.IsStale);
        Assert.False(record.IsApplied);
        Assert.Equal(0UL, record.Address);
    }

    [Fact]
    public void VersionedMetadata_RoundTripsByFingerprintAndRva()
    {
        string directory = Path.Combine(Path.GetTempPath(), "CrxMem.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "metadata.json");
        Directory.CreateDirectory(directory);
        try
        {
            var module = new ModuleDescriptor("target.exe", 0x4000, 0x1000);
            var modules = new FakeModuleService(module);
            var identity = new ModuleIdentity("target.exe", 0x1000, 1234, 5678, new string('D', 64));
            var identities = new FakeIdentityService(identity, module.BaseAddress);
            var location = MemoryLocation.Create(0x4123, module);
            var writer = new VersionedMemoryMetadataService(modules, identities, path, migrateLegacy: false);

            Assert.True(writer.SetComment(location, "player health"));
            Assert.True(writer.SetBookmark(location, true, "health write"));
            Assert.True(writer.SetMetadata(location, new EditableMemoryMetadata("armor", true, "armor write")).Succeeded);

            var reader = new VersionedMemoryMetadataService(modules, identities, path, migrateLegacy: false);
            Assert.Equal("armor", reader.GetComment(location));
            Assert.True(reader.HasBookmark(location));
            Assert.Equal("armor write", reader.GetMetadata(location).Label);

            var wrongIdentity = identity with { Sha256 = new string('E', 64) };
            var staleReader = new VersionedMemoryMetadataService(modules,
                new FakeIdentityService(wrongIdentity, module.BaseAddress), path, migrateLegacy: false);
            Assert.Null(staleReader.GetComment(location));
            Assert.False(staleReader.HasBookmark(location));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class FakeModuleService(ModuleDescriptor module) : IModuleService
    {
        public IReadOnlyList<ModuleDescriptor> GetModules() => [module];
        public ModuleDescriptor? FindByAddress(ulong address) => module.Contains(address) ? module : null;
        public ModuleDescriptor? FindByName(string moduleName) => module.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase) ? module : null;
        public ulong GetDefaultAddress() => module.BaseAddress;
    }

    private sealed class FakeStore : IPatchHistoryStore
    {
        public IReadOnlyList<PersistedPatchEntry> Entries { get; set; } = Array.Empty<PersistedPatchEntry>();
        public IReadOnlyList<PersistedPatchEntry> Load() => Entries;
        public void Save(IReadOnlyList<PersistedPatchEntry> entries) => Entries = entries.ToArray();
    }

    private sealed class FakeIdentityService(ModuleIdentity identity, ulong moduleBase, bool match = true) : IModuleIdentityService
    {
        public ModuleIdentity? GetCurrent(string moduleName) => match ? identity : null;
        public ModuleIdentityResolution Resolve(ModuleIdentity expected, ulong rva) => match
            ? new(true, moduleBase + rva, "verified", identity)
            : new(false, null, "fingerprint mismatch", identity);
    }

    private sealed class FakeMemory : IPatchMemoryAdapter
    {
        private readonly ulong _address;
        public FakeMemory(ulong address, byte[] bytes) { _address = address; Bytes = bytes.ToArray(); }
        public byte[] Bytes { get; private set; }
        public bool IsAvailable => true;
        public byte[]? Read(ulong address, int size) => address == _address ? Bytes.Take(size).ToArray() : null;
        public bool ChangeProtection(ulong address, int size, uint protection, out uint previousProtection) { previousProtection = 0x20; return true; }
        public bool Write(ulong address, byte[] bytes) { if (address != _address) return false; Bytes = bytes.ToArray(); return true; }
        public bool FlushInstructionCache(ulong address, int size) => true;
        public void InvalidateReadCache() { }
    }
}
