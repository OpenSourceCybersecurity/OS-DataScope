using DataScope.Core.Activity;
using DataScope.Tests.Activity;

namespace DataScope.Core.Tests.Activity;

public class ActivityInterpreterTests
{
    private static InterpretedEvent? Interpret(string xml) => ActivityInterpreter.Interpret(SecurityEventXml.Parse(xml));

    [Theory]
    [InlineData(0x1u, ActivityOperation.Read)]
    [InlineData(0x2u, ActivityOperation.Write)]
    [InlineData(0x4u, ActivityOperation.Write)]
    [InlineData(0x20u, ActivityOperation.Execute)]
    [InlineData(0x10000u, ActivityOperation.DeleteOrRename)]
    [InlineData(0x40000u, ActivityOperation.PermissionChange)]
    [InlineData(0x80000u, ActivityOperation.OwnerChange)]
    [InlineData(0x100u, ActivityOperation.AttributeChange)]
    [InlineData(0x80u, ActivityOperation.MetadataRead)]
    [InlineData(0x20000u, ActivityOperation.MetadataRead)]
    [InlineData(0x100000u, ActivityOperation.Other)]
    [InlineData(0x3u, ActivityOperation.Write)] // read and write together: the write matters more
    [InlineData(0x10003u, ActivityOperation.DeleteOrRename)]
    [InlineData(0x80000000u, ActivityOperation.Read)] // GENERIC_READ
    public void An_object_access_event_is_a_recorded_operation(uint mask, ActivityOperation expected)
    {
        var e = Interpret(EventXml.ObjectAccess(1, @"C:\Shares\Finance\Budget.xlsx", mask))!;
        Assert.Equal(expected, e.Operation);
        Assert.Equal(ActivityCertainty.Performed, e.Certainty);
        Assert.Equal(ActivityOutcome.Success, e.Outcome);
        Assert.Equal(mask, e.AccessMask);
        Assert.Equal(@"C:\Shares\Finance\Budget.xlsx", e.ObjectPath);
        Assert.Equal(EventXml.Alice, e.PrincipalSid);
        Assert.Equal(@"NORTHWIND\alice", e.PrincipalName);
        Assert.Equal("0x3e7a1", e.LogonId);
        Assert.Equal(4, e.ProcessId);
        Assert.Equal(0x1ac, e.HandleId);
        Assert.Null(e.ProcessName);
    }

    [Fact]
    public void A_share_access_check_is_never_a_read_even_when_it_checks_read_access()
    {
        var e = Interpret(EventXml.ShareCheck(1, "Finance", @"C:\Shares\Finance", @"Budget\2026.xlsx", 0x1))!;

        Assert.Equal(ActivityOperation.ShareAccessCheck, e.Operation);
        Assert.Equal(ActivityCertainty.AccessCheck, e.Certainty);
        Assert.Equal("Finance", e.ShareName);
        Assert.Equal(@"C:\Shares\Finance", e.ShareLocalPath);
        Assert.Equal(@"Budget\2026.xlsx", e.ShareRelativePath);
        Assert.Equal(@"C:\Shares\Finance\Budget\2026.xlsx", e.ObjectPath);
        Assert.Equal("10.0.0.25", e.ClientAddress);
        Assert.Contains("not proof", ActivityText.Certainty(e.Certainty));
    }

    [Fact]
    public void A_denied_share_access_check_is_a_failure()
    {
        var e = Interpret(EventXml.ShareCheck(1, "HR", @"C:\Shares\HR", "Salaries.xlsx", 0x1, denied: true))!;
        Assert.Equal(ActivityOutcome.Failure, e.Outcome);
        Assert.Equal(ActivityCertainty.AccessCheck, e.Certainty);
    }

