using DataScope.Core.Access;
using DataScope.Scanner.Security;

namespace DataScope.Scanner.Tests;

/// <summary>The development permission reader must propagate NTFS inheritance the way Windows does.</summary>
public sealed class LabSecurityReaderTests : IDisposable
{
    private const string Hr = "S-1-5-21-1-2-3-1201";
    private const string Fin = "S-1-5-21-1-2-3-1203";
    private const string Dave = "S-1-5-21-1-2-3-1104";
    private readonly string _root = Directory.CreateTempSubdirectory("ds-labsec-").FullName;

    public LabSecurityReaderTests()
    {
        foreach (var dir in new[] { "HR/Payroll/2025", "HR/Open/Deeper", "HR/Files/Child", "HR/Hidden" })
        {
            Directory.CreateDirectory(Path.Join(_root, "fs1", dir));
        }
        File.WriteAllText(Path.Join(_root, "fs1.security.json"), $$"""
            {
              "localGroups": [ { "sid": "S-1-5-32-545", "name": "BUILTIN\\Users", "members": [ { "sid": "S-1-5-11", "kind": "WellKnownGroup" } ] } ],
              "shares": { "HR": [ { "sid": "S-1-5-11", "rights": "Change" } ], "Secret": null },
              "folders": {
                "HR": { "owner": "S-1-5-32-544", "aces": [ { "sid": "{{Hr}}", "rights": "Read" }, { "sid": "S-1-5-32-544", "rights": "Full", "applies": "subfolders" } ] },
                "HR/Payroll": { "protected": true, "aces": [ { "sid": "{{Fin}}", "rights": "Modify" } ] },
                "HR/Open": { "aces": [ { "sid": "{{Dave}}", "deny": true }, { "sid": "S-1-1-0", "rights": "Read", "applies": "folder" } ] },
                "HR/Files": { "aces": [ { "sid": "S-1-1-0", "rights": "Read", "applies": "files" } ] }
              },
              "denied": [ "HR/Hidden" ]
            }
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private SecurityDescriptor Folder(LabSecurityReader reader, string path)
    {
        var read = reader.ReadFolder(Path.Join(_root, "fs1", path));
        Assert.Equal(PermissionState.Read, read.State);
        return SecurityDescriptorCodec.Parse(read.Descriptor);
    }

    [Fact]
    public void Share_root_is_protected_and_children_inherit_with_the_inherited_flag()
    {
        var reader = new LabSecurityReader(_root);
        var root = Folder(reader, "HR");
        Assert.True(root.IsProtected);
        Assert.Equal(WellKnownSid.Administrators, root.Owner);
        Assert.All(root.Dacl!, a => Assert.False(a.IsInherited));

        var deeper = Folder(reader, "HR/Open/Deeper");
        Assert.False(deeper.IsProtected);
        Assert.All(deeper.Dacl!, a => Assert.True(a.IsInherited));
        Assert.Contains(deeper.Dacl!, a => a.Sid == Hr && a.AppliesToObject);
        // "This folder only" does not pass down; a deny does.
        Assert.DoesNotContain(deeper.Dacl!, a => a.Sid == WellKnownSid.Everyone);
        Assert.Contains(deeper.Dacl!, a => a.Sid == Dave && a.IsDeny);
    }

    [Fact]
    public void Explicit_entries_come_first_with_denies_before_allows()
    {
        var open = Folder(new LabSecurityReader(_root), "HR/Open");
        Assert.True(open.IsCanonical);
        Assert.Equal(Dave, open.Dacl![0].Sid);
        Assert.True(open.Dacl[0].IsDeny);
        Assert.False(open.Dacl[1].IsInherited);
        Assert.True(open.HasExplicitEntries);
    }

    [Fact]
    public void Protected_folders_drop_inherited_entries()
    {
        var reader = new LabSecurityReader(_root);
        var payroll = Folder(reader, "HR/Payroll");
        Assert.True(payroll.IsProtected);
        Assert.Equal([Fin], payroll.Dacl!.Select(a => a.Sid));
        Assert.Equal([Fin], Folder(reader, "HR/Payroll/2025").Dacl!.Select(a => a.Sid));
    }

    [Fact]
    public void Files_only_and_subfolders_only_entries_reach_the_right_targets()
    {
        var reader = new LabSecurityReader(_root);
        var files = Folder(reader, "HR/Files");
        var everyone = files.Dacl!.Single(a => a.Sid == WellKnownSid.Everyone);
        Assert.False(everyone.AppliesToObject);
        Assert.True(everyone.AppliesToChildFiles);
        // Files-only entries pass through subfolders to their files, without applying to the subfolders themselves.
        var child = Folder(reader, "HR/Files/Child").Dacl!.Single(a => a.Sid == WellKnownSid.Everyone);
        Assert.False(child.AppliesToObject);
        Assert.True(child.AppliesToChildFiles);
        Assert.True(child.IsInherited);

        // The subfolders-only entry on the root does not apply to the root but applies to a child folder.
        Assert.False(Folder(reader, "HR").Dacl!.Single(a => a.Sid == WellKnownSid.Administrators).AppliesToObject);
        Assert.True(Folder(reader, "HR/Files").Dacl!.Single(a => a.Sid == WellKnownSid.Administrators).AppliesToObject);
    }

    [Fact]
    public void Denied_and_missing_reads_are_reported_not_invented()
    {
        var reader = new LabSecurityReader(_root);
        Assert.Equal(PermissionState.AccessDenied, reader.ReadFolder(Path.Join(_root, "fs1", "HR", "Hidden")).State);
        Assert.Equal(PermissionState.Unreadable, reader.ReadFolder(Path.Join(_root, "fs1", "HR", "Gone")).State);
        Assert.Equal(PermissionState.AccessDenied, reader.ReadShare("fs1", "Secret").State);

        var share = reader.ReadShare("fs1", "HR");
        Assert.Equal(PermissionState.Read, share.State);
        var ace = Assert.Single(SecurityDescriptorCodec.Parse(share.Descriptor).Dacl!);
        Assert.Equal(AccessMask.ShareChange, ace.Mask);

        Assert.Equal(LocalGroupsState.Read, reader.ReadLocalGroups("fs1").State);
        var other = reader.ReadLocalGroups("fs2");
        Assert.Equal(LocalGroupsState.AccessDenied, other.State);
        Assert.Empty(other.Groups);
        Assert.Equal(PermissionState.AccessDenied, reader.ReadShare("fs2", "Anything").State);
    }
}
