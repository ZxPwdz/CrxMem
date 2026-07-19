namespace CrxMem.MemoryView.Wpf.Services;

public sealed record HostCommandResult(bool Succeeded, string Message)
{
    public static HostCommandResult Success(string message) => new(true, message);
    public static HostCommandResult Failure(string message) => new(false, message);
}

public interface IMemoryViewHostBridge
{
    bool CanAddAddress { get; }
    HostCommandResult AddAddress(ulong address, string expression, string description);
}
