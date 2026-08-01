using System.Text;

namespace CrxMem.MemoryView.Wpf.Services;

public readonly record struct MemoryAnnotationId(Guid Value)
{
    public static MemoryAnnotationId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("D");
}

public enum MemoryAnnotationLocationKind { ModuleRelative, Absolute }
public enum MemoryAnnotationResolutionState { Resolved, StaleModule, StaleExecutable, Unavailable }
public enum AnnotationMutationError
{
    None, Conflict, StaleIdentity, InvalidAddress, UnsupportedExpression, Persistence, Cancelled, NotFound
}
public enum AnnotationConflictResolution { Cancel, Merge, Replace }

public sealed record MemoryAnnotationRecord(
    MemoryAnnotationId Id,
    MemoryAnnotationLocationKind LocationKind,
    ModuleIdentity? ModuleIdentity,
    ulong? ModuleRelativeAddress,
    ModuleIdentity? ExecutableIdentity,
    bool Is64Bit,
    ulong? AbsoluteAddress,
    string? Label,
    string? Comment,
    string? Group,
    IReadOnlyList<string> Tags,
    string? Color,
    bool IsPinned,
    bool IsBookmarked,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ResolvedMemoryAnnotation(
    MemoryAnnotationRecord Record,
    ulong? RuntimeAddress,
    string Expression,
    MemoryAnnotationResolutionState State,
    string Diagnostic)
{
    public MemoryAnnotationId Id => Record.Id;
}

public sealed record AnnotationMutationResult(
    bool Succeeded,
    AnnotationMutationError Error,
    string Message,
    ResolvedMemoryAnnotation? Annotation = null,
    ResolvedMemoryAnnotation? Conflict = null)
{
    public static AnnotationMutationResult Success(ResolvedMemoryAnnotation? annotation, string message = "Annotation saved.") =>
        new(true, AnnotationMutationError.None, message, annotation);
    public static AnnotationMutationResult Failure(AnnotationMutationError error, string message,
        ResolvedMemoryAnnotation? conflict = null) => new(false, error, message, null, conflict);
}

public sealed record MemoryAnnotationEdit(
    string? Label,
    string? Comment,
    string? Group,
    IReadOnlyList<string>? Tags,
    string? Color,
    bool IsPinned,
    bool IsBookmarked);

public sealed record MemoryAnnotationBulkEdit(
    string? Group = null,
    IReadOnlyList<string>? AddTags = null,
    string? Color = null,
    bool? IsPinned = null,
    bool? IsBookmarked = null);

public sealed class MemoryAnnotationsChangedEventArgs : EventArgs
{
    public MemoryAnnotationsChangedEventArgs(IEnumerable<MemoryAnnotationId> ids) => Ids = ids.Distinct().ToArray();
    public IReadOnlyList<MemoryAnnotationId> Ids { get; }
}

public interface IEnumerableMemoryMetadataService : IEditableMemoryMetadataService
{
    event EventHandler<MemoryAnnotationsChangedEventArgs>? Changed;
    IReadOnlyList<ResolvedMemoryAnnotation> GetAllAnnotations();
    ResolvedMemoryAnnotation? GetAnnotation(MemoryAnnotationId id);
    ResolvedMemoryAnnotation? GetAnnotation(MemoryLocation location);
    AnnotationMutationResult Create(MemoryLocation location, MemoryAnnotationEdit edit);
    AnnotationMutationResult Update(MemoryAnnotationId id, MemoryAnnotationEdit edit);
    AnnotationMutationResult Move(MemoryAnnotationId id, MemoryLocation destination, AnnotationConflictResolution resolution);
    AnnotationMutationResult RebindAbsolute(MemoryAnnotationId id, ulong address);
    AnnotationMutationResult Delete(IReadOnlyCollection<MemoryAnnotationId> ids);
    AnnotationMutationResult BulkUpdate(IReadOnlyCollection<MemoryAnnotationId> ids, MemoryAnnotationBulkEdit edit);
    string ExportJson(IReadOnlyCollection<MemoryAnnotationId>? ids = null);
    string ExportCsv(IReadOnlyCollection<MemoryAnnotationId>? ids = null);
    AnnotationMutationResult ImportJson(string json, AnnotationConflictResolution resolution);
}

internal static class AnnotationCsv
{
    public static string Escape(string? value)
    {
        value ??= string.Empty;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    public static string JoinTags(IReadOnlyList<string> tags) => string.Join(';', tags);
}
