using DataScope.Core.Access;
using DataScope.Core.Classification;
using DataScope.Core.Inventory;
using DataScope.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace DataScope.Persistence;

public sealed class DataScopeDbContext(DbContextOptions<DataScopeDbContext> options) : DbContext(options)
{
    /// <summary>The longest name or SID kept for the person an alert or baseline is about.</summary>
    private const int MaxPersonLength = 320;

    public DbSet<ManagedServer> ManagedServers => Set<ManagedServer>();
    public DbSet<ScanJob> ScanJobs => Set<ScanJob>();
    public DbSet<AppAuditEntry> AppAuditLog => Set<AppAuditEntry>();
    public DbSet<Share> Shares => Set<Share>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<InventoryFile> Files => Set<InventoryFile>();
    public DbSet<ScanGap> ScanGaps => Set<ScanGap>();
    public DbSet<ShareScanHistory> ShareScanHistory => Set<ShareScanHistory>();
    public DbSet<ScanFrontierItem> ScanFrontier => Set<ScanFrontierItem>();
    public DbSet<ClassificationFinding> Findings => Set<ClassificationFinding>();
    public DbSet<ClassificationOverride> ClassificationOverrides => Set<ClassificationOverride>();
    public DbSet<DirectorySnapshot> DirectorySnapshots => Set<DirectorySnapshot>();
    public DbSet<DirectoryPrincipal> DirectoryPrincipals => Set<DirectoryPrincipal>();
    public DbSet<DirectoryMembership> DirectoryMemberships => Set<DirectoryMembership>();
    public DbSet<StoredSecurityDescriptor> SecurityDescriptors => Set<StoredSecurityDescriptor>();
    public DbSet<StoredAce> SecurityAces => Set<StoredAce>();
    public DbSet<FolderSecurityChange> FolderSecurityChanges => Set<FolderSecurityChange>();
    public DbSet<ServerLocalGroup> ServerLocalGroups => Set<ServerLocalGroup>();
    public DbSet<ServerLocalGroupMember> ServerLocalGroupMembers => Set<ServerLocalGroupMember>();
    public DbSet<OwnerAssignment> OwnerAssignments => Set<OwnerAssignment>();
    public DbSet<ReviewCampaign> ReviewCampaigns => Set<ReviewCampaign>();
    public DbSet<AccessReview> AccessReviews => Set<AccessReview>();
    public DbSet<ReviewItem> ReviewItems => Set<ReviewItem>();
    public DbSet<ReviewItemUser> ReviewItemUsers => Set<ReviewItemUser>();
    public DbSet<ReviewDecision> ReviewDecisions => Set<ReviewDecision>();
    public DbSet<ReviewEvent> ReviewEvents => Set<ReviewEvent>();
    public DbSet<ReviewExemption> ReviewExemptions => Set<ReviewExemption>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<ScanExclusion> ScanExclusions => Set<ScanExclusion>();
    public DbSet<ComplianceStandardSetting> ComplianceStandards => Set<ComplianceStandardSetting>();
    public DbSet<CustomDetectionRule> CustomRules => Set<CustomDetectionRule>();
    public DbSet<RiskDecision> RiskDecisions => Set<RiskDecision>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    public DbSet<FileAuditEvent> FileAuditEvents => Set<FileAuditEvent>();
    public DbSet<ActivitySource> ActivitySources => Set<ActivitySource>();
    public DbSet<ActivityGap> ActivityGaps => Set<ActivityGap>();
    public DbSet<ActivityAlert> ActivityAlerts => Set<ActivityAlert>();
    public DbSet<ActivityBaseline> ActivityBaselines => Set<ActivityBaseline>();
    public DbSet<PostureSnapshot> PostureSnapshots => Set<PostureSnapshot>();
    public DbSet<ServiceHeartbeat> ServiceHeartbeats => Set<ServiceHeartbeat>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Trigram indexes for the search box (D128). pg_trgm ships with PostgreSQL and is trusted, so the database owner can add it.
        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.Entity<ManagedServer>(e =>
        {
            e.ToTable("managed_servers");
            e.HasKey(x => x.Id);
            e.Property(x => x.Hostname).HasMaxLength(253);
            e.HasIndex(x => x.Hostname).IsUnique();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24).HasDefaultValue(SourceKind.FileServer).HasSentinel(SourceKind.FileServer);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.Property(x => x.CreatedBySid).HasMaxLength(184);
            e.Property(x => x.LocalGroupsState).HasConversion<string>().HasMaxLength(16).HasDefaultValue(LocalGroupsState.NotRead).HasSentinel(LocalGroupsState.NotRead);
            e.Property(x => x.ScanFrequency).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ScanDay).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.FullScanFrequency).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.FullScanDay).HasConversion<string>().HasMaxLength(16);
            e.Ignore(x => x.Schedule);
            e.Ignore(x => x.FullScan);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_managed_servers_scan_hours", "scan_start_hour BETWEEN 0 AND 23 AND (scan_stop_hour IS NULL OR (scan_stop_hour BETWEEN 0 AND 23 AND scan_stop_hour <> scan_start_hour))");
                t.HasCheckConstraint("ck_managed_servers_scan_day", "scan_frequency <> 'Weekly' OR scan_day IS NOT NULL");
                t.HasCheckConstraint("ck_managed_servers_scan_minute", "scan_start_minute BETWEEN 0 AND 59");
                t.HasCheckConstraint("ck_managed_servers_scan_every", "scan_frequency <> 'Hourly' OR scan_every_hours IN (1, 2, 3, 4, 6, 8, 12)");
                t.HasCheckConstraint("ck_managed_servers_full_scan_day", "full_scan_frequency = 'Off' OR full_scan_day IS NOT NULL");
            });
            e.Property(x => x.RowVersion).IsRowVersion();
        });

        modelBuilder.Entity<ScanJob>(e =>
        {
            e.ToTable("scan_jobs");
            e.HasKey(x => x.Id);
            // Restrict: deleting a server must never silently erase scan history.
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.RequestedState).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.PolicyVersion).HasMaxLength(64);
            e.Property(x => x.Checkpoint).HasColumnType("jsonb");
            e.Property(x => x.LeaseOwner).HasMaxLength(200);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.Property(x => x.RequestedBySid).HasMaxLength(184);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.State, x.CreatedAt });
        });

        modelBuilder.Entity<AppAuditEntry>(e =>
        {
            e.ToTable("app_audit_log");
            e.HasKey(x => x.Sequence);
            // Sequence is assigned by the writer under an advisory lock so the hash chain has no gaps.
            e.Property(x => x.Sequence).ValueGeneratedNever();
            e.Property(x => x.ActorSid).HasMaxLength(184);
            e.Property(x => x.ActorName).HasMaxLength(256);
            e.Property(x => x.Action).HasMaxLength(128);
            e.Property(x => x.Target).HasMaxLength(512);
            // json, not jsonb: jsonb rewrites spacing and key order, which would break the hash chain.
            e.Property(x => x.Details).HasColumnType("json");
            e.Property(x => x.PreviousHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.Hash).HasMaxLength(64).IsFixedLength();
            e.HasIndex(x => x.At);
        });

        modelBuilder.Entity<Share>(e =>
        {
            e.ToTable("shares");
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.NameKey).HasMaxLength(80);
            e.Property(x => x.UncPath).HasMaxLength(400);
            e.Property(x => x.Remark).HasMaxLength(256);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ExclusionReason).HasMaxLength(500);
            e.Property(x => x.ExcludedBySid).HasMaxLength(184);
            e.Property(x => x.ExcludedByName).HasMaxLength(256);
            e.Property(x => x.SecurityState).HasConversion<string>().HasMaxLength(16).HasDefaultValue(PermissionState.NotRead);
            e.HasOne<StoredSecurityDescriptor>().WithMany().HasForeignKey(x => x.SecurityId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.LocalPath).HasMaxLength(1024);
            e.Property(x => x.LocalPathSource).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.ServerId, x.NameKey }).IsUnique();
        });

        // Inventory rows are keyed by share and path, and kept (marked removed) rather than deleted,
        // so findings and reviews that point at them stay interpretable.
        modelBuilder.Entity<Folder>(e =>
        {
            e.ToTable("folders");
            e.HasKey(x => x.Id);
            e.HasOne<Share>().WithMany().HasForeignKey(x => x.ShareId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Folder>().WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.PathHash).HasMaxLength(32);
            e.HasIndex(x => new { x.ShareId, x.PathHash }).IsUnique();
            e.HasIndex(x => x.ParentId);
            e.HasIndex(x => x.LastWalkedJobId);
            e.Property(x => x.SecurityState).HasConversion<string>().HasMaxLength(16).HasDefaultValue(PermissionState.NotRead);
            e.HasOne<StoredSecurityDescriptor>().WithMany().HasForeignKey(x => x.SecurityId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.SecurityId);
            e.HasIndex(x => x.RemovedAt).HasFilter("removed_at IS NOT NULL");
            // The search box (D128): folder names anywhere in the path, by trigrams, among current folders.
            e.HasIndex(x => x.Path).HasMethod("gin").HasOperators("gin_trgm_ops").HasFilter("removed_at IS NULL").HasDatabaseName("ix_folders_current_path_trgm");
        });

        modelBuilder.Entity<InventoryFile>(e =>
        {
            e.ToTable("files");
            e.HasKey(x => x.Id);
            e.HasOne<Share>().WithMany().HasForeignKey(x => x.ShareId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Folder>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Name).HasMaxLength(255);
            e.Property(x => x.NameKey).HasMaxLength(255);
            e.Property(x => x.Extension).HasMaxLength(32);
            e.HasIndex(x => new { x.FolderId, x.NameKey }).IsUnique();
            e.HasIndex(x => new { x.ShareId, x.Extension });
            e.Property(x => x.Sensitivity).HasConversion<string>().HasMaxLength(16).HasDefaultValue(Sensitivity.Unknown);
            e.Property(x => x.OverrideSensitivity).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ContentState).HasConversion<string>().HasMaxLength(16).HasDefaultValue(ContentState.NotRead);
            e.Property(x => x.ContentNote).HasMaxLength(500);
            e.Property(x => x.ClassifiedPolicy).HasMaxLength(64);
            e.HasIndex(x => new { x.ShareId, x.Sensitivity });
            // Duplicate files (D119): copies are looked up by size and name among current files.
            e.HasIndex(x => new { x.Size, x.NameKey }).HasFilter("removed_at IS NULL").HasDatabaseName("ix_files_current_size_name_key");
            // Only removed files: for retention, without weighing on the live rows.
            e.HasIndex(x => x.RemovedAt).HasFilter("removed_at IS NOT NULL");
            // The search box (D128): any part of a current file's name, by trigrams.
            e.HasIndex(x => x.Name).HasMethod("gin").HasOperators("gin_trgm_ops").HasFilter("removed_at IS NULL").HasDatabaseName("ix_files_current_name_trgm");
        });

        modelBuilder.Entity<ClassificationFinding>(e =>
        {
            e.ToTable("classification_findings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            // Findings describe a file's current contents and are rebuilt on every classification.
            e.HasOne<InventoryFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<ScanJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.PolicyVersion).HasMaxLength(64);
            e.Property(x => x.RuleId).HasMaxLength(32);
            e.Property(x => x.Category).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Confidence).HasConversion<string>().HasMaxLength(8);
            e.HasIndex(x => x.FileId);
            e.HasIndex(x => new { x.Category, x.Confidence });
        });

        modelBuilder.Entity<ClassificationOverride>(e =>
        {
            e.ToTable("classification_overrides");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasOne<InventoryFile>().WithMany().HasForeignKey(x => x.FileId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sensitivity).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.PolicySensitivity).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.ReviewerSid).HasMaxLength(184);
            e.Property(x => x.ReviewerName).HasMaxLength(256);
            e.HasIndex(x => new { x.FileId, x.Id });
        });

        modelBuilder.Entity<DirectorySnapshot>(e =>
        {
            e.ToTable("directory_snapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Domain).HasMaxLength(253);
            e.Property(x => x.DomainSid).HasMaxLength(184);
            e.Property(x => x.NamingContext).HasMaxLength(512);
            e.Property(x => x.ServerName).HasMaxLength(253);
            e.Property(x => x.RequestedBySid).HasMaxLength(184);
            e.Property(x => x.LeaseOwner).HasMaxLength(200);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.State, x.RequestedAt });
        });

        // Principals and memberships belong to their snapshot and go when it is pruned.
        modelBuilder.Entity<DirectoryPrincipal>(e =>
        {
            e.ToTable("directory_principals");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasOne<DirectorySnapshot>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Sid).HasMaxLength(184);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.DistinguishedName).HasMaxLength(2048);
            e.Property(x => x.Scope).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.PrimaryGroupSid).HasMaxLength(184);
            e.Property(x => x.UserPrincipalName).HasMaxLength(1024);
            e.Property(x => x.Mail).HasMaxLength(256);
            e.Property(x => x.Department).HasMaxLength(128);
            e.Property(x => x.Title).HasMaxLength(128);
            e.Property(x => x.ManagerSid).HasMaxLength(184);
            e.HasIndex(x => new { x.SnapshotId, x.Sid }).IsUnique();
            e.HasIndex(x => new { x.SnapshotId, x.Kind });
        });

        modelBuilder.Entity<DirectoryMembership>(e =>
        {
            e.ToTable("directory_memberships");
            e.HasKey(x => new { x.SnapshotId, x.GroupSid, x.MemberSid });
            e.HasOne<DirectorySnapshot>().WithMany().HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.GroupSid).HasMaxLength(184);
            e.Property(x => x.MemberSid).HasMaxLength(184);
            e.HasIndex(x => new { x.SnapshotId, x.MemberSid });
        });

        modelBuilder.Entity<StoredSecurityDescriptor>(e =>
        {
            e.ToTable("security_descriptors");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Hash).HasMaxLength(32);
            e.HasIndex(x => x.Hash).IsUnique();
            e.Property(x => x.OwnerSid).HasMaxLength(184);
        });

        modelBuilder.Entity<StoredAce>(e =>
        {
            e.ToTable("security_aces");
            e.HasKey(x => new { x.DescriptorId, x.Ordinal });
            e.HasOne<StoredSecurityDescriptor>().WithMany().HasForeignKey(x => x.DescriptorId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Sid).HasMaxLength(184);
            e.HasIndex(x => x.Sid);
        });

        modelBuilder.Entity<FolderSecurityChange>(e =>
        {
            e.ToTable("folder_security_changes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasOne<Folder>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ScanJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StoredSecurityDescriptor>().WithMany().HasForeignKey(x => x.FromDescriptorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StoredSecurityDescriptor>().WithMany().HasForeignKey(x => x.ToDescriptorId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.FolderId, x.At });
            e.HasIndex(x => x.JobId);
        });

        modelBuilder.Entity<ServerLocalGroup>(e =>
        {
            e.ToTable("server_local_groups");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasOne<ManagedServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sid).HasMaxLength(184);
            e.Property(x => x.Name).HasMaxLength(256);
            e.HasIndex(x => new { x.ServerId, x.Sid }).IsUnique();
        });

        modelBuilder.Entity<ServerLocalGroupMember>(e =>
        {
            e.ToTable("server_local_group_members");
            e.HasKey(x => new { x.LocalGroupId, x.MemberSid });
            e.HasOne<ServerLocalGroup>().WithMany().HasForeignKey(x => x.LocalGroupId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.MemberSid).HasMaxLength(184);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
        });

        ConfigureGovernance(modelBuilder);
        ConfigureActivity(modelBuilder);

        modelBuilder.Entity<ScanGap>(e =>
        {
            e.ToTable("scan_gaps");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasOne<ScanJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Share>().WithMany().HasForeignKey(x => x.ShareId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Message).HasMaxLength(500);
            e.HasIndex(x => x.JobId);
            e.HasIndex(x => new { x.ShareId, x.JobId });
        });

        modelBuilder.Entity<ShareScanHistory>(e =>
        {
            e.ToTable("share_scan_history");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasOne<Share>().WithMany().HasForeignKey(x => x.ShareId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ScanJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.ShareId, x.JobId }).IsUnique();
            e.HasIndex(x => x.CompletedAt);
        });

        modelBuilder.Entity<ScanFrontierItem>(e =>
        {
            e.ToTable("scan_frontier");
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq).UseIdentityAlwaysColumn();
            e.HasOne<ScanJob>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Folder>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.JobId, x.FolderId }).IsUnique();
            e.HasIndex(x => new { x.JobId, x.Seq });
        });
    }

    private static void ConfigureGovernance(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OwnerAssignment>(e =>
        {
            e.ToTable("owner_assignments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasOne<Folder>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Share>().WithMany().HasForeignKey(x => x.ShareId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.OwnerSid).HasMaxLength(184);
            e.Property(x => x.OwnerName).HasMaxLength(256);
            e.Property(x => x.OwnerEmail).HasMaxLength(256);
            e.Property(x => x.BackupSid).HasMaxLength(184);
            e.Property(x => x.BackupName).HasMaxLength(256);
            e.Property(x => x.BackupEmail).HasMaxLength(256);
            e.Property(x => x.Department).HasMaxLength(128);
            e.Property(x => x.Purpose).HasMaxLength(500);
            e.Property(x => x.ReviewFrequency).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.RetentionCategory).HasMaxLength(64);
            e.Property(x => x.Notes).HasMaxLength(1000);
            e.Property(x => x.AssignedBySid).HasMaxLength(184);
            e.Property(x => x.AssignedByName).HasMaxLength(256);
            e.Property(x => x.EndedBySid).HasMaxLength(184);
            e.Property(x => x.EndReason).HasMaxLength(500);
            // One current owner per folder; ended rows are history.
            e.HasIndex(x => x.FolderId).IsUnique().HasFilter("ended_at IS NULL");
            e.HasIndex(x => x.OwnerSid);
            e.HasIndex(x => x.BackupSid);
        });

        modelBuilder.Entity<ReviewCampaign>(e =>
        {
            e.ToTable("review_campaigns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Scope).HasColumnType("jsonb");
            e.Property(x => x.Skipped).HasColumnType("jsonb");
            e.Property(x => x.Domain).HasMaxLength(253);
            e.Property(x => x.CreatedBySid).HasMaxLength(184);
            e.Property(x => x.CreatedByName).HasMaxLength(256);
            e.Property(x => x.ClosedBySid).HasMaxLength(184);
            e.Property(x => x.ClosedByName).HasMaxLength(256);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<AccessReview>(e =>
        {
            e.ToTable("access_reviews");
            e.HasKey(x => x.Id);
            e.HasOne<ReviewCampaign>().WithMany().HasForeignKey(x => x.CampaignId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Folder>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Resource).HasMaxLength(2048);
            e.Property(x => x.OwnerSid).HasMaxLength(184);
            e.Property(x => x.OwnerName).HasMaxLength(256);
            e.Property(x => x.OwnerEmail).HasMaxLength(256);
            e.Property(x => x.BackupSid).HasMaxLength(184);
            e.Property(x => x.BackupName).HasMaxLength(256);
            e.Property(x => x.BackupEmail).HasMaxLength(256);
            e.Property(x => x.DelegateSid).HasMaxLength(184);
            e.Property(x => x.DelegateName).HasMaxLength(256);
            e.Property(x => x.DelegateEmail).HasMaxLength(256);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.ShareState).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.FolderState).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.EvidenceHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.SubmittedBySid).HasMaxLength(184);
            e.Property(x => x.SubmittedByName).HasMaxLength(256);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.CampaignId, x.FolderId }).IsUnique();
            e.HasIndex(x => x.OwnerSid);
            e.HasIndex(x => x.BackupSid);
            e.HasIndex(x => x.DelegateSid);
        });

        modelBuilder.Entity<ReviewItem>(e =>
        {
            e.ToTable("review_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasOne<AccessReview>().WithMany().HasForeignKey(x => x.ReviewId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.TrusteeSid).HasMaxLength(184);
            e.Property(x => x.TrusteeName).HasMaxLength(256);
            e.Property(x => x.TrusteeKind).HasMaxLength(32);
            e.Property(x => x.Effect).HasMaxLength(16);
            e.Property(x => x.Rights).HasMaxLength(32);
            e.Property(x => x.AppliesTo).HasMaxLength(500);
            e.HasIndex(x => new { x.ReviewId, x.Ordinal }).IsUnique();
        });

        modelBuilder.Entity<ReviewItemUser>(e =>
        {
            e.ToTable("review_item_users");
            e.HasKey(x => new { x.ItemId, x.Ordinal });
            e.HasOne<ReviewItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Sid).HasMaxLength(184);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Department).HasMaxLength(128);
            e.Property(x => x.Title).HasMaxLength(128);
            e.Property(x => x.Rights).HasMaxLength(32);
            e.Property(x => x.UpTo).HasMaxLength(32);
            e.Property(x => x.Certainty).HasMaxLength(16);
            e.Property(x => x.AlsoVia).HasMaxLength(800);
        });

        modelBuilder.Entity<ReviewDecision>(e =>
        {
            e.ToTable("review_decisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasOne<AccessReview>().WithMany().HasForeignKey(x => x.ReviewId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ReviewItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Decision).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Justification).HasMaxLength(1000);
            e.Property(x => x.DecidedBySid).HasMaxLength(184);
            e.Property(x => x.DecidedByName).HasMaxLength(256);
            e.Property(x => x.DecidedAs).HasMaxLength(16);
            e.HasIndex(x => new { x.ItemId, x.Id });
            e.HasIndex(x => x.ReviewId);
        });

        modelBuilder.Entity<ReviewEvent>(e =>
        {
            e.ToTable("review_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.HasOne<AccessReview>().WithMany().HasForeignKey(x => x.ReviewId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Kind).HasMaxLength(32);
            e.Property(x => x.ActorSid).HasMaxLength(184);
            e.Property(x => x.ActorName).HasMaxLength(256);
            e.Property(x => x.Detail).HasMaxLength(1000);
            e.HasIndex(x => new { x.ReviewId, x.At });
        });

        modelBuilder.Entity<ServiceHeartbeat>(e =>
        {
            e.ToTable("service_heartbeats");
            e.HasKey(x => x.Service);
            e.Property(x => x.Service).HasMaxLength(32);
            e.Property(x => x.Machine).HasMaxLength(256);
            e.Property(x => x.Version).HasMaxLength(64);
        });


        modelBuilder.Entity<ReviewExemption>(e =>
        {
            e.ToTable("review_exemptions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.Sid).HasMaxLength(184);
            e.Property(x => x.Name).HasMaxLength(256);
            e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.AddedBySid).HasMaxLength(184);
            e.Property(x => x.AddedByName).HasMaxLength(256);
            e.Property(x => x.EndedBySid).HasMaxLength(184);
            e.Property(x => x.EndedByName).HasMaxLength(256);
            // One current entry per account; ended entries stay as history.
            e.HasIndex(x => x.Sid).IsUnique().HasFilter("ended_at IS NULL");
        });

        modelBuilder.Entity<SystemSetting>(e =>
        {
            e.ToTable("system_settings");
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Value).HasMaxLength(16_000);
            e.Property(x => x.UpdatedBySid).HasMaxLength(184);
            e.Property(x => x.UpdatedByName).HasMaxLength(256);
        });

        modelBuilder.Entity<ScanExclusion>(e =>
        {
            e.ToTable("scan_exclusions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Effect).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Share>().WithMany().HasForeignKey(x => x.ShareId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Path).HasMaxLength(1024);
            e.Property(x => x.Extension).HasMaxLength(32);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedBySid).HasMaxLength(184);
            e.Property(x => x.CreatedByName).HasMaxLength(256);
            e.Property(x => x.UpdatedBySid).HasMaxLength(184);
            e.Property(x => x.UpdatedByName).HasMaxLength(256);
            e.HasIndex(x => x.ShareId);
            e.ToTable(t => t.HasCheckConstraint("ck_scan_exclusions_target",
                "(kind = 'Folder' AND share_id IS NOT NULL AND path IS NOT NULL AND extension IS NULL) OR (kind = 'FileType' AND extension IS NOT NULL AND path IS NULL AND effect = 'ListOnly')"));
        });

        modelBuilder.Entity<ComplianceStandardSetting>(e =>
        {
            e.ToTable("compliance_standards");
            e.HasKey(x => x.StandardId);
            e.Property(x => x.StandardId).HasMaxLength(32);
            e.Property(x => x.MinConfidence).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.UpdatedBySid).HasMaxLength(184);
            e.Property(x => x.UpdatedByName).HasMaxLength(256);
            e.ToTable(t => t.HasCheckConstraint("ck_compliance_standards_categories", "cardinality(categories) BETWEEN 1 AND 16"));
        });

        modelBuilder.Entity<CustomDetectionRule>(e =>
        {
            e.ToTable("custom_detection_rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(80);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Pattern).HasMaxLength(500);
            e.Property(x => x.Effect).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reason).HasMaxLength(500);
            e.Property(x => x.CreatedByName).HasMaxLength(256);
            e.Property(x => x.UpdatedBySid).HasMaxLength(184);
            e.Property(x => x.UpdatedByName).HasMaxLength(256);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("ck_custom_detection_rules_kind",
                    "(kind = 'Pattern' AND pattern IS NOT NULL AND cardinality(keywords) = 0) OR (kind = 'Keywords' AND pattern IS NULL AND cardinality(keywords) BETWEEN 1 AND 100)");
                t.HasCheckConstraint("ck_custom_detection_rules_min_matches", "min_matches BETWEEN 1 AND 1000");
            });
        });

        modelBuilder.Entity<RiskDecision>(e =>
        {
            e.ToTable("risk_decisions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.RiskId).HasMaxLength(400);
            e.Property(x => x.Rule).HasMaxLength(64);
            e.Property(x => x.Title).HasMaxLength(512);
            e.Property(x => x.Where).HasMaxLength(2048);
            e.Property(x => x.Severity).HasMaxLength(16);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.DecidedBySid).HasMaxLength(184);
            e.Property(x => x.DecidedByName).HasMaxLength(256);
            e.Property(x => x.EndedBySid).HasMaxLength(184);
            e.Property(x => x.EndedByName).HasMaxLength(256);
            // One current decision per risk; ended ones stay as history.
            e.HasIndex(x => x.RiskId).IsUnique().HasFilter("ended_at IS NULL");
        });






        modelBuilder.Entity<PostureSnapshot>(e =>
        {
            e.ToTable("posture_snapshots");
            e.HasKey(x => x.Day);
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("notification_outbox");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.IdempotencyKey).HasMaxLength(320);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.RecipientSid).HasMaxLength(184);
            e.Property(x => x.RecipientName).HasMaxLength(256);
            e.Property(x => x.RecipientEmail).HasMaxLength(256);
            e.Property(x => x.Subject).HasMaxLength(500);
            e.Property(x => x.Body).HasMaxLength(8000);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.HasIndex(x => new { x.State, x.NextAttemptAt });
            e.HasIndex(x => x.ReviewId);
        });
    }

    private static void ConfigureActivity(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FileAuditEvent>(e =>
        {
            e.ToTable("file_audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            // Restrict: events are evidence; a server with events cannot be deleted.
            e.HasOne<ManagedServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Share>().WithMany().HasForeignKey(x => x.ShareId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Channel).HasMaxLength(64);
            e.Property(x => x.Provider).HasMaxLength(256);
            e.Property(x => x.PrincipalSid).HasMaxLength(184);
            e.Property(x => x.PrincipalName).HasMaxLength(256);
            e.Property(x => x.LogonId).HasMaxLength(20);
            e.Property(x => x.ProcessName).HasMaxLength(520);
            e.Property(x => x.ClientAddress).HasMaxLength(64);
            e.Property(x => x.EventShareName).HasMaxLength(80);
            e.Property(x => x.EventShareLocalPath).HasMaxLength(1024);
            e.Property(x => x.ObjectKind).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.PathSource).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Operation).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Certainty).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.OldSecurity).HasMaxLength(8192);
            e.Property(x => x.NewSecurity).HasMaxLength(8192);
            e.Property(x => x.ParseError).HasMaxLength(500);
            // Idempotent ingest: the same event read twice is the same row (PRD module H, data model).
            e.HasIndex(x => new { x.ServerId, x.Channel, x.RecordId, x.LogGeneration }).IsUnique();
            e.HasIndex(x => new { x.EventTime, x.Id });
            e.HasIndex(x => new { x.ShareId, x.EventTime });
            e.HasIndex(x => new { x.PrincipalSid, x.EventTime });
            // Finding the event a 4660 delete belongs to (D100).
            e.HasIndex(x => new { x.ServerId, x.HandleId, x.RecordId }).HasFilter("handle_id IS NOT NULL");
            // Events waiting for their share's folder to be known (D99).
            e.HasIndex(x => x.ServerId).HasFilter("share_id IS NULL AND object_path IS NOT NULL").HasDatabaseName("ix_file_audit_events_unmatched");
            // The latest 5140 or 5145 for each share, to learn its folder (D99).
            e.HasIndex(x => new { x.ServerId, x.EventShareName, x.RecordId }).HasFilter("event_share_local_path IS NOT NULL");
        });

        modelBuilder.Entity<ActivitySource>(e =>
        {
            e.ToTable("activity_sources");
            e.HasKey(x => new { x.ServerId, x.Channel });
            e.HasOne<ManagedServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Channel).HasMaxLength(64);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.Property(x => x.LeaseOwner).HasMaxLength(200);
            e.Property(x => x.ChangedBySid).HasMaxLength(184);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.Enabled, x.NextReadAt });
        });
        modelBuilder.Entity<ActivityGap>(e =>
        {
            e.ToTable("activity_gaps");
            e.HasOne<ManagedServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.Channel).HasMaxLength(64);
            e.Property(x => x.Reason).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(x => new { x.ServerId, x.DetectedAt });
        });
        modelBuilder.Entity<ActivityAlert>(e =>
        {
            e.ToTable("activity_alerts");
            e.HasOne<ManagedServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ActivityGap>().WithMany().HasForeignKey(x => x.GapId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Rule).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.State).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.PrincipalSid).HasMaxLength(MaxPersonLength);
            e.Property(x => x.PrincipalName).HasMaxLength(MaxPersonLength);
            e.Property(x => x.AcknowledgedBySid).HasMaxLength(184);
            e.Property(x => x.AcknowledgedByName).HasMaxLength(512);
            e.Property(x => x.Note).HasMaxLength(2000);
            e.Property(x => x.Detail).HasMaxLength(2000);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasIndex(x => new { x.State, x.LastEventAt });
            e.HasIndex(x => new { x.Rule, x.ServerId, x.PrincipalSid, x.LastEventAt });
        });
        modelBuilder.Entity<ActivityBaseline>(e =>
        {
            e.ToTable("activity_baselines");
            e.HasKey(x => new { x.ServerId, x.PersonKey });
            e.HasOne<ManagedServer>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.PersonKey).HasMaxLength(MaxPersonLength);
            e.Property(x => x.PersonName).HasMaxLength(MaxPersonLength);
        });
    }
}
