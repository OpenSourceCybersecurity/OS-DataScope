using DataScope.Core.Access;

namespace DataScope.ActiveDirectory.Tests;

public class DirectoryReaderTests
{
    [Fact]
    public void Converts_ad_value_formats()
    {
        Assert.Equal("northwind.test", LdapDirectoryReader.DomainName("DC=Northwind, DC=Test"));
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 22, 15, 0, TimeSpan.Zero), LdapDirectoryReader.GeneralizedTime("20261009221500.0Z"));
        Assert.Null(LdapDirectoryReader.GeneralizedTime("yesterday"));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), LdapDirectoryReader.FileTime(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Null(LdapDirectoryReader.FileTime("0"));
        Assert.Null(LdapDirectoryReader.FileTime("9223372036854775807"));
    }

    [Fact]
    public void Marks_groups_with_unmatched_members_incomplete()
    {
        DirectoryObject[] objects =
        [
            new("S-1-5-21-1-2-3-1001", PrincipalKind.User, "alice", "CN=alice,DC=x"),
            new("S-1-5-21-1-2-3-2001", PrincipalKind.Group, "Team", "CN=Team,DC=x"),
        ];
        var members = new Dictionary<string, IReadOnlyList<string>> { ["S-1-5-21-1-2-3-2001"] = ["cn=ALICE,dc=x", "CN=gone,DC=other"] };
        var (resolved, edges, unresolved) = Memberships.Resolve(objects, members);
        Assert.Equal([new MembershipEdge("S-1-5-21-1-2-3-2001", "S-1-5-21-1-2-3-1001")], edges);
        Assert.Equal(1, unresolved);
        var team = resolved.Single(o => o.Name == "Team");
        Assert.True(team.MembersIncomplete);
        Assert.Equal(2, team.DirectMemberCount);
    }

    [Fact]
    public void Refuses_a_password_without_tls_unless_allowed()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows sends credentials with Kerberos or NTLM, which protect them.
        }
        var options = new LdapDirectoryOptions { Server = "dc01", UserName = "reader", Password = "x" };
        Assert.Throws<InvalidOperationException>(options.Validate);
        options.UseLdaps = true;
        options.Validate();
        Assert.Throws<InvalidOperationException>(new LdapDirectoryOptions { PageSize = 1 }.Validate);
    }

    [Fact]
    public async Task Reads_the_lab_directory_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ds-lab-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {
              "domain": "northwind.test",
              "domainSid": "S-1-5-21-1-2-3",
              "principals": [
                { "sid": "S-1-5-21-1-2-3-1001", "kind": "User", "name": "alice", "department": "HR" },
                { "sid": "S-1-5-21-1-2-3-1002", "kind": "User", "name": "carol", "enabled": false, "manager": "S-1-5-21-1-2-3-1001" },
                { "sid": "S-1-5-21-1-2-3-513", "kind": "Group", "name": "Domain Users" },
                { "sid": "S-1-5-21-1-2-3-2001", "kind": "Group", "name": "HR_Read", "members": ["S-1-5-21-1-2-3-1001", "S-1-5-21-1-2-3-1002"] },
                { "sid": "S-1-5-21-1-2-3-2002", "kind": "Group", "name": "Partners", "members": ["S-1-5-21-9-9-9-1"] },
                { "sid": "not-a-sid", "kind": "User", "name": "ignored" }
              ]
            }
            """);
        try
        {
            var result = await new LabDirectoryReader(path).ReadAsync(CancellationToken.None);
            Assert.Equal("northwind.test", result.DomainName);
            Assert.Equal(5, result.Objects.Count);
            Assert.Equal("S-1-5-21-1-2-3-513", result.Objects.Single(o => o.Name == "alice").PrimaryGroupSid);
            Assert.False(result.Objects.Single(o => o.Name == "carol").Enabled);
            Assert.Equal(result.Objects.Single(o => o.Name == "alice").DistinguishedName, result.Objects.Single(o => o.Name == "carol").ManagerDn);
            Assert.Null(result.Objects.Single(o => o.Name == "alice").ManagerDn);
            Assert.Equal(2, result.Edges.Count);
            Assert.True(result.Objects.Single(o => o.Name == "Partners").MembersIncomplete);
            Assert.Equal(1, result.UnresolvedMembers);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Reports_a_missing_or_invalid_lab_file()
    {
        await Assert.ThrowsAsync<DirectoryReadException>(() => new LabDirectoryReader("/nonexistent/lab.json").ReadAsync(CancellationToken.None));
        var path = Path.Combine(Path.GetTempPath(), $"ds-lab-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """{ "domain": "x", "domainSid": "nope" }""");
        try
        {
            await Assert.ThrowsAsync<DirectoryReadException>(() => new LabDirectoryReader(path).ReadAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
