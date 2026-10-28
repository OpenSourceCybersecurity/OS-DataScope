namespace DataScope.Scanner.Content;

/// <summary>
/// The reader full scans use (D130): the same readers with <see cref="ContentLimits.ForFullScan"/>. Without one, full
/// scans read with the quick-scan reader, still reading every file.
/// </summary>
public sealed class FullScanContentReader(IContentReader reader) : IDisposable
{
    public IContentReader Reader { get; } = reader;

    /// <summary>Stops its reader processes when the worker stops.</summary>
    public void Dispose() => (Reader as IDisposable)?.Dispose();
}
