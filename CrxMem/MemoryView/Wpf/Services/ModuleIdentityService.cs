using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed record ModuleIdentity(
    string Name,
    ulong ImageSize,
    long FileLength,
    long LastWriteUtcTicks,
    string Sha256);

public sealed record ModuleIdentityResolution(
    bool IsMatch,
    ulong? RuntimeAddress,
    string Reason,
    ModuleIdentity? CurrentIdentity = null);

public interface IModuleIdentityService
{
    ModuleIdentity? GetCurrent(string moduleName);
    ModuleIdentityResolution Resolve(ModuleIdentity expected, ulong relativeVirtualAddress);
}

public sealed class FileModuleIdentityService : IModuleIdentityService
{
    private readonly IModuleService _modules;
    private readonly ConcurrentDictionary<string, ModuleIdentity> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FileModuleIdentityService(IModuleService modules) =>
        _modules = modules ?? throw new ArgumentNullException(nameof(modules));

    public ModuleIdentity? GetCurrent(string moduleName)
    {
        ModuleDescriptor? module = _modules.FindByName(moduleName);
        if (module == null || string.IsNullOrWhiteSpace(module.FilePath)) return null;
        try
        {
            string path = Path.GetFullPath(module.FilePath);
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            string cacheKey = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{module.Size}";
            return _cache.GetOrAdd(cacheKey, _ => new ModuleIdentity(
                module.Name,
                module.Size,
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                ComputeSha256(path)));
        }
        catch
        {
            return null;
        }
    }

    public ModuleIdentityResolution Resolve(ModuleIdentity expected, ulong relativeVirtualAddress)
    {
        ModuleDescriptor? module = _modules.FindByName(expected.Name);
        if (module == null) return new(false, null, $"Module '{expected.Name}' is not loaded.");
        ModuleIdentity? current = GetCurrent(expected.Name);
        if (current == null) return new(false, null, $"Module '{expected.Name}' could not be fingerprinted.");
        if (current.ImageSize != expected.ImageSize || current.FileLength != expected.FileLength ||
            !current.Sha256.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
            return new(false, null, $"Module '{expected.Name}' does not match the persisted binary fingerprint.", current);
        if (relativeVirtualAddress >= module.Size)
            return new(false, null, $"RVA 0x{relativeVirtualAddress:X} is outside module '{expected.Name}'.", current);
        return new(true, module.BaseAddress + relativeVirtualAddress, "Module identity verified.", current);
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 1024 * 128, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