    [Fact]
    public void A_share_check_on_the_share_itself_has_an_empty_relative_path()
    {
        var e = Interpret(EventXml.ShareCheck(1, "Finance", @"C:\Shares\Finance", @"\", 0x1))!;
        Assert.Equal("", e.ShareRelativePath);
        Assert.Equal(@"C:\Shares\Finance", e.ObjectPath);
    }

    [Fact]
    public void A_share_connection_says_nothing_about_files()
    {
        var e = Interpret(EventXml.ShareConnect(1, "Finance", @"C:\Shares\Finance"))!;
        Assert.Equal(ActivityOperation.ShareConnect, e.Operation);
        Assert.Equal(ActivityCertainty.ShareSession, e.Certainty);
        Assert.Equal(@"C:\Shares\Finance", e.ShareLocalPath);
        Assert.Equal("", e.ShareRelativePath);
    }

    [Fact]
    public void A_handle_request_is_a_request_and_a_denied_one_is_a_failure()
    {
        var granted = Interpret(EventXml.HandleRequest(1, @"C:\Shares\HR\Salaries.xlsx", 0x12019f))!;
        Assert.Equal(ActivityOperation.HandleRequest, granted.Operation);
        Assert.Equal(ActivityCertainty.Requested, granted.Certainty);
        Assert.Equal(ActivityOutcome.Success, granted.Outcome);

        var denied = Interpret(EventXml.HandleRequest(2, @"C:\Shares\HR\Salaries.xlsx", 0x1, denied: true))!;
        Assert.Equal(ActivityOutcome.Failure, denied.Outcome);
        Assert.Equal(ActivityCertainty.Requested, denied.Certainty);
    }

    [Fact]
    public void A_delete_event_has_no_path_of_its_own()
    {
        var e = Interpret(EventXml.Deleted(1))!;
        Assert.Equal(ActivityOperation.Deleted, e.Operation);
        Assert.Equal(ActivityCertainty.Performed, e.Certainty);
        Assert.Null(e.ObjectPath);
        Assert.Equal(0x1ac, e.HandleId);
    }

    [Fact]
    public void A_permission_change_keeps_the_old_and_new_permissions()
    {
        var e = Interpret(EventXml.PermissionsChanged(1, @"C:\Shares\HR", "D:AI(A;OICI;FA;;;BA)", "D:AI(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;WD)"))!;
        Assert.Equal(ActivityOperation.PermissionChange, e.Operation);
        Assert.Equal("D:AI(A;OICI;FA;;;BA)", e.OldSecurity);
        Assert.EndsWith("(A;OICI;0x1200a9;;;WD)", e.NewSecurity);
    }

    [Fact]
    public void A_cleared_log_is_recorded()
    {
        var e = Interpret(EventXml.LogCleared(1))!;
        Assert.Equal(ActivityOperation.LogCleared, e.Operation);
        Assert.Equal(EventXml.Alice, e.PrincipalSid);
    }

    [Fact]
    public void Registry_and_other_objects_are_not_file_activity()
    {
        var key = EventXml.ObjectAccess(1, @"\REGISTRY\MACHINE\SOFTWARE\x", 1).Replace("<Data Name='ObjectType'>File</Data>", "<Data Name='ObjectType'>Key</Data>");
        Assert.Null(Interpret(key));
    }

    [Fact]
    public void Look_alike_ids_from_another_provider_are_ignored()
    {
        Assert.Null(Interpret(EventXml.Event(4663, 1, [("ObjectType", "File"), ("ObjectName", @"C:\a.txt")], provider: "Some-App")));
        Assert.Null(Interpret(EventXml.Event(1102, 1, [], provider: "Microsoft-Windows-Security-Auditing")));
        Assert.Null(Interpret(EventXml.Event(4624, 1, [])));
    }

    [Fact]
    public void Dashes_and_bad_values_are_left_empty_not_guessed()
    {
        var e = Interpret(EventXml.Event(4663, 1, [("SubjectUserSid", "not-a-sid"), ("SubjectUserName", "-"), ("SubjectLogonId", "-"),
            ("ObjectType", "File"), ("ObjectName", @"C:\a.txt"), ("AccessMask", "bogus"), ("HandleId", "-"), ("ProcessId", "0x4")]))!;
        Assert.Null(e.PrincipalSid);
        Assert.Null(e.PrincipalName);
        Assert.Null(e.LogonId);
        Assert.Null(e.AccessMask);
        Assert.Null(e.HandleId);
        Assert.Equal(ActivityOperation.Other, e.Operation);
    }

    [Theory]
    [InlineData(@"\\*\Finance", "Finance")]
    [InlineData(@"\\FS01\Finance\", "Finance")]
    [InlineData("Finance", "Finance")]
    [InlineData("-", null)]
    [InlineData(null, null)]
    public void Reads_share_names(string? text, string? expected) => Assert.Equal(expected, ActivityInterpreter.ShareName(text));

    [Theory]
    [InlineData(ActivityOperation.Read, ActivityObjectKind.File, "Read")]
    [InlineData(ActivityOperation.Read, ActivityObjectKind.Folder, "List folder")]
    [InlineData(ActivityOperation.Read, ActivityObjectKind.Unknown, "Read or list folder")]
    [InlineData(ActivityOperation.Write, ActivityObjectKind.Folder, "Create in folder")]
    [InlineData(ActivityOperation.Execute, ActivityObjectKind.Unknown, "Run or traverse")]
    [InlineData(ActivityOperation.ShareAccessCheck, ActivityObjectKind.File, "Share access check")]
    public void Labels_say_what_windows_cannot_tell_apart(ActivityOperation op, ActivityObjectKind kind, string expected) =>
        Assert.Equal(expected, ActivityText.Operation(op, kind));

    [Fact]
    public void Describes_masks_with_windows_names()
    {
        Assert.Equal(["ReadData (or ListDirectory)", "DELETE"], FileAccessMask.Describe(0x10001));
        Assert.Equal(["SYNCHRONIZE", "0x200"], FileAccessMask.Describe(0x100200));
        Assert.Empty(FileAccessMask.Describe(0));
    }
}
