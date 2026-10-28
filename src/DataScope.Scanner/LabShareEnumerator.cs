using DataScope.Core.Inventory;

namespace DataScope.Scanner;

/// <summary>
/// Development and test stand-in for a file server: each folder under <c>{root}/{hostname}/</c> is a share.
/// Lets the full scan pipeline run on any OS against synthetic folder trees. Never registered in production.
/// </summary>
public sealed class LabShareEnumerator(string root) : IShareEnumerator
{
    public Task<IReadOnlyList<ShareInfo>> ListAsync(string hostname, CancellationToken ct)
    {
        var serverDir = Path.Join(root, hostname);
        if (!Directory.Exists(serverDir))
        {
            throw new ShareEnumerationException($"{hostname} could not be reached over SMB.");
        }
        IReadOnlyList<ShareInfo> shares = Directory.EnumerateDirectories(serverDir)
            .Select(d => Path.GetFileName(d))
            .Where(ShareName.IsValid)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(n => new ShareInfo(n, Path.Join(serverDir, n), null, n.EndsWith('$') ? ShareKind.Special : ShareKind.Disk))
            .ToList();
        return Task.FromResult(shares);
    }
}
