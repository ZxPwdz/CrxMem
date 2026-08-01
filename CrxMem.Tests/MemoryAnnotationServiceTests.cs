using System.Text.Json;
using CrxMem.MemoryView.Wpf.Services;
using Xunit;

namespace CrxMem.Tests;

public sealed class MemoryAnnotationServiceTests
{
    [Fact]
    public void V1Document_MigratesToStableV2Record()
    {
        using var scope = new TempScope();
        TestContext context = TestContext.Create();
        var old = new PersistedMemoryMetadataDocument
        {
            Entries = [new PersistedMemoryMetadataEntry(context.Identity, 0x123, "health", true, "player", DateTimeOffset.Parse("2025-01-02T03:04:05Z"))]
        };
        File.WriteAllText(scope.Path, JsonSerializer.Serialize(old));

        var service = new VersionedMemoryMetadataService(context.Modules, context.Identities, scope.Path, false);
        ResolvedMemoryAnnotation item = Assert.Single(service.GetAllAnnotations());

        Assert.Equal(MemoryAnnotationLocationKind.ModuleRelative, item.Record.LocationKind);
        Assert.Equal(0x400123UL, item.RuntimeAddress);
        Assert.Equal("health", item.Record.Comment);
        Assert.Equal("player", item.Record.Label);
        Assert.NotEqual(Guid.Empty, item.Id.Value);
    }

    [Fact]
    public void AbsoluteRecord_IsExecutableBoundAndBecomesStaleOnMismatch()
    {
        using var scope = new TempScope();
        TestContext context = TestContext.Create();
        var writer = new VersionedMemoryMetadataService(context.Modules, context.Identities, scope.Path, false);
        AnnotationMutationResult created = writer.Create(new MemoryLocation(0x900000, null, null),
            new MemoryAnnotationEdit("heap", "absolute", null, [], "#39A9FF", false, true));
        Assert.True(created.Succeeded);

        TestContext mismatch = context with { Identities = new FakeIdentityService(context.Identity with { Sha256 = new string('E', 64) }, context.Modules) };
        var reader = new VersionedMemoryMetadataService(mismatch.Modules, mismatch.Identities, scope.Path, false);
        ResolvedMemoryAnnotation item = Assert.Single(reader.GetAllAnnotations());

        Assert.Equal(MemoryAnnotationResolutionState.StaleExecutable, item.State);
        Assert.Null(item.RuntimeAddress);
        Assert.Contains("different executable", item.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MoveConflict_MergeUsesMovingFieldsAndDeletesDestination()
    {
        using var scope = new TempScope();
        TestContext context = TestContext.Create();
        var service = new VersionedMemoryMetadataService(context.Modules, context.Identities, scope.Path, false);
        AnnotationMutationResult source = service.Create(MemoryLocation.Create(0x400100, context.Modules.Module),
            new MemoryAnnotationEdit("source", null, "combat", ["a"], "#FF0000", true, true));
        service.Create(MemoryLocation.Create(0x400200, context.Modules.Module),
            new MemoryAnnotationEdit("dest", "note", null, ["b"], null, false, true));

        AnnotationMutationResult moved = service.Move(source.Annotation!.Id,
            MemoryLocation.Create(0x400200, context.Modules.Module), AnnotationConflictResolution.Merge);

        Assert.True(moved.Succeeded);
        ResolvedMemoryAnnotation only = Assert.Single(service.GetAllAnnotations());
        Assert.Equal(source.Annotation.Id, only.Id);
        Assert.Equal("source", only.Record.Label);
        Assert.Equal("note", only.Record.Comment);
        Assert.Equal(["a", "b"], only.Record.Tags);
        Assert.True(only.Record.IsPinned);
    }

    [Fact]
    public void ChangeEventsAndJsonRoundTrip_PreserveExtendedFields()
    {
        using var first = new TempScope();
        using var second = new TempScope();
        TestContext context = TestContext.Create();
        var service = new VersionedMemoryMetadataService(context.Modules, context.Identities, first.Path, false);
        int eventCount = 0;
        service.Changed += (_, args) => eventCount += args.Ids.Count;
        service.Create(MemoryLocation.Create(0x400321, context.Modules.Module),
            new MemoryAnnotationEdit("entry", "comment", "group", ["x", "y"], "#55D96B", true, true));

        string json = service.ExportJson();
        var imported = new VersionedMemoryMetadataService(context.Modules, context.Identities, second.Path, false);
        AnnotationMutationResult result = imported.ImportJson(json, AnnotationConflictResolution.Replace);

        Assert.True(result.Succeeded);
        Assert.Equal(1, eventCount);
        MemoryAnnotationRecord record = Assert.Single(imported.GetAllAnnotations()).Record;
        Assert.Equal("group", record.Group);
        Assert.Equal(["x", "y"], record.Tags);
        Assert.Equal("#55D96B", record.Color);
    }

    private sealed class TempScope : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CrxMem.Tests", Guid.NewGuid().ToString("N"));
        public TempScope() { Directory.CreateDirectory(_directory); Path = System.IO.Path.Combine(_directory, "metadata.json"); }
        public string Path { get; }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }

    private sealed record TestContext(FakeModuleService Modules, FakeIdentityService Identities, ModuleIdentity Identity)
    {
        public static TestContext Create()
        {
            var module = new ModuleDescriptor("target.exe", 0x400000, 0x100000);
            var modules = new FakeModuleService(module);
            var identity = new ModuleIdentity("target.exe", module.Size, 1234, 5678, new string('D', 64));
            return new(modules, new FakeIdentityService(identity, modules), identity);
        }
    }

    private sealed class FakeModuleService(ModuleDescriptor module) : IModuleService
    {
        public ModuleDescriptor Module => module;
        public IReadOnlyList<ModuleDescriptor> GetModules() => [module];
        public ModuleDescriptor? FindByAddress(ulong address) => module.Contains(address) ? module : null;
        public ModuleDescriptor? FindByName(string name) => module.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? module : null;
        public ulong GetDefaultAddress() => module.BaseAddress;
    }

    private sealed class FakeIdentityService(ModuleIdentity current, FakeModuleService modules) : IModuleIdentityService
    {
        public ModuleIdentity? GetCurrent(string moduleName) => modules.Module.Name.Equals(moduleName, StringComparison.OrdinalIgnoreCase) ? current : null;
        public ModuleIdentityResolution Resolve(ModuleIdentity expected, ulong rva)
        {
            bool match = expected.Name.Equals(current.Name, StringComparison.OrdinalIgnoreCase) && expected.Sha256 == current.Sha256;
            return match ? new(true, modules.Module.BaseAddress + rva, "verified", current)
                : new(false, null, "fingerprint mismatch", current);
        }
    }
}
