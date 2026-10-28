using DataScope.Core.Audit;

namespace DataScope.Core.Tests;

public class AuditChainTests
{
    private static List<AuditRecord> Chain(int n)
    {
        var list = new List<AuditRecord>();
        var prev = AuditChain.GenesisHash;
        var at = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
        for (var i = 1; i <= n; i++)
        {
            var hash = AuditChain.ComputeHash(i, at.AddMinutes(i), "S-1-5-21-1", "NW\\dana", "server.enroll", $"fs0{i}", "{}", prev);
            list.Add(new AuditRecord(i, at.AddMinutes(i), "S-1-5-21-1", "NW\\dana", "server.enroll", $"fs0{i}", "{}", prev, hash));
            prev = hash;
        }
        return list;
    }

    [Fact]
    public void Intact_chain_verifies()
    {
        var result = AuditChain.Verify(Chain(5));
        Assert.True(result.Valid);
        Assert.Equal(5, result.Checked);
    }

    [Fact]
    public void Edited_entry_is_detected()
    {
        var chain = Chain(4);
        chain[1] = chain[1] with { Target = "fs99" };
        var result = AuditChain.Verify(chain);
        Assert.False(result.Valid);
        Assert.Equal(2, result.FirstBrokenSequence);
    }

    [Fact]
    public void Deleted_entry_is_detected()
    {
        var chain = Chain(4);
        chain.RemoveAt(2);
        var result = AuditChain.Verify(chain);
        Assert.False(result.Valid);
        Assert.Equal(3, result.FirstBrokenSequence);
    }

    [Fact]
    public void Field_boundaries_cannot_be_shifted()
    {
        var at = DateTimeOffset.UnixEpoch;
        var a = AuditChain.ComputeHash(1, at, "x", "a|b", "c", "d", "{}", AuditChain.GenesisHash);
        var b = AuditChain.ComputeHash(1, at, "x", "a", "b|c", "d", "{}", AuditChain.GenesisHash);
        Assert.NotEqual(a, b);
    }
}
