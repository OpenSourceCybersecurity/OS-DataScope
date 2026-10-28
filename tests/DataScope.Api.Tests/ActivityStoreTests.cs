using DataScope.Core.Activity;
using DataScope.Core.Inventory;
using DataScope.Persistence;
using DataScope.Persistence.Entities;
using DataScope.Tests.Activity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DataScope.Api.Tests;

/// <summary>Storing collected file events: idempotent, honest about what each proves, and matched to shares and files.</summary>
[Trait("Category", "Database")]
public sealed class ActivityStoreTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private const string FinanceRoot = @"D:\Shares\Finance";

    private sealed record Lab(Guid ServerId, Guid FinanceId, Guid HrId);

    /// <summary>A server with Finance (Budget\2026.xlsx, Budget\Old) and HR shares, and collection turned on.</summary>
    private async Task<Lab> SeedAsync(string host, bool knowFinancePath = false)
    {
        await using var db = database.CreateContext();
        var now = DateTimeOffset.UtcNow;
        var server = new ManagedServer { Id = Guid.NewGuid(), Hostname = host, CreatedAt = now, UpdatedAt = now, CreatedBySid = "S-1" };
        Share Share(string name) => new()
        {
            Id = Guid.NewGuid(),
            ServerId = server.Id,
            Name = name,
            NameKey = ShareName.Key(name),
            UncPath = $@"\\{host}\{name}",
            Kind = ShareKind.Disk,
            Included = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var finance = Share("Finance");
        var hr = Share("HR");
        if (knowFinancePath)
        {
            finance.LocalPath = FinanceRoot;
            finance.LocalPathSource = ShareLocalPathSource.Admin;
        }
        Folder Folder(Share share, string path, Guid? parent) => new()
        {
            Id = Guid.NewGuid(),
            ShareId = share.Id,
            ParentId = parent,
            Path = path,
            PathHash = InventoryStore.PathHash(path),
            Depth = path.Length == 0 ? 0 : path.Split('\\').Length,
        };
        var root = Folder(finance, "", null);
        var budget = Folder(finance, "Budget", root.Id);
        var old = Folder(finance, @"Budget\Old", budget.Id);
        var file = new InventoryFile { Id = Guid.NewGuid(), ShareId = finance.Id, FolderId = budget.Id, Name = "2026.xlsx", NameKey = "2026.XLSX", Extension = "xlsx" };
        db.AddRange(server, finance, hr);
        await db.SaveChangesAsync();
        db.AddRange(root);
        await db.SaveChangesAsync();
        db.AddRange(budget);
        await db.SaveChangesAsync();
        db.AddRange(old, file);
        await db.SaveChangesAsync();
        await new ActivityStore(db, TimeProvider.System).SetEnabledAsync(server.Id, true, "S-1", default);
        return new Lab(server.Id, finance.Id, hr.Id);
    }

    private async Task<IngestResult> Ingest(Lab lab, params string[] events)
    {
        await using var db = database.CreateContext();
        var raws = events.Select(x => new RawSecurityEvent(SecurityEventXml.Parse(x).RecordId, x)).ToList();
        return await new ActivityStore(db, TimeProvider.System).IngestAsync(lab.ServerId, ActivityStore.SecurityChannel, raws, default);
    }

    private async Task<List<FileAuditEvent>> Events(Lab lab)
    {
        await using var db = database.CreateContext();
        return await db.FileAuditEvents.AsNoTracking().Where(e => e.ServerId == lab.ServerId).OrderBy(e => e.RecordId).ToListAsync();
    }

    [Fact]
    public async Task Collecting_the_same_events_again_stores_them_once()
    {
        var lab = await SeedAsync("fs-act-1", knowFinancePath: true);
        string[] batch = [EventXml.ObjectAccess(10, $@"{FinanceRoot}\Budget\2026.xlsx", 0x1), EventXml.ObjectAccess(11, $@"{FinanceRoot}\Budget\2026.xlsx", 0x2)];

        var first = await Ingest(lab, batch);
        var again = await Ingest(lab, [.. batch, EventXml.ObjectAccess(12, $@"{FinanceRoot}\Budget", 0x1)]);

        Assert.Equal((2, 0), (first.Stored, first.Duplicates));
        Assert.Equal((1, 2), (again.Stored, again.Duplicates));
        Assert.Equal(12, again.LastRecordId);
        var events = await Events(lab);
        Assert.Equal([10L, 11, 12], events.Select(e => e.RecordId));
        await using var db = database.CreateContext();
        var source = await db.ActivitySources.SingleAsync(s => s.ServerId == lab.ServerId);
        Assert.Equal(3, source.EventsStored);
        Assert.Equal(12, source.LastRecordId);
    }

    [Fact]
    public async Task The_same_record_id_in_a_new_log_generation_is_a_different_event()
    {
        var lab = await SeedAsync("fs-act-2", knowFinancePath: true);
        var xml = EventXml.ObjectAccess(5, $@"{FinanceRoot}\Budget\2026.xlsx", 0x1);
        await Ingest(lab, xml);
        await using (var owner = database.CreateOwnerContext())
        {
            await owner.ActivitySources.Where(s => s.ServerId == lab.ServerId).ExecuteUpdateAsync(s => s.SetProperty(x => x.LogGeneration, 1));
        }
        Assert.Equal(1, (await Ingest(lab, xml)).Stored);
        Assert.Equal([0, 1], (await Events(lab)).Select(e => e.LogGeneration));
    }

    [Fact]
    public async Task Events_keep_what_they_prove_and_their_raw_text()
    {
        var lab = await SeedAsync("fs-act-3", knowFinancePath: true);
        var read = EventXml.ObjectAccess(1, $@"{FinanceRoot}\Budget\2026.xlsx", 0x1);
        await Ingest(lab, read,
            EventXml.HandleRequest(2, $@"{FinanceRoot}\Budget\2026.xlsx", 0x2, denied: true, sid: EventXml.Bob, user: "bob"),
            EventXml.ShareCheck(3, "Finance", FinanceRoot, @"Budget\2026.xlsx", 0x1),
            EventXml.ShareConnect(4, "Finance", FinanceRoot));

        var events = await Events(lab);
        Assert.Equal(
            [(ActivityOperation.Read, ActivityCertainty.Performed, ActivityOutcome.Success),
             (ActivityOperation.HandleRequest, ActivityCertainty.Requested, ActivityOutcome.Failure),
             (ActivityOperation.ShareAccessCheck, ActivityCertainty.AccessCheck, ActivityOutcome.Success),
             (ActivityOperation.ShareConnect, ActivityCertainty.ShareSession, ActivityOutcome.Success)],
            events.Select(e => (e.Operation, e.Certainty, e.Outcome)));
        Assert.Equal(read, events[0].RawXml);
        Assert.Equal(0x1, events[0].AccessMask);
        Assert.Equal(EventXml.Bob, events[1].PrincipalSid);
        Assert.All(events, e => Assert.Equal(lab.FinanceId, e.ShareId));
    }

    [Fact]
    public async Task Paths_are_matched_to_scanned_files_and_folders()
    {
        var lab = await SeedAsync("fs-act-4", knowFinancePath: true);
        await Ingest(lab,
            EventXml.ObjectAccess(1, $@"{FinanceRoot}\Budget\2026.xlsx", 0x1),
            EventXml.ObjectAccess(2, $@"{FinanceRoot}\BUDGET", 0x1),
            EventXml.ObjectAccess(3, $@"{FinanceRoot}\Budget\new.docx", 0x2),
            EventXml.ObjectAccess(4, FinanceRoot, 0x1),
            EventXml.ObjectAccess(5, @"C:\Windows\Temp\x.tmp", 0x2));

        var events = await Events(lab);
        Assert.Equal(
            [(ActivityObjectKind.File, @"Budget\2026.xlsx"), (ActivityObjectKind.Folder, "BUDGET"), (ActivityObjectKind.Unknown, @"Budget\new.docx"),
             (ActivityObjectKind.Folder, ""), (ActivityObjectKind.Unknown, null)],
            events.Select(e => (e.ObjectKind, e.RelativePath)));
        Assert.Null(events[4].ShareId);
        Assert.Equal(@"C:\Windows\Temp\x.tmp", events[4].ObjectPath);
    }

    [Fact]
    public async Task A_share_access_check_teaches_the_share_folder_and_earlier_events_are_matched_to_it()
    {
        var lab = await SeedAsync("fs-act-5");
        await Ingest(lab, EventXml.ObjectAccess(1, @"E:\Data\HR\Salaries.xlsx", 0x1), EventXml.Deleted(2, handle: 0x99));

        Assert.All(await Events(lab), e => Assert.Null(e.ShareId));

        await Ingest(lab, EventXml.ShareCheck(3, "hr", @"E:\Data\HR", "Salaries.xlsx", 0x1), EventXml.ObjectAccess(4, @"E:\Data\HR\Reviews\2026.docx", 0x2));

        var events = await Events(lab);
        Assert.Equal([lab.HrId, null, lab.HrId, lab.HrId], events.Select(e => e.ShareId));
        Assert.Equal(["Salaries.xlsx", null, "Salaries.xlsx", @"Reviews\2026.docx"], events.Select(e => e.RelativePath));
        await using var db = database.CreateContext();
        var hr = await db.Shares.SingleAsync(s => s.Id == lab.HrId);
        Assert.Equal((@"E:\Data\HR", ShareLocalPathSource.Event), (hr.LocalPath, hr.LocalPathSource));
    }

    [Fact]
    public async Task An_admin_set_share_folder_is_not_replaced_by_events()
    {
        var lab = await SeedAsync("fs-act-6", knowFinancePath: true);
        await Ingest(lab, EventXml.ShareCheck(1, "Finance", @"Z:\Elsewhere", "a.txt", 0x1));

        await using var db = database.CreateContext();
        Assert.Equal(FinanceRoot, (await db.Shares.SingleAsync(s => s.Id == lab.FinanceId)).LocalPath);
    }

    [Fact]
    public async Task Setting_a_share_folder_by_hand_matches_stored_events()
    {
        var lab = await SeedAsync("fs-act-7");
        await Ingest(lab, EventXml.ObjectAccess(1, @"F:\Finance\Budget\2026.xlsx", 0x1), EventXml.ObjectAccess(2, @"F:\FinanceArchive\a.xlsx", 0x1));

        await using (var db = database.CreateContext())
        {
            var share = await new ActivityStore(db, TimeProvider.System).SetShareLocalPathAsync(lab.FinanceId, @"F:\Finance\", default);
            Assert.Equal((@"F:\Finance", ShareLocalPathSource.Admin), (share!.LocalPath, share.LocalPathSource));
        }
        var events = await Events(lab);
        Assert.Equal([lab.FinanceId, null], events.Select(e => e.ShareId));
        Assert.Equal(@"Budget\2026.xlsx", events[0].RelativePath);
    }

    [Fact]
    public async Task A_delete_gets_its_path_from_the_event_that_opened_the_handle()
    {
        var lab = await SeedAsync("fs-act-8", knowFinancePath: true);
        await Ingest(lab,
            EventXml.ObjectAccess(1, $@"{FinanceRoot}\Budget\2026.xlsx", 0x10000, handle: 0x200),
            EventXml.Deleted(2, handle: 0x200));
        // Across batches, and the handle value reused for a read: only the latest event on the handle counts.
        await Ingest(lab, EventXml.ObjectAccess(3, $@"{FinanceRoot}\Budget\a.txt", 0x10000, handle: 0x300));
        await Ingest(lab,
            EventXml.Deleted(4, handle: 0x300),
            EventXml.ObjectAccess(5, $@"{FinanceRoot}\Budget\b.txt", 0x1, handle: 0x300),
            EventXml.Deleted(6, handle: 0x300));

        var events = await Events(lab);
        var deletes = events.Where(e => e.Operation == ActivityOperation.Deleted).ToList();
        Assert.Equal(
            [(ActivityPathSource.Correlated, (long?)1, $@"{FinanceRoot}\Budget\2026.xlsx", (Guid?)lab.FinanceId),
             (ActivityPathSource.Correlated, 3, $@"{FinanceRoot}\Budget\a.txt", lab.FinanceId),
             (ActivityPathSource.None, null, null, null)],
            deletes.Select(e => (e.PathSource, e.CorrelatedRecordId, e.ObjectPath, e.ShareId)));
        Assert.Equal(ActivityObjectKind.File, deletes[0].ObjectKind);
        Assert.Equal(ActivityOperation.DeleteOrRename, events[0].Operation);
    }

    [Fact]
    public async Task A_delete_is_not_matched_to_another_session_or_a_handle_never_opened_for_delete()
    {
        var lab = await SeedAsync("fs-act-9", knowFinancePath: true);
        await Ingest(lab,
            EventXml.ObjectAccess(1, $@"{FinanceRoot}\a.txt", 0x10000, logon: "0x111", handle: 0x40),
            EventXml.Deleted(2, logon: "0x222", handle: 0x40),
            EventXml.ObjectAccess(3, $@"{FinanceRoot}\b.txt", 0x1, logon: "0x333", handle: 0x50),
            EventXml.Deleted(4, logon: "0x333", handle: 0x50));

        var deletes = (await Events(lab)).Where(e => e.Operation == ActivityOperation.Deleted).ToList();
        Assert.All(deletes, d => Assert.Equal((ActivityPathSource.None, (string?)null), (d.PathSource, d.ObjectPath)));
    }

    [Fact]
    public async Task Unreadable_events_are_kept_as_received_and_other_objects_are_ignored()
    {
        var lab = await SeedAsync("fs-act-10", knowFinancePath: true);
        await using var db = database.CreateContext();
        var registry = EventXml.ObjectAccess(2, @"\REGISTRY\MACHINE\x", 1).Replace("'ObjectType'>File", "'ObjectType'>Key");
        var mismatched = EventXml.ObjectAccess(99, $@"{FinanceRoot}\a.txt", 1);
        var result = await new ActivityStore(db, TimeProvider.System).IngestAsync(lab.ServerId, ActivityStore.SecurityChannel,
            [new RawSecurityEvent(1, "<Event><System>broken"), new RawSecurityEvent(2, registry), new RawSecurityEvent(3, mismatched)], default);

        Assert.Equal((0, 1, 2), (result.Stored, result.Ignored, result.Unreadable));
        var events = await Events(lab);
        Assert.Equal([1L, 3], events.Select(e => e.RecordId));
        Assert.All(events, e => Assert.Equal(ActivityOperation.Unreadable, e.Operation));
        Assert.Equal("<Event><System>broken", events[0].RawXml);
        Assert.Contains("not well-formed", events[0].ParseError);
        Assert.Contains("does not match", events[1].ParseError);
        Assert.Equal(3, result.LastRecordId);
    }

    [Fact]
    public async Task The_services_cannot_change_or_delete_a_collected_event_only_match_it()
    {
        var lab = await SeedAsync("fs-act-11", knowFinancePath: true);
        await Ingest(lab, EventXml.ObjectAccess(1, $@"{FinanceRoot}\Budget\2026.xlsx", 0x1));
        var id = (await Events(lab)).Single().Id;

        await using (var runtime = database.CreateContext())
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                runtime.Database.ExecuteSqlAsync($"UPDATE file_audit_events SET principal_sid = 'S-1-5-21-9-9-9-500' WHERE id = {id}"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            await Assert.ThrowsAsync<PostgresException>(() => runtime.Database.ExecuteSqlAsync($"UPDATE file_audit_events SET raw_xml = '<Event/>' WHERE id = {id}"));
            Assert.Equal(1, await runtime.Database.ExecuteSqlAsync($"UPDATE file_audit_events SET object_kind = 'Unknown' WHERE id = {id}"));
            var deleted = await Assert.ThrowsAsync<PostgresException>(() => runtime.Database.ExecuteSqlAsync($"DELETE FROM file_audit_events WHERE id = {id}"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, deleted.SqlState);
        }
        // Even the schema owner cannot rewrite the event itself.
        await using var owner = database.CreateOwnerContext();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => owner.Database.ExecuteSqlAsync($"UPDATE file_audit_events SET operation = 'Read', event_time = now() WHERE id = {id}"));
        Assert.Contains("exactly as they were collected", ex.MessageText);
    }

    [Fact]
    public async Task Share_events_read_before_the_share_was_listed_are_used_once_it_is()
    {
        var lab = await SeedAsync("fs-act-12");
        Guid ops;
        await using (var db = database.CreateContext())
        {
            // HR and Finance exist; Operations is listed only by a later scan.
            await Ingest(lab, EventXml.ShareConnect(1, "OPERATIONS", @"E:\Ops"), EventXml.ObjectAccess(2, @"E:\Ops\Projects\plan.txt", 0x2));
            var now = DateTimeOffset.UtcNow;
            ops = Guid.NewGuid();
            db.Shares.Add(new Share { Id = ops, ServerId = lab.ServerId, Name = "Operations", NameKey = "OPERATIONS", UncPath = @"\\fs-act-12\Operations", Kind = ShareKind.Disk, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }
        await using (var db = database.CreateContext())
        {
            await new ActivityStore(db, TimeProvider.System).CatchUpAsync(lab.ServerId, ActivityStore.SecurityChannel, default);
        }
        var events = await Events(lab);
        Assert.Equal([ops, ops], events.Select(e => e.ShareId));
        Assert.Equal(["", @"Projects\plan.txt"], events.Select(e => e.RelativePath));
        Assert.Equal(("OPERATIONS", @"E:\Ops"), (events[0].EventShareName, events[0].EventShareLocalPath));
    }

    [Fact]
    public async Task Events_stored_before_a_scan_are_matched_to_files_and_folders_after_it()
    {
        var lab = await SeedAsync("fs-act-13", knowFinancePath: true);
        await Ingest(lab,
            EventXml.ObjectAccess(1, $@"{FinanceRoot}\Budget\later.xlsx", 0x1),
            EventXml.ObjectAccess(2, $@"{FinanceRoot}\Budget\Later Folder", 0x1),
            EventXml.ObjectAccess(3, $@"{FinanceRoot}\Budget\still-unknown.txt", 0x1));
        Assert.All(await Events(lab), e => Assert.Equal(ActivityObjectKind.Unknown, e.ObjectKind));

        await using (var db = database.CreateContext())
        {
            // A scan finds the new file and folder.
            var budget = await db.Folders.SingleAsync(f => f.ShareId == lab.FinanceId && f.Path == "Budget");
            db.Files.Add(new InventoryFile { Id = Guid.NewGuid(), ShareId = lab.FinanceId, FolderId = budget.Id, Name = "Later.xlsx", NameKey = "LATER.XLSX", Extension = "xlsx" });
            db.Folders.Add(new Folder { Id = Guid.NewGuid(), ShareId = lab.FinanceId, ParentId = budget.Id, Path = @"Budget\Later Folder", PathHash = InventoryStore.PathHash(@"Budget\Later Folder"), Depth = 2 });
            var now = DateTimeOffset.UtcNow;
            db.ScanJobs.Add(new ScanJob { Id = Guid.NewGuid(), ServerId = lab.ServerId, PolicyVersion = "test", RequestedBySid = "S-1", CreatedAt = now, UpdatedAt = now, FinishedAt = now, State = Core.ScanJobs.ScanJobState.Completed });
            await db.SaveChangesAsync();
        }
        await using (var db = database.CreateContext())
        {
            await new ActivityStore(db, TimeProvider.System).CatchUpAsync(lab.ServerId, ActivityStore.SecurityChannel, default);
        }
        Assert.Equal([ActivityObjectKind.File, ActivityObjectKind.Folder, ActivityObjectKind.Unknown], (await Events(lab)).Select(e => e.ObjectKind));
    }
}
