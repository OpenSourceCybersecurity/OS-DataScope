using DataScope.Core.Access;

namespace DataScope.ActiveDirectory.Tests;

/// <summary>
/// Reads a real Active Directory schema: a Samba AD domain controller started by tests/lab/samba-dc.sh with synthetic
/// accounts. Needs DATASCOPE_TEST_LDAP; fails (is not skipped) without it, so a green run always means it ran.
/// Kerberos sign-in and Windows domain controllers are covered by the Windows lab checklist instead.
/// </summary>
[Trait("Category", "Directory")]
public sealed class SambaDirectoryTests : IAsyncLifetime
{
    public const string EnvVar = "DATASCOPE_TEST_LDAP";

    private DirectoryReadResult _result = null!;

    public static LdapDirectoryOptions Options()
    {
        var text = Environment.GetEnvironmentVariable(EnvVar)
            ?? throw new InvalidOperationException($"{EnvVar} is not set. Start a lab domain controller with tests/lab/samba-dc.sh (see docs/DEVELOPMENT.md).");
        var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0].Trim(), p => p.Length > 1 ? p[1] : "", StringComparer.OrdinalIgnoreCase);
        return new LdapDirectoryOptions
        {
            Server = parts.GetValueOrDefault("Server"),
            Port = int.Parse(parts.GetValueOrDefault("Port", "389"), System.Globalization.CultureInfo.InvariantCulture),
            UserName = parts.GetValueOrDefault("User"),
            Password = parts.GetValueOrDefault("Password"),
            AllowUnencryptedPassword = true,
            PageSize = 200,
        };
    }

    public async Task InitializeAsync() => _result = await new LdapDirectoryReader(Options()).ReadAsync(CancellationToken.None);

    public Task DisposeAsync() => Task.CompletedTask;

    private DirectoryObject Named(string name) => _result.Objects.Single(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool HasEdge(string group, string member) => _result.Edges.Contains(new MembershipEdge(Named(group).Sid, Named(member).Sid));

    [Fact]
    public void Reads_the_domain()
    {
        Assert.Equal("northwind.test", _result.DomainName);
        Assert.Equal("DC=northwind,DC=test", _result.NamingContext, ignoreCase: true);
        Assert.StartsWith("S-1-5-21-", _result.DomainSid, StringComparison.Ordinal);
        Assert.False(_result.Truncated);
    }

    [Fact]
    public void Reads_users_with_state_and_primary_group()
    {
        var alice = Named("alice");
        Assert.Equal(PrincipalKind.User, alice.Kind);
        Assert.True(alice.Enabled);
        Assert.Equal("HR", alice.Department);
        Assert.Equal(_result.DomainSid + "-513", alice.PrimaryGroupSid);
        Assert.StartsWith(_result.DomainSid + "-", alice.Sid, StringComparison.Ordinal);
        Assert.False(Named("carol").Enabled);
    }

    [Fact]
    public void Reads_who_each_user_reports_to()
    {
        Assert.Equal(Named("bob").DistinguishedName, Named("alice").ManagerDn, ignoreCase: true);
        Assert.Null(Named("bob").ManagerDn);
        Assert.Null(Named("HR_Read").ManagerDn);
    }

    [Fact]
    public void Reads_groups_nesting_and_loops()
    {
        Assert.True(HasEdge("HR_Read", "alice"));
        Assert.True(HasEdge("Finance_Read", "Finance_Reviewers"));
        Assert.True(HasEdge("Finance_Reviewers", "dave"));
        Assert.True(HasEdge("Loop_A", "Loop_B"));
        Assert.True(HasEdge("Loop_B", "Loop_A"));
        Assert.True(HasEdge("AllStaff", "Domain Users"));
        Assert.False(Named("Newsletter").IsSecurityGroup);
        Assert.Equal(GroupScope.Global, Named("HR_Read").Scope);
    }

    [Fact]
    public void Reads_all_members_of_a_large_group()
    {
        var big = Named("Big_Group");
        Assert.Equal(1600, _result.Edges.Count(e => e.GroupSid == big.Sid));
        Assert.Equal(1600, big.DirectMemberCount);
        Assert.False(big.MembersIncomplete);
    }

    [Fact]
    public void Keeps_foreign_members_as_unexpandable()
    {
        var partners = Named("Partners");
        var foreign = _result.Objects.Single(o => o.Sid == "S-1-5-21-9000-9000-9000-4001");
        Assert.Equal(PrincipalKind.ForeignSecurityPrincipal, foreign.Kind);
        Assert.Contains(new MembershipEdge(partners.Sid, foreign.Sid), _result.Edges);
    }

    [Fact]
    public void Leaves_out_the_domain_controllers_builtin_groups()
    {
        Assert.DoesNotContain(_result.Objects, o => o.Sid.StartsWith(WellKnownSid.BuiltinPrefix, StringComparison.Ordinal));
        Assert.DoesNotContain(_result.Edges, e => e.GroupSid.StartsWith(WellKnownSid.BuiltinPrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Feeds_the_access_engine()
    {
        var graph = new DirectoryGraph(_result.Objects.Select(o => o.ToEntry()), _result.Edges, null, _result.DomainSid);
        var dave = graph.TokenFor(Named("dave").Sid);
        Assert.Equal(Membership.Yes, dave.Contains(Named("Finance_Read").Sid));
        Assert.Equal(Membership.Yes, dave.Contains(Named("AllStaff").Sid));
        Assert.Equal([Named("dave").Sid, _result.DomainSid + "-513", Named("AllStaff").Sid], dave.ChainTo(Named("AllStaff").Sid));
        Assert.Equal(Membership.No, graph.TokenFor(Named("eve").Sid).Contains(Named("Newsletter").Sid));
        Assert.Equal(Membership.Maybe, graph.TokenFor(Named("eve").Sid).Contains(Named("Partners").Sid));
        Assert.Contains(graph.Cycles, c => c.Contains(Named("Loop_A").Sid) && c.Contains(Named("Loop_B").Sid));
    }

    [Fact]
    public async Task Reports_a_wrong_password_plainly()
    {
        var options = Options();
        options.Password = "wrong";
        var ex = await Assert.ThrowsAsync<DirectoryReadException>(() => new LdapDirectoryReader(options).ReadAsync(CancellationToken.None));
        Assert.Contains("refused the sign-in", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stops_at_the_object_limit_and_says_so()
    {
        var options = Options();
        options.MaxObjects = 50;
        var result = await new LdapDirectoryReader(options).ReadAsync(CancellationToken.None);
        Assert.True(result.Truncated);
        Assert.Equal(50, result.Objects.Count);
        Assert.Contains(result.Warnings, w => w.Contains("Stopped after 50", StringComparison.Ordinal));
    }
}
