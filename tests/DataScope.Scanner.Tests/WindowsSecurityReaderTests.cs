using System.Security.AccessControl;
using System.Security.Principal;
using DataScope.Core.Access;
using DataScope.Scanner.Security;

namespace DataScope.Scanner.Tests;

/// <summary>
/// The real Windows permission reader, against folders on the test machine. These run in the Windows CI job; on other
/// systems there is nothing to read and they return early.
/// </summary>
public sealed class WindowsSecurityReaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ds-winsec-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Reads_the_owner_and_entries_of_a_local_folder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var read = new WindowsSecurityReader().ReadFolder(_root);

        Assert.Equal(PermissionState.Read, read.State);
        Assert.Equal(read.Descriptor!.Length, SecurityDescriptorCodec.SelfRelativeLength(read.Descriptor));
        var sd = SecurityDescriptorCodec.Parse(read.Descriptor);
        Assert.NotNull(sd.Owner);
        Assert.NotEmpty(sd.Dacl!);
    }

    [Fact]
    public void Reads_a_folder_through_a_share_path_like_the_scanner_does()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        // The administrative share of the temp folder's drive, reached over SMB on this machine.
        var full = Path.GetFullPath(_root);
        var unc = $@"\\localhost\{full[0]}${full[2..]}";
        if (!Directory.Exists(unc))
        {
            return; // Administrative shares need an elevated account; the CI runner has one.
        }
        var reader = new WindowsSecurityReader();

        var overSmb = reader.ReadFolder(unc);
        var local = reader.ReadFolder(full);

        Assert.Equal(PermissionState.Read, overSmb.State);
        var remote = SecurityDescriptorCodec.Parse(overSmb.Descriptor!);
        Assert.Equal(SecurityDescriptorCodec.Parse(local.Descriptor!).Dacl, remote.Dacl);
        Assert.NotNull(remote.Owner);
    }

    [Fact]
    public void A_missing_folder_is_reported_as_not_read()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var read = new WindowsSecurityReader().ReadFolder(Path.Join(_root, "gone"));

        Assert.Equal(PermissionState.Unreadable, read.State);
        Assert.Null(read.Descriptor);
    }

    [Fact]
    public void A_folder_whose_permissions_are_hidden_from_the_account_is_reported_as_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        var hidden = Directory.CreateDirectory(Path.Join(_root, "hidden"));
        var original = hidden.GetAccessControl();
        var denied = hidden.GetAccessControl();
        // OWNER RIGHTS removes the owner's implicit right to read permissions, so the deny applies to the creator too.
        foreach (var who in new IdentityReference[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier("S-1-3-4") })
        {
            denied.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.ReadPermissions, AccessControlType.Deny));
        }
        hidden.SetAccessControl(denied);
        try
        {
            var read = new WindowsSecurityReader().ReadFolder(hidden.FullName);

            Assert.Equal(PermissionState.AccessDenied, read.State);
            Assert.Null(read.Descriptor);
        }
        finally
        {
            hidden.SetAccessControl(original);
        }
    }
}
