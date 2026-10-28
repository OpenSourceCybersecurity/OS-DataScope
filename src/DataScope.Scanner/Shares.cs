using DataScope.Core.Inventory;

namespace DataScope.Scanner;

/// <summary>A share as reported by the server.</summary>
/// <param name="AccessPath">Path the scanner lists, normally \\host\share.</param>
public sealed record ShareInfo(string Name, string AccessPath, string? Remark, ShareKind Kind);

/// <summary>Share listing failed for the whole server (unreachable, access denied, not Windows).</summary>
public sealed class ShareEnumerationException : Exception
{
    public ShareEnumerationException() : base("Share enumeration failed.") { }

    public ShareEnumerationException(string message) : base(message) { }

    public ShareEnumerationException(string message, Exception inner) : base(message, inner) { }
}

public interface IShareEnumerator
{
    Task<IReadOnlyList<ShareInfo>> ListAsync(string hostname, CancellationToken ct);
}
