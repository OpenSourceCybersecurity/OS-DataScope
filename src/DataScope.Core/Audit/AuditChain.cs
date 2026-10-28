using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DataScope.Core.Audit;

/// <summary>One link in the admin audit log's hash chain.</summary>
public sealed record AuditRecord(
    long Sequence,
    DateTimeOffset At,
    string ActorSid,
    string ActorName,
    string Action,
    string Target,
    string Details,
    string PreviousHash,
    string Hash);

/// <summary>
/// Tamper-evident chain for the admin audit log. Each entry's hash covers its
/// content and the previous entry's hash, so editing or deleting a row breaks
/// every later link. Database administrators remain inside the trust boundary:
/// they could rewrite the whole chain, which is documented in the threat model.
/// </summary>
public static class AuditChain
{
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputeHash(long sequence, DateTimeOffset at, string actorSid, string actorName, string action, string target, string details, string previousHash)
    {
        // Length-prefix every field so values containing the separator cannot collide.
        var builder = new StringBuilder();
        foreach (var field in new[]
                 {
                     sequence.ToString(CultureInfo.InvariantCulture),
                     at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                     actorSid, actorName, action, target, details, previousHash,
                 })
        {
            builder.Append(field.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(field).Append('|');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public sealed record VerifyResult(bool Valid, long Checked, long? FirstBrokenSequence, string Message);

    /// <summary>Verifies records ordered by sequence, starting from the genesis entry.</summary>
    public static VerifyResult Verify(IEnumerable<AuditRecord> records)
    {
        var expectedPrevious = GenesisHash;
        long expectedSequence = 1;
        long count = 0;
        foreach (var r in records)
        {
            if (r.Sequence != expectedSequence)
            {
                return new(false, count, expectedSequence, $"Entry {expectedSequence} is missing.");
            }
            if (!string.Equals(r.PreviousHash, expectedPrevious, StringComparison.Ordinal))
            {
                return new(false, count, r.Sequence, $"Entry {r.Sequence} does not link to the entry before it.");
            }
            var actual = ComputeHash(r.Sequence, r.At, r.ActorSid, r.ActorName, r.Action, r.Target, r.Details, r.PreviousHash);
            if (!string.Equals(actual, r.Hash, StringComparison.Ordinal))
            {
                return new(false, count, r.Sequence, $"Entry {r.Sequence} was changed after it was written.");
            }
            expectedPrevious = r.Hash;
            expectedSequence++;
            count++;
        }
        return new(true, count, null, count == 0 ? "The audit log is empty." : $"All {count} entries are intact.");
    }
}
