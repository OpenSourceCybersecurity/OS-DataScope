using DataScope.Core.Classification;

namespace DataScope.Scanner.Content;

/// <summary>
/// Text extracted from one file, or the reason there is none. <see cref="Note"/> is a fixed, plain-English
/// explanation; it never contains file contents.
/// </summary>
public sealed record ContentExtraction(ContentState State, string? Text, string? Note)
{
    public static ContentExtraction Of(ContentState state, string note) => new(state, null, note);

    public bool HasText => Text is not null;
}

/// <summary>Reads a file's text for classification. Implementations open files read-only and never modify them.</summary>
public interface IContentReader
{
    /// <summary>Reasons to skip a file that need no file access (type, offline attribute, size), or null to read it.</summary>
    ContentExtraction? Precheck(string fileName, long size, FileAttributes attributes);

    ContentExtraction Read(string fullPath, CancellationToken ct);
}

/// <summary>A safety limit was hit, so the file is treated as unreadable rather than partially trusted.</summary>
public sealed class ContentLimitException(string message) : Exception(message);
