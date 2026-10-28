// Typed client for the Data Scope API (/api/v1). Field names match the .NET DTOs.
// Authentication is Windows sign-in handled by the browser, so requests carry no tokens.

import type { Confidence, FindingCategory } from '../domain/types';

export interface Page<T> {
  items: T[];
  limit: number;
  offset: number;
  total: number;
}

export interface Me {
  name: string;
  sid: string;
  roles: string[];
  version: string;
  /** Reviews waiting for this person as owner, delegate or escalated backup owner. */
  openReviews: number;
  /** Shares and folders this person owns or backs up. */
  owns: number;
}

export type HealthStatus = 'Ok' | 'Warning' | 'Problem' | 'Unknown';

export interface HealthItem {
  name: string;
  status: HealthStatus;
  detail: string;
}

export interface HealthCheck {
  id: string;
  area: string;
  title: string;
  status: HealthStatus;
  detail: string;
  fix?: string | null;
  /** The screen that shows more, as a route name. */
  route?: string | null;
  items?: HealthItem[] | null;
}

/** System health: the daily health view. */
export interface HealthReport {
  overall: HealthStatus;
  checkedAt: string;
  checks: HealthCheck[];
}

export type OutboxState = 'Pending' | 'Sent' | 'Failed' | 'Skipped';

export type EnrollmentState = 'Pending' | 'Enrolled' | 'Error' | 'Retired';
export interface Server {
  id: string;
  hostname: string;
  state: EnrollmentState;
  lastSeenAt: string | null;
  lastError: string | null;
  createdAt: string;
  schedule: ScanSchedule;
}

export type ServerKind = 'FileServer';

export type ScanFrequency = 'Off' | 'Hourly' | 'Daily' | 'Weekly';
export type Weekday = 'Sunday' | 'Monday' | 'Tuesday' | 'Wednesday' | 'Thursday' | 'Friday' | 'Saturday';
/** Monthly: on the first of the chosen weekday each month (D130). */
export type FullScanFrequency = 'Off' | 'Weekly' | 'Monthly';
/** Quick reads only new and changed files; Full reads every file again with more room per file (D130). */
export type ScanKind = 'Quick' | 'Full';
/** When a server is scanned on its own. Times are in the Data Scope server's time zone. */
export interface ScanSchedule {
  frequency: ScanFrequency;
  /** The hour of a daily or weekly scan; for scans every few hours, the first of the day. */
  startHour: number;
  /** The minute past the hour each scan starts. */
  startMinute: number;
  /** For scans every few hours: 1, 2, 3, 4, 6, 8 or 12. Null otherwise. */
  everyHours: number | null;
  day: Weekday | null;
  /**
   * A scheduled scan still running at this hour pauses and continues at the next scheduled time. Null: runs to the end.
   * For scans every few hours it also ends the day's scans until the start hour.
   */
  stopHour: number | null;
  nextScanAt: string | null;
  /** A scheduled start that never happened because the worker service wasn't running in time. */
  missedAt: string | null;
  timeZone: string;
  /** Full scans start at the scan start time and take the place of that time's quick scan. */
  fullScan: FullScanFrequency;
  fullScanDay: Weekday | null;
  nextFullScanAt: string | null;
}
export interface ScanScheduleInput {
  frequency: ScanFrequency;
  startHour: number;
  startMinute?: number;
  everyHours?: number | null;
  day: Weekday | null;
  stopHour: number | null;
  /** Left out, the server's full scans stay as they are. */
  fullScan?: FullScanFrequency;
  fullScanDay?: Weekday | null;
}

/** What removing a server changed. Nothing is deleted; items are marked removed and history is kept. */
export interface ServerRemoval {
  scansCancelled: number;
  shares: number;
  folders: number;
  files: number;
  activityStopped: boolean;
}

export type JobState = 'Queued' | 'Running' | 'Paused' | 'Completed' | 'Failed' | 'Cancelled';
export interface ScanJob {
  id: string;
  serverId: string;
  state: JobState;
  requestedState: JobState | null;
  policyVersion: string;
  itemsDone: number;
  itemsTotal: number;
  attempts: number;
  lastError: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  /** walk: items are folders. classify: items are files being checked for sensitive data. */
  phase: 'walk' | 'classify';
  /** Started by the server's schedule rather than by a person. */
  scheduled: boolean;
  /** The schedule pauses it at the stop hour and continues it at the next scheduled time. */
  keepToWindow: boolean;
  kind: ScanKind;
}

export type ShareState = 'NotScanned' | 'Scanned' | 'Partial' | 'Unavailable' | 'Removed';
export interface Share {
  id: string;
  serverId: string;
  hostname: string;
  name: string;
  uncPath: string;
  remark: string | null;
  kind: 'Disk' | 'Special' | 'Other';
  included: boolean;
  state: ShareState;
  lastScannedAt: string | null;
  lastScanJobId: string | null;
  folderCount: number;
  fileCount: number;
  totalBytes: number;
  gapCount: number;
  /** The share's owner (the active owner of its top folder), if one is assigned. */
  ownerName?: string | null;
  /** Why an administrator left the share out of scans (D124). */
  exclusionReason?: string | null;
  /** Always a file share. Older APIs leave it out. */
  source?: ItemSource;
  excludedByName?: string | null;
  excludedAt?: string | null;
}

export interface Folder {
  id: string;
  name: string;
  path: string;
  depth: number;
  modifiedAt: string | null;
  listed: boolean;
  removed: boolean;
}

export interface FileEntry {
  id: string;
  name: string;
  extension: string;
  size: number;
  createdAt: string | null;
  modifiedAt: string | null;
  removed: boolean;
}

export type GapKind =
  | 'ShareUnavailable'
  | 'AccessDenied'
  | 'NotFound'
  | 'PathTooLong'
  | 'ReparsePointSkipped'
  | 'DepthLimit'
  | 'FolderEntryLimit'
  | 'JobEntryLimit'
  | 'UnrepresentableName'
  | 'IoError'
  /** An administrator left this folder out of scans (D124). */
  | 'Excluded';
export interface Gap {
  id: number;
  jobId: string;
  shareId: string | null;
  shareName: string | null;
  path: string;
  kind: GapKind;
  message: string;
  at: string;
}

export interface Coverage {
  servers: number;
  sharesTotal: number;
  sharesIncluded: number;
  sharesScanned: number;
  sharesPartial: number;
  sharesUnavailable: number;
  sharesNotScanned: number;
  folders: number;
  files: number;
  bytes: number;
  gapsByKind: Partial<Record<GapKind, number>>;
}

export type Sensitivity = 'Unknown' | 'Public' | 'Internal' | 'Confidential' | 'Restricted';
export const SENSITIVITIES: Sensitivity[] = ['Restricted', 'Confidential', 'Internal', 'Public', 'Unknown'];

export type ContentState =
  | 'NotRead'
  | 'Scanned'
  | 'Partial'
  | 'Unsupported'
  | 'TooLarge'
  | 'Offline'
  | 'AccessDenied'
  | 'NotFound'
  | 'Encrypted'
  | 'Unreadable'
  | 'TimedOut'
  | 'IoError'
  /** An administrator excluded the contents from reading (D124). Never the same as clean. */
  | 'Excluded';

export interface CategoryCount {
  category: FindingCategory;
  confidence: Confidence;
  matches: number;
}

/** Where an item lives: a file on a file share. */
export type ItemSource = 'FileShare';

export interface ClassifiedFile {
  id: string;
  shareId: string;
  shareName: string;
  hostname: string;
  path: string;
  name: string;
  extension: string;
  size: number;
  modifiedAt: string | null;
  /** The label in force: the reviewer's override when there is one, otherwise the policy's. */
  sensitivity: Sensitivity;
  policySensitivity: Sensitivity;
  overrideSensitivity: Sensitivity | null;
  contentState: ContentState;
  contentNote: string | null;
  classifiedAt: string | null;
  classifiedPolicy: string | null;
  categories: CategoryCount[];
  removed: boolean;
  /** Ids of the turned-on compliance standards this file falls under (D126). */
  standards: string[];
  /** Always a file share. Older APIs leave it out. */
  source?: ItemSource;
  /** The folder's path in the share; empty at the top. */
  folder?: string;
}

export interface FindingDetail {
  ruleId: string;
  ruleName: string;
  category: FindingCategory;
  confidence: Confidence;
  matches: number;
  /** Redacted snippets, or null when the signed-in role may not see evidence. */
  evidence: string[] | null;
  policyVersion: string;
  discoveredAt: string;
}

export interface OverrideEntry {
  sensitivity: Sensitivity | null;
  policySensitivity: Sensitivity;
  reason: string;
  reviewerName: string;
  at: string;
}

export interface FileClassification {
  file: ClassifiedFile;
  findings: FindingDetail[];
  evidenceHidden: boolean;
  overrides: OverrideEntry[];
  changedSinceReview: boolean;
}

export interface ClassificationSummary {
  policyVersion: string;
  files: number;
  bySensitivity: Record<Sensitivity, number>;
  byContentState: Record<ContentState, number>;
  byCategory: { category: FindingCategory; files: number; high: number; medium: number; low: number }[];
  overrides: number;
}

export interface FileFilters {
  /** Only files on file shares. */
  source?: SourceFilter;
  serverId?: string;
  shareId?: string;
  sensitivity?: Sensitivity;
  minSensitivity?: Sensitivity;
  category?: FindingCategory;
  minConfidence?: 'Low' | 'Medium' | 'High';
  contentState?: ContentState;
  /** Only files whose contents were not read: coverage gaps. */
  notRead?: boolean;
  /** Only files under this turned-on compliance standard (D126). */
  standard?: string;
  /** Only files one rule found something in, such as DS-SSN-01 or a custom rule's DS-CUSTOM-3 (D133). */
  rule?: string;
}

export type SourceFilter = 'files';

// ---------- directory and access (M3) ----------

export type SnapshotState = 'Queued' | 'Running' | 'Completed' | 'Failed';
export interface DirectorySnapshot {
  id: string;
  state: SnapshotState;
  domain: string | null;
  serverName: string | null;
  requestedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  attempts: number;
  users: number;
  disabledUsers: number;
  groups: number;
  computers: number;
  foreignPrincipals: number;
  memberships: number;
  unresolvedMembers: number;
  cycles: number;
  truncated: boolean;
  warnings: string[];
  lastError: string | null;
}
export interface DirectoryStatus {
  latest: DirectorySnapshot | null;
  pending: DirectorySnapshot | null;
  lastFailure: DirectorySnapshot | null;
}

export type PrincipalKind = 'User' | 'Group' | 'Computer' | 'ForeignSecurityPrincipal';
export interface Principal {
  sid: string;
  kind: PrincipalKind;
  name: string;
  displayName: string | null;
  enabled: boolean;
  isSecurityGroup: boolean;
  scope: string | null;
  userPrincipalName: string | null;
  department: string | null;
  title: string | null;
}

export type TrusteeKind = PrincipalKind | 'LocalGroup' | 'WellKnown' | 'Deleted' | 'Unknown';
export interface Trustee {
  sid: string;
  name: string;
  kind: TrusteeKind;
  /** Reaches (almost) every account: Everyone, Authenticated Users, Domain Users and the like. */
  broad: boolean;
  /** Expected administrative access: SYSTEM, Administrators, Domain Admins. */
  administrative: boolean;
  enabled: boolean | null;
}

export interface PrincipalDetail {
  principal: Principal;
  distinguishedName: string;
  mail: string | null;
  lastLogon: string | null;
  whenChanged: string | null;
  sidHistory: string[];
  membersIncomplete: boolean;
  directGroups: Trustee[];
  memberOf: Trustee[];
  maybeMemberOf: Trustee[];
  members: Trustee[];
  directMemberCount: number;
  usersCertain: number | null;
  usersPossible: number | null;
  inCycle: boolean;
  /** Who the user reports to (AD manager attribute). */
  manager: Trustee | null;
  snapshotId: string;
  snapshotAt: string | null;
}

export type RightsLabel = 'No access' | 'Read' | 'Write' | 'Modify' | 'Full control';
export type Certainty = 'Likely' | 'Partial' | 'Unknown';
export type PermissionState = 'NotRead' | 'Read' | 'AccessDenied' | 'Unreadable';
export type AccessTarget = 'Files' | 'Folder';

export interface AceEntry {
  index: number;
  effect: 'Allow' | 'Deny' | 'Conditional allow' | 'Conditional deny' | 'Other';
  trustee: Trustee | null;
  /** For share entries, the Advanced Sharing names (Full Control, Change, Read). */
  rights: string;
  mask: string;
  appliesTo: string;
  inherited: boolean;
  appliesToFolder: boolean;
  appliesToFiles: boolean;
  membersCertain: number | null;
  membersPossible: number | null;
}
export interface PermissionSet {
  state: PermissionState;
  readAt: string | null;
  owner: Trustee | null;
  protected: boolean | null;
  canonical: boolean | null;
  noList: boolean | null;
  entries: AceEntry[];
}

export interface AccessNote {
  code: string;
  text: string;
}
/** What Data Scope can show (rights) and what is possible (upTo); equal when certainty is Likely. */
export interface AccessRange {
  rights: RightsLabel;
  upTo: RightsLabel;
  certainty: Certainty;
  notes: AccessNote[];
}
export interface UserAccess {
  sid: string;
  name: string;
  enabled: boolean;
  rights: RightsLabel;
  upTo: RightsLabel;
  certainty: Certainty;
  via: string | null;
}
export interface AccessReason {
  source: 'Share' | 'NTFS';
  index: number;
  effect: AceEntry['effect'];
  trustee: Trustee | null;
  match: 'Yes' | 'Maybe';
  chain: string[];
  rights: RightsLabel;
  inherited: boolean;
  appliesTo: string;
}
export interface AccessExplanation {
  user: UserAccess;
  target: AccessTarget;
  shareRights: RightsLabel | null;
  folderRights: RightsLabel | null;
  reasons: AccessReason[];
  notes: AccessNote[];
}
export interface DirectoryRef {
  snapshotId: string | null;
  syncedAt: string | null;
  domain: string | null;
  localGroups: string;
}
export interface ShareAccess {
  shareId: string;
  serverId: string;
  hostname: string;
  name: string;
  uncPath: string;
  share: PermissionSet;
  rootFolderId: string | null;
  openAccess: AccessRange | null;
  folderPermissionStates: Record<PermissionState, number>;
  boundaries: number;
  directory: DirectoryRef;
}
export interface Boundary {
  folderId: string;
  path: string;
  depth: number;
  state: PermissionState;
  protected: boolean | null;
  explicit: boolean | null;
  owner: Trustee | null;
  openAccess: AccessRange;
}
export interface FolderAccess {
  folderId: string;
  shareId: string;
  shareName: string;
  serverId: string;
  hostname: string;
  uncPath: string;
  path: string;
  removed: boolean;
  share: PermissionSet;
  folder: PermissionSet;
  openFolder: AccessRange;
  openFiles: AccessRange;
  usersCertain: number;
  usersPossible: number;
  directory: DirectoryRef;
}
export interface FileAccess {
  fileId: string;
  folderId: string;
  uncPath: string;
  openAccess: AccessRange;
  usersCertain: number;
  usersPossible: number;
  users: UserAccess[];
  directory: DirectoryRef;
}
export type ExposureStatus = 'Open' | 'PossiblyOpen' | 'Unknown';
export interface ExposureItem {
  folderId: string;
  shareId: string;
  hostname: string;
  shareName: string;
  uncPath: string;
  status: ExposureStatus;
  openAccess: AccessRange;
  restrictedFiles: number;
  confidentialFiles: number;
}
export interface Exposure {
  folders: Page<ExposureItem>;
  foldersChecked: number;
  open: number;
  possiblyOpen: number;
  unknown: number;
  sensitiveFilesOpen: number;
  truncated: boolean;
  directory: DirectoryRef;
}
export interface ReachItem {
  folderId: string;
  shareId: string;
  hostname: string;
  shareName: string;
  uncPath: string;
  rights: RightsLabel;
  upTo: RightsLabel;
  certainty: Certainty;
}
export interface Reach {
  user: UserAccess;
  target: AccessTarget;
  places: Page<ReachItem>;
  foldersWithAccess: number;
  foldersCertain: number;
  truncated: boolean;
  directory: DirectoryRef;
}

// ---------- M4: owners and access reviews ----------

export type ReviewFrequency = 'Quarterly' | 'Semiannual' | 'Annual';
export type OwnerStatus = 'Active' | 'Disabled' | 'Missing' | 'Unknown';
export interface OwnerAssignment {
  id: number;
  folderId: string;
  shareId: string;
  serverId: string;
  hostname: string;
  shareName: string;
  path: string;
  resource: string;
  ownerSid: string;
  ownerName: string;
  ownerEmail: string | null;
  ownerStatus: OwnerStatus;
  backupSid: string | null;
  backupName: string | null;
  backupEmail: string | null;
  backupStatus: OwnerStatus | null;
  department: string | null;
  purpose: string | null;
  reviewFrequency: ReviewFrequency;
  retentionCategory: string | null;
  notes: string | null;
  assignedByName: string;
  assignedAt: string;
  endedAt: string | null;
  endReason: string | null;
  lastReviewedOn: string | null;
  nextReviewDue: string | null;
}
export interface UnownedShare {
  folderId: string;
  shareId: string;
  serverId: string;
  hostname: string;
  shareName: string;
  resource: string;
  fileCount: number;
  totalBytes: number;
  mostSensitive: 'Restricted' | 'Confidential' | null;
}
export interface OwnerSummary {
  owned: number;
  unowned: number;
  orphanedOwners: number;
  missingBackup: number;
  reviewsDue: number;
}
export interface OwnerSuggestion {
  sid: string;
  name: string;
  reason: string;
  /** Manager: from reporting lines in AD. WindowsOwner: the folder's owner in Windows. DirectRights: granted Modify or more directly. */
  basis: 'Manager' | 'WindowsOwner' | 'DirectRights';
  title: string | null;
  department: string | null;
}
export interface FolderOwner {
  current: OwnerAssignment | null;
  history: OwnerAssignment[];
  suggestions: OwnerSuggestion[];
}
export interface AssignOwnerRequest {
  ownerSid: string;
  ownerEmail?: string;
  backupSid?: string;
  backupEmail?: string;
  department?: string;
  purpose?: string;
  reviewFrequency: ReviewFrequency;
  retentionCategory?: string;
  notes?: string;
}

export type CampaignState = 'Open' | 'Closed';
export type ReviewState = 'Pending' | 'InProgress' | 'Submitted';
export type DecisionKind = 'Keep' | 'Remove' | 'Investigate' | 'Exception';
export type ReviewRole = 'Owner' | 'Delegate' | 'Backup';

export interface SkippedResource {
  folderId: string;
  resource: string;
  reason: string;
}
export interface Campaign {
  id: string;
  name: string;
  description: string | null;
  dueOn: string;
  state: CampaignState;
  overdue: boolean;
  createdAt: string;
  createdByName: string;
  closedAt: string | null;
  closedByName: string | null;
  directorySyncedAt: string | null;
  domain: string | null;
  reviews: number;
  submitted: number;
  inProgress: number;
  pending: number;
  decisionsRequired: number;
  decided: number;
  skipped: SkippedResource[];
}
export interface ReviewSummary {
  id: string;
  campaignId: string;
  campaignName: string;
  campaignState: CampaignState;
  dueOn: string;
  overdue: boolean;
  resource: string;
  folderId: string;
  shareId: string;
  ownerSid: string;
  ownerName: string;
  backupName: string | null;
  delegateSid: string | null;
  delegateName: string | null;
  state: ReviewState;
  itemCount: number;
  decisionsRequired: number;
  decided: number;
  remove: number;
  investigate: number;
  exception: number;
  submittedAt: string | null;
  submittedByName: string | null;
  escalatedAt: string | null;
  myRole: ReviewRole | null;
}
export interface CampaignDetail {
  campaign: Campaign;
  reviews: ReviewSummary[];
}
export interface ReviewDecision {
  decision: DecisionKind;
  justification: string | null;
  exceptionExpiresOn: string | null;
  decidedByName: string;
  decidedAs: ReviewRole | string;
  decidedAt: string;
}
export type ReviewFlag =
  | 'Broad'
  | 'DirectUser'
  | 'DeletedAccount'
  | 'OtherDomain'
  | 'DisabledAccounts'
  | 'UnresolvedMembers'
  | 'CanChangePermissions'
  | 'Conditional'
  | 'NoMembers'
  | 'NoDecisionNeeded';
export interface ReviewItem {
  id: number;
  ordinal: number;
  trusteeSid: string;
  trusteeName: string;
  trusteeKind: TrusteeKind;
  effect: 'Allow' | 'Deny';
  rights: RightsLabel;
  appliesTo: string;
  inherited: boolean;
  broad: boolean;
  administrative: boolean;
  requiresDecision: boolean;
  usersCertain: number;
  usersPossible: number;
  usersDisabled: number;
  usersWithOtherAccess: number;
  usersTruncated: boolean;
  flags: ReviewFlag[];
  decision: ReviewDecision | null;
  revisions: number;
}
export interface ReviewEvent {
  kind: string;
  actorName: string;
  detail: string;
  at: string;
}
export interface ReviewDetail {
  review: ReviewSummary;
  notes: string[];
  shareState: PermissionState;
  folderState: PermissionState;
  permissionsReadAt: string | null;
  frozenAt: string;
  directorySyncedAt: string | null;
  evidenceHash: string;
  canDecide: boolean;
  canDelegate: boolean;
  items: ReviewItem[];
  events: ReviewEvent[];
  /** Entries left out in the owner portal because their account is on the administrators' no-decision list. */
  hiddenItems: number;
}

/** A user or group the administrators listed as needing no decision in access reviews. */
export interface ReviewExemption {
  id: number;
  sid: string;
  name: string;
  kind: 'User' | 'Group';
  reason: string;
  addedByName: string;
  addedAt: string;
}
export interface ReviewItemUser {
  sid: string;
  name: string;
  enabled: boolean;
  department: string | null;
  title: string | null;
  certain: boolean;
  chain: string[];
  rights: RightsLabel;
  upTo: RightsLabel;
  certainty: Certainty;
  alsoVia: string | null;
}
export interface DecisionInput {
  itemId: number;
  decision: DecisionKind;
  justification?: string;
  exceptionExpiresOn?: string;
}
export interface CampaignRequest {
  name: string;
  description?: string;
  dueOn: string;
  frequency?: ReviewFrequency;
  shareIds?: string[];
}
export interface CampaignVerification {
  campaignId: string;
  reviews: number;
  verified: number;
  mismatched: { reviewId: string; resource: string; expected: string; actual: string }[];
  checkedAt: string;
}
export interface PersonRef {
  sid: string;
  name: string;
  department: string | null;
  title: string | null;
}
export interface OwnedResource {
  folderId: string;
  shareId: string;
  resource: string;
  role: 'Owner' | 'Backup';
  reviewFrequency: ReviewFrequency;
  purpose: string | null;
  assignedAt: string;
}

/** Stands for "any ordinary domain user" in explain and reach calls. */
export const ANY_USER = 'any';

/** An API error with the server's plain-language explanation when it gave one. */

// File activity (M6). Certainty says how much an event proves: only Performed is something Windows recorded happening.
export type ActivityOperation =
  | 'Read' | 'Write' | 'Execute' | 'DeleteOrRename' | 'Deleted' | 'PermissionChange' | 'OwnerChange' | 'AttributeChange'
  | 'MetadataRead' | 'HandleRequest' | 'ShareConnect' | 'ShareAccessCheck' | 'LogCleared' | 'Other' | 'Unreadable';
export type ActivityCertainty = 'Performed' | 'Requested' | 'AccessCheck' | 'ShareSession';
export interface ActivityEvent {
  id: number;
  serverId: string;
  hostname: string;
  eventId: number;
  recordId: number;
  time: string;
  principalSid: string | null;
  principalName: string | null;
  operation: ActivityOperation;
  /** Server-side wording that already accounts for files versus folders. */
  label: string;
  certainty: ActivityCertainty;
  outcome: 'Success' | 'Failure';
  shareId: string | null;
  shareName: string | null;
  relativePath: string | null;
  objectPath: string | null;
  objectKind: 'Unknown' | 'File' | 'Folder';
  pathSource: 'Event' | 'Correlated' | 'None';
  correlatedRecordId: number | null;
  clientAddress: string | null;
}
export interface ActivityEventDetail {
  event: ActivityEvent;
  explanation: string;
  rights: string[];
  accessMask: number | null;
  processName: string | null;
  processId: number | null;
  handleId: number | null;
  logonId: string | null;
  channel: string;
  logGeneration: number;
  provider: string | null;
  ingestedAt: string;
  oldSecurity: string | null;
  newSecurity: string | null;
  parseError: string | null;
  rawXml: string;
}
export interface ActivityPage {
  items: ActivityEvent[];
  next: string | null;
}
export interface ActivityDay {
  date: string;
  reads: number;
  writes: number;
  deletes: number;
  permissionChanges: number;
  denied: number;
}
export interface ActivitySummary {
  from: string;
  to: string;
  offsetMinutes: number;
  days: ActivityDay[];
  unmatched: number;
  lastEventAt: string | null;
  /** Times this week that log records were gone before Data Scope read them. */
  gaps: number;
}
/** A file server's Security log as last checked. `behind` counts log records of any kind not read yet. */
export interface SecurityLogHealth {
  oldestRecordId: number | null;
  newestRecordId: number | null;
  recordCount: number | null;
  oldestEventAt: string | null;
  sizeBytes: number | null;
  maxSizeBytes: number | null;
  full: boolean | null;
  checkedAt: string | null;
  behind: number | null;
  atRisk: boolean;
}
export interface ActivityGap {
  id: number;
  reason: 'Overwritten' | 'LogReset';
  firstRecordId: number;
  lastRecordId: number | null;
  records: number | null;
  detectedAt: string;
  logOldestEventAt: string | null;
}
export interface ShareMapping {
  id: string;
  name: string;
  localPath: string | null;
  localPathSource: 'Event' | 'Admin' | null;
  localPathSetAt: string | null;
}
export interface ActivitySource {
  serverId: string;
  hostname: string;
  enabled: boolean;
  state: 'Off' | 'Waiting' | 'Ok' | 'Error';
  lastReadAt: string | null;
  lastEventAt: string | null;
  nextReadAt: string | null;
  lastError: string | null;
  lastErrorAt: string | null;
  eventsStored: number;
  eventsIgnored: number;
  eventsUnreadable: number;
  unmatched: number;
  shares: ShareMapping[];
  log: SecurityLogHealth | null;
  gaps: ActivityGap[];
}

export interface ActivityFilters {
  serverId?: string;
  shareId?: string;
  principalSid?: string;
  operation?: ActivityOperation;
  outcome?: 'Success' | 'Failure';
  includeChecks?: boolean;
  from?: string;
  to?: string;
  search?: string;
}
export type ActivityAlertRule =
  | 'MassDelete'
  | 'ManyDenied'
  | 'PermissionChange'
  | 'LogCleared'
  | 'EventsMissed'
  // Unusual behaviour (D166): each person against their own past.
  | 'UnusualVolume'
  | 'UnusualHours'
  | 'UnusualPlaces';
/** Something in the file activity worth a look (D105). `operation`/`outcome` filter the events behind it. */
export interface ActivityAlert {
  id: number;
  rule: ActivityAlertRule;
  title: string;
  severity: 'High' | 'Medium';
  state: 'Open' | 'Acknowledged';
  serverId: string;
  hostname: string;
  principalSid: string | null;
  principalName: string | null;
  firstEventAt: string;
  lastEventAt: string;
  eventCount: number;
  description: string;
  operation: ActivityOperation | null;
  outcome: 'Failure' | null;
  createdAt: string;
  acknowledgedByName: string | null;
  acknowledgedAt: string | null;
  note: string | null;
  /** Unusual behaviour (D166): true for an alert comparing the person with their own past. */
  unusual: boolean;
  /** What was usual for them: a busy day's files and folders, the hours they work in, or the places they used. */
  usual: number | null;
  /** The unusual hours ("02:00 to 04:00") or the sensitive folders they hadn't used. */
  detail: string | null;
}
export interface ActivityAlertList {
  items: ActivityAlert[];
  open: number;
}
/** One file server whose people Data Scope has learned (D166). */
export interface UnusualSource {
  serverId: string;
  hostname: string;
  learnedOn: string | null;
  /** People with activity in the learning days. */
  people: number;
  /** Of those, the ones with enough active days to be judged. */
  judged: number;
}
export interface Unusual {
  on: boolean;
  learnDays: number;
  minActiveDays: number;
  sources: UnusualSource[];
}

// ---------- System settings (D123) ----------
export type SettingKind = 'Whole' | 'Toggle' | 'Text' | 'Url' | 'Email' | 'EmailList' | 'Choice';
/** Where the value in effect comes from: built in, datascope.json (or appsettings.json), or saved on System settings. */
export type SettingSource = 'Default' | 'File' | 'Here';
export interface SettingChoice {
  value: string;
  label: string;
}
export interface SettingGroup {
  id: string;
  title: string;
  description: string;
}
export interface Setting {
  key: string;
  group: string;
  label: string;
  help: string;
  kind: SettingKind;
  /** The value in effect, as text; null when empty. Lists use `values`. */
  value: string | null;
  values: string[] | null;
  default: string | null;
  min: number;
  max: number;
  /** For whole numbers: what 0 means, such as "No limit" or "Keep forever". */
  zeroLabel: string | null;
  optional: boolean;
  unit: string | null;
  choices: SettingChoice[];
  /** When a change takes effect, in plain words. */
  applies: string;
  source: SettingSource;
  /** Saved here while datascope.json also sets it. */
  overridesFile: boolean;
  updatedAt: string | null;
  updatedByName: string | null;
}
export interface SystemSettingsInfo {
  groups: SettingGroup[];
  settings: Setting[];
  timeZone: string;
  /** datascope.json holds a sign-in for the mail server, set with the setup tool. */
  emailSignIn: boolean;
}
/** A value to save: text, a list of addresses, or null to go back to the file or built-in value. */
export type SettingValue = string | string[] | null;
export interface TestEmail {
  id: number;
  /** Pending until sent; a failed try leaves it Pending with the error and tries again. */
  state: 'Pending' | 'Sent' | 'Failed' | 'Skipped';
  error: string | null;
  sentAt: string | null;
}

// ---------- Scan scope and exclusions (D124) ----------
export type ExclusionKind = 'Folder' | 'FileType';
/** Skip: not listed or read at all. ListOnly: listed with permissions, contents never read. */
export type ExclusionEffect = 'Skip' | 'ListOnly';
export interface ScopeImpact {
  files: number;
  bytes: number;
  folders: number;
  restricted: number;
  confidential: number;
  /** Files on the share, or on every scanned share for a file type, to compare with. */
  totalFiles: number;
  /** Hides enough that saving needs confirming; `warnings` says why. */
  large: boolean;
  warnings: string[];
  notes: string[];
}
export interface ScanExclusion {
  id: number;
  kind: ExclusionKind;
  effect: ExclusionEffect;
  shareId: string | null;
  shareUnc: string | null;
  path: string | null;
  extension: string | null;
  target: string;
  reason: string;
  enabled: boolean;
  createdAt: string;
  createdByName: string;
  updatedAt: string;
  updatedByName: string;
  impact: ScopeImpact;
}
export interface ScopeShare {
  id: string;
  serverId: string;
  hostname: string;
  name: string;
  uncPath: string;
  kind: 'Disk' | 'Special' | 'Other';
  included: boolean;
  state: ShareState;
  fileCount: number;
  totalBytes: number;
  lastScannedAt: string | null;
  exclusionReason: string | null;
  excludedByName: string | null;
  excludedAt: string | null;
}
export interface ScopeCoverage {
  sharesIncluded: number;
  sharesExcluded: number;
  specialShares: number;
  folderExclusions: number;
  contentExclusions: number;
  disabled: number;
  /** Files listed but never opened because of an exclusion. */
  filesNotRead: number;
  /** Folders the latest scans left out because of an exclusion. */
  foldersNotScanned: number;
  filesInExcludedShares: number;
}
export interface ScopeChange {
  at: string;
  actorName: string;
  action: string;
  target: string;
  reason: string | null;
}
export interface ScanScopeInfo {
  shares: ScopeShare[];
  exclusions: ScanExclusion[];
  coverage: ScopeCoverage;
  history: ScopeChange[];
}
export interface ExclusionInput {
  kind: ExclusionKind;
  effect: ExclusionEffect;
  shareId?: string | null;
  path?: string;
  extension?: string;
  reason: string;
  confirm?: boolean;
}

// ---------- Sensitive Data Exposure Map (D125) ----------
/** Open, PossiblyOpen or Unknown for any domain user; Limited when only some people can read. */
export type ExposureMapStatus = 'Open' | 'PossiblyOpen' | 'Unknown' | 'Limited';
export interface ExposureMapSummary {
  sensitiveFolders: number;
  restrictedFiles: number;
  confidentialFiles: number;
  openFolders: number;
  possiblyOpenFolders: number;
  unknownFolders: number;
  limitedFolders: number;
  /** Sensitive files any domain user can read. */
  openFiles: number;
  /** Enabled accounts that can read at least one sensitive file; peoplePossible only might. */
  people: number;
  peoplePossible: number;
  groups: number;
}
/** Everything that limits how far the map can be trusted. */
export interface ExposureMapCoverage {
  filesNotRead: number;
  filesExcluded: number;
  sharesNotFullyScanned: number;
  sharesLeftOut: number;
  sharesPermissionsNotRead: number;
  foldersUnknown: number;
  foldersUncertain: number;
  disabledAccounts: number;
  truncated: boolean;
  folderLimit: number;
}
export interface ExposureMapShare {
  shareId: string;
  serverId: string;
  hostname: string;
  name: string;
  uncPath: string;
  included: boolean;
  state: ShareState;
  sharePermissions: PermissionState;
  restricted: number;
  confidential: number;
  sensitiveFolders: number;
  openFolders: number;
  possiblyOpenFolders: number;
  unknownFolders: number;
  people: number;
  peoplePossible: number;
  filesNotRead: number;
}
export interface ExposureGrant {
  trustee: Trustee;
  rights: RightsLabel;
  people: number;
  peoplePossible: number;
}
export interface ExposureMapFolder {
  folderId: string;
  shareId: string;
  serverId: string;
  hostname: string;
  shareName: string;
  uncPath: string;
  restricted: number;
  confidential: number;
  status: ExposureMapStatus;
  anyDomainUser: AccessRange;
  people: number;
  peoplePossible: number;
  grants: ExposureGrant[];
}
export interface ExposureMapGroup {
  trustee: Trustee;
  people: number;
  peoplePossible: number;
  membersCertain: number | null;
  membersPossible: number | null;
  folders: number;
  shares: number;
  restricted: number;
  confidential: number;
  highestRights: RightsLabel;
}
export interface ExposureMapPerson {
  sid: string;
  name: string;
  restricted: number;
  confidential: number;
  possibleFiles: number;
  folders: number;
  shares: number;
  via: string[];
}
export interface ExposureMap {
  summary: ExposureMapSummary;
  coverage: ExposureMapCoverage;
  shares: ExposureMapShare[];
  folders: ExposureMapFolder[];
  foldersTotal: number;
  groups: ExposureMapGroup[];
  groupsTotal: number;
  people: ExposureMapPerson[];
  peopleTotal: number;
  minimum: 'Confidential' | 'Restricted';
  directory: DirectoryRef;
  /** The compliance standard the map was narrowed to, if any. */
  standard: string | null;
}
export interface ExposureVia {
  trustee: Trustee;
  certain: boolean;
  rights: RightsLabel;
}
export interface PersonExposureFolder {
  folderId: string;
  shareId: string;
  uncPath: string;
  restricted: number;
  confidential: number;
  status: ExposureMapStatus;
  rights: RightsLabel;
  upTo: RightsLabel;
  certain: boolean;
  via: ExposureVia[];
}
export interface PersonExposure {
  account: UserAccess;
  totals: ExposureMapPerson | null;
  folders: PersonExposureFolder[];
  foldersTotal: number;
  directory: DirectoryRef;
}
export interface GroupExposureFolder {
  folderId: string;
  shareId: string;
  uncPath: string;
  restricted: number;
  confidential: number;
  status: ExposureMapStatus;
  rights: RightsLabel;
  people: number;
  peoplePossible: number;
}
export interface GroupExposure {
  trustee: Trustee;
  totals: ExposureMapGroup | null;
  folders: GroupExposureFolder[];
  foldersTotal: number;
  directory: DirectoryRef;
}
export interface ExposureScope {
  serverId?: string;
  shareId?: string;
  minSensitivity?: 'Confidential' | 'Restricted';
  /** Only files under this turned-on compliance standard (D126). */
  standard?: string;
}

// ---------- compliance standards (D126) ----------

export interface ComplianceRequirement {
  ref: string;
  text: string;
  /** What Data Scope shows that helps with it; never a claim that it is met. */
  helps: string;
}
export interface ComplianceCounts {
  files: number;
  restricted: number;
  confidential: number;
  folders: number;
  shares: number;
}
export interface ComplianceStandard {
  id: string;
  name: string;
  summary: string;
  enabled: boolean;
  categories: FindingCategory[];
  defaultCategories: FindingCategory[];
  minConfidence: 'Medium' | 'High';
  reason: string | null;
  updatedAt: string | null;
  updatedByName: string | null;
  /** What the standard covers that Data Scope can't find. */
  notDetected: string[];
  requirements: ComplianceRequirement[];
  /** Only for a turned-on standard. */
  counts: ComplianceCounts | null;
}
export interface ComplianceStandards {
  standards: ComplianceStandard[];
  categories: FindingCategory[];
  /** Files whose contents were never read: any of them may hold data under a standard. */
  filesNotRead: number;
}
export interface ComplianceChange {
  at: string;
  actorName: string;
  action: 'compliance.enable' | 'compliance.disable' | 'compliance.change';
  target: string;
  reason: string | null;
}
export interface ComplianceUpdate {
  enabled?: boolean;
  categories?: FindingCategory[];
  minConfidence?: 'Medium' | 'High';
  reason: string;
}

// ---------- custom detection rules (D133) ----------

export type CustomRuleKind = 'Keywords' | 'Pattern';
/** What a file with a match becomes: Restricted, at least Confidential, or listed without changing the label. */
export type CustomRuleEffect = 'Restricted' | 'Confidential' | 'ListOnly';
export interface CustomRule {
  id: number;
  /** DS-CUSTOM-{id}, as findings carry it. */
  ruleId: string;
  name: string;
  description: string | null;
  kind: CustomRuleKind;
  pattern: string | null;
  keywords: string[];
  caseSensitive: boolean;
  nearWords: string[];
  minMatches: number;
  effect: CustomRuleEffect;
  enabled: boolean;
  /** The rule in a sentence. */
  summary: string;
  reason: string;
  createdAt: string;
  createdByName: string;
  updatedAt: string;
  updatedByName: string;
  /** Current files it found something in. */
  files: number;
}
export interface CustomRuleTemplate {
  id: string;
  name: string;
  description: string;
  kind: CustomRuleKind;
  pattern: string | null;
  keywords: string[];
  nearWords: string[];
  minMatches: number;
  effect: CustomRuleEffect;
  /** Made-up text the template matches, for the test box. */
  example: string;
}
export interface CustomRuleLimits {
  maxRules: number;
  maxEnabled: number;
  maxNameChars: number;
  maxDescriptionChars: number;
  maxPatternChars: number;
  maxKeywords: number;
  maxKeywordChars: number;
  maxNearWords: number;
  maxMinMatches: number;
  maxSampleChars: number;
  nearWindow: number;
  maxMatchChars: number;
}
export interface CustomRules {
  rules: CustomRule[];
  templates: CustomRuleTemplate[];
  limits: CustomRuleLimits;
  policyVersion: string;
  /** Current files last classified with other rules: read again on the next scan. */
  filesToReread: number;
}
export interface CustomRuleDraft {
  name: string;
  description: string;
  kind: CustomRuleKind;
  pattern: string;
  keywords: string[];
  caseSensitive: boolean;
  nearWords: string[];
  minMatches: number;
  effect: CustomRuleEffect;
}
export interface CustomRuleTestResult {
  /** Different matching values found. */
  values: number;
  /** Whether a file holding the text would be reported (enough values for the minimum). */
  reported: boolean;
  evidence: string[];
  label: Sensitivity;
  truncated: boolean;
}
export interface CustomRuleChange {
  at: string;
  actorName: string;
  action: 'custom-rule.add' | 'custom-rule.change' | 'custom-rule.enable' | 'custom-rule.disable' | 'custom-rule.remove';
  target: string;
  reason: string | null;
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    /** The server's reasons by field, when it refused the values sent. */
    readonly errors: Record<string, string[]> = {},
    /** Anything else the server sent with its refusal, such as what a change would hide. */
    readonly details: Record<string, unknown> = {},
  ) {
    super(message);
  }
}

const BASE = '/api/v1';

// ---------- Reports and dashboard (M5) ----------
export interface ReportInfo {
  id: string;
  title: string;
  about: string;
  /** Covers the last `days` days rather than the latest state. */
  period: boolean;
  /** About one turned-on compliance standard, which must be chosen (D135). */
  standard: boolean;
}
export type ReportFormat = 'csv' | 'html';
export interface ReportScope {
  serverId?: string;
  shareId?: string;
}
export type RiskSeverity = 'High' | 'Medium' | 'Low';
export type RiskRule =
  | 'open-sensitive'
  | 'secrets'
  | 'open-change'
  | 'no-owner'
  | 'owner-gone'
  | 'disabled-account'
  | 'deleted-account'
  | 'direct-user'
  | 'unusual-order'
  | 'inheritance-off'
  | 'not-covered'
  | 'nesting-loop'
  | 'stale-sensitive'
  | 'review-overdue';
/** A rule that matched a place, with why it matters and what to do. Severity follows fixed rules, never a score. */
export interface Risk {
  id: string;
  rule: RiskRule;
  severity: RiskSeverity;
  title: string;
  where: string;
  serverId: string | null;
  shareId: string | null;
  folderId: string | null;
  restricted: number | null;
  confidential: number | null;
  who: string | null;
  whoSid: string | null;
  access: string | null;
  certainty: string | null;
  count: number | null;
  why: string;
  fix: string;
  /** Open unless an accept or snooze still applies. A risk that became more severe is Open again. */
  status: RiskStatus;
  decision: RiskDecision | null;
  /** Ids of the turned-on compliance standards whose data the risk involves, in catalog order. */
  standards: string[];
  /** The severity before it was raised one step for those standards. */
  raisedFrom: RiskSeverity | null;
}
export type RiskStatus = 'Open' | 'Accepted' | 'Snoozed';
export type RiskDecisionKind = 'Accepted' | 'Snoozed';
export type RiskView = 'open' | 'accepted' | 'snoozed' | 'all';
export interface RiskDecision {
  id: number;
  kind: RiskDecisionKind;
  note: string | null;
  until: string;
  /** The severity when it was decided. */
  severity: RiskSeverity;
  decidedByName: string;
  decidedAt: string;
  /** The risk is now more severe than when this was decided, so it shows again. */
  superseded: boolean;
}
export interface RiskRuleSummary {
  id: RiskRule;
  name: string;
  high: number;
  medium: number;
  low: number;
}
export interface RiskList {
  items: Risk[];
  high: number;
  medium: number;
  low: number;
  /** Risks in scope that an accept or snooze hides. */
  accepted: number;
  snoozed: number;
  rules: RiskRuleSummary[];
  truncated: boolean;
  limits: string[];
  checkedAt: string;
  lastScanAt: string | null;
  directorySyncedAt: string | null;
}

export interface Dashboard {
  generatedAt: string;
  domain: string | null;
  directorySyncedAt: string | null;
  coverage: {
    servers: number;
    sharesIncluded: number;
    sharesScanned: number;
    sharesPartial: number;
    sharesUnavailable: number;
    sharesNotScanned: number;
    files: number;
    bytes: number;
    filesRead: number;
    filesNotRead: number;
    gaps: number;
    lastScanAt: string | null;
  };
  sensitive: { restricted: number; confidential: number; policyVersion: string };
  exposure: { open: number; possiblyOpen: number; unknown: number; sensitiveFilesOpen: number; truncated: boolean; top: ExposureItem[] };
  owners: { owned: number; unowned: number; orphanedOwners: number };
  reviews: { openCampaigns: number; reviewsOpen: number; reviewsOverdue: number };
  age: { label: string; files: number; bytes: number }[];
  /** One point per day a scan finished in the last year, oldest first. Thinned to weekly when there are many. */
  trend: TrendPoint[];
}

export interface TrendPoint {
  /** yyyy-mm-dd (UTC). */
  day: string;
  shares: number;
  files: number;
  bytes: number;
  restricted: number;
  confidential: number;
  filesNotRead: number;
}

/** One day's posture (D165), as Data Scope last measured it that day. */
export interface ProgressPoint {
  /** yyyy-mm-dd (UTC). */
  day: string;
  takenAt: string;
  lastScanAt: string | null;
  risksHigh: number;
  risksMedium: number;
  risksLow: number;
  risksAccepted: number;
  risksSnoozed: number;
  /** The risk check stopped early somewhere that day, so its risk counts may be low. */
  risksLimited: boolean;
  files: number;
  filesNotRead: number;
  restricted: number;
  confidential: number;
  sensitiveOpen: number;
  sharesUnowned: number;
}

export interface Progress {
  days: number;
  /** The first day the range covers. */
  from: string;
  /** The first day Data Scope kept; a range starting earlier is short because nothing was kept before. */
  keptSince: string | null;
  /** Oldest first. */
  points: ProgressPoint[];
  /** A long range shows each week's last day, with its first and last day. */
  thinned: boolean;
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(BASE + path, {
    credentials: 'same-origin',
    // The API refuses changes without this header, so another site can't make the browser act for the user.
    headers: { 'X-DataScope-Client': 'web', ...(init?.body ? { 'Content-Type': 'application/json' } : {}) },
    ...init,
  });
  if (!res.ok) {
    let message = res.status === 403 ? 'Your role does not allow this.' : `Request failed (${res.status}).`;
    let errors: Record<string, string[]> = {};
    let details: Record<string, unknown> = {};
    try {
      const problem = await res.json();
      details = problem && typeof problem === 'object' ? (problem as Record<string, unknown>) : {};
      errors = problem?.errors && typeof problem.errors === 'object' ? (problem.errors as Record<string, string[]>) : {};
      const field = Object.values(errors)[0]?.[0];
      message = field || problem?.title || message;
    } catch {
      /* not JSON */
    }
    throw new ApiError(res.status, message, errors, details);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

const qs = (params: Record<string, string | number | boolean | undefined>) => {
  const entries = Object.entries(params).filter(([, v]) => v !== undefined) as [string, string | number | boolean][];
  return entries.length ? '?' + entries.map(([k, v]) => `${k}=${encodeURIComponent(String(v))}`).join('&') : '';
};

/** Stale and duplicate data (D119). Data Scope reports what could be archived or removed; it never moves or deletes files. */
export interface CleanupTotals {
  files: number;
  bytes: number;
  staleFiles: number;
  staleBytes: number;
  staleRestricted: number;
  staleConfidential: number;
  /** Older than the cutoff, but file activity shows someone opened or changed them since: not stale. */
  inUse: number;
  noDate: number;
  duplicateGroups: number;
  duplicateCopies: number;
  duplicateBytes: number;
}
export interface CleanupShare {
  shareId: string;
  serverId: string;
  uncPath: string;
  files: number;
  bytes: number;
  staleFiles: number;
  staleBytes: number;
  staleRestricted: number;
  staleConfidential: number;
  lastScannedAt: string | null;
  source?: ItemSource;
}
export interface CleanupFolder {
  folderId: string;
  shareId: string;
  uncPath: string;
  staleFiles: number;
  staleBytes: number;
  restricted: number;
  confidential: number;
  newestChange: string | null;
}
export interface CleanupSummary {
  years: number;
  defaultYears: number;
  cutoff: string;
  totals: CleanupTotals;
  shares: CleanupShare[];
  folders: CleanupFolder[];
  servers: number;
  /** Servers whose file activity is collected, so recent opens can count. */
  activityServers: number;
  minDuplicateBytes: number;
  lastScanAt: string | null;
}
export interface StaleFile {
  fileId: string;
  shareId: string;
  folderId: string;
  path: string;
  name: string;
  size: number;
  lastChanged: string | null;
  label: Sensitivity;
  /** For an email, name is its subject with digits and secrets masked (D143). */
  source?: ItemSource;
}
export interface DuplicateCopy {
  fileId: string;
  shareId: string;
  path: string;
  modified: string | null;
  label: Sensitivity;
}
export interface DuplicateGroup {
  name: string;
  size: number;
  copies: number;
  shares: number;
  wasted: number;
  sameModified: boolean;
  label: Sensitivity;
  locations: DuplicateCopy[];
}
export interface DuplicateList {
  items: DuplicateGroup[];
  limit: number;
  offset: number;
  total: number;
  copies: number;
  wasted: number;
  minDuplicateBytes: number;
}
export interface CleanupScope {
  serverId?: string;
  shareId?: string;
  /** Only file shares. */
  source?: SourceFilter;
}

/** The search box at the top of every page (D128). */
export type SearchKind = 'User' | 'Group' | 'Share' | 'Folder' | 'File' | 'Server';
export interface SearchHit {
  /** The SID for people and groups, otherwise the row's id. */
  id: string;
  title: string;
  detail: string | null;
  /** A file's sensitivity once its contents have been read. */
  label: Sensitivity | null;
  /** A pasted path names exactly this. */
  exact: boolean;
  disabled: boolean;
}
export interface SearchGroup {
  kind: SearchKind;
  items: SearchHit[];
  more: boolean;
  timedOut: boolean;
}
export interface SearchResult {
  query: string;
  groups: SearchGroup[];
  /** False for searches too short to look through file and folder names. */
  filesSearched: boolean;
  directoryAt: string | null;
}

/** Why something is listed in a person search (D160). */
export type PersonReason = 'named' | 'evidence' | 'owner' | 'dataOwner';

export interface PersonAccount {
  sid: string;
  name: string;
  displayName: string | null;
  userPrincipalName: string | null;
  mail: string | null;
  enabled: boolean;
  title: string | null;
  department: string | null;
  lastLogon: string | null;
  groups: string[];
}

export interface PersonItem {
  why: PersonReason;
  kind: 'File' | 'Folder';
  /** The file's or folder's id. */
  id: string;
  shareId: string;
  source: 'files';
  /** The share it is in. */
  place: string;
  /** The folder inside it; empty at the top. */
  path: string;
  name: string;
  label: Sensitivity | null;
  /** What matched. */
  detail: string | null;
}

export interface PersonSection {
  why: PersonReason;
  count: number;
  /** More than the limit were found; only the first are listed. */
  more: boolean;
  /** This part took too long and is left out. */
  timedOut: boolean;
}

export interface PersonTerms {
  sid: string | null;
  names: string[];
  addresses: string[];
}

export interface PersonSearchResult {
  searchedAt: string;
  typed: PersonTerms;
  /** What was searched: the typed terms plus the accounts' names and addresses. */
  searched: PersonTerms;
  accounts: PersonAccount[];
  directoryAt: string | null;
  sections: PersonSection[];
  items: PersonItem[];
  activity: {
    fileEvents: number;
    fileFirst: string | null;
    fileLast: string | null;
    fileShares: string[];
    timedOut: boolean;
  };
  limit: number;
}

export interface PersonSearchRequest {
  sid?: string;
  names: string[];
  addresses: string[];
  reason: string;
}

export const api = {
  me: () => call<Me>('/me'),
  search: (q: string, init?: RequestInit) => call<SearchResult>('/search' + qs({ q }), init),
  searchPerson: (request: PersonSearchRequest) => call<PersonSearchResult>('/people/search', { method: 'POST', body: JSON.stringify(request) }),
  /** Anonymous: tells the UI it is on a Data Scope server without starting Windows sign-in. */
  signInInfo: () => call<{ product: string; signIn: string }>('/sign-in'),
  servers: (includeRemoved?: boolean) => call<Page<Server>>('/servers' + qs({ includeRemoved, limit: 200 })),
  enroll: (hostname: string) => call<Server>('/servers', { method: 'POST', body: JSON.stringify({ hostname }) }),
  setSchedule: (id: string, schedule: ScanScheduleInput) =>
    call<Server>(`/servers/${encodeURIComponent(id)}/schedule`, { method: 'PUT', body: JSON.stringify(schedule) }),
  removeServer: (id: string) => call<ServerRemoval>(`/servers/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  jobs: (serverId?: string, limit = 20) => call<Page<ScanJob>>('/scan-jobs' + qs({ serverId, limit })),
  startScan: (serverId: string, kind: ScanKind = 'Quick') => call<ScanJob>('/scan-jobs', { method: 'POST', body: JSON.stringify({ serverId, kind }) }),
  controlScan: (id: string, action: 'pause' | 'resume' | 'cancel') => call<ScanJob>(`/scan-jobs/${id}/${action}`, { method: 'POST' }),
  shares: (serverId?: string) => call<Page<Share>>('/shares' + qs({ serverId, limit: 200 })),
  /** Leaving a share out needs a reason (D124); including it again doesn't. */
  setIncluded: (shareId: string, included: boolean, reason?: string) =>
    call<Share>(`/shares/${shareId}`, { method: 'PATCH', body: JSON.stringify({ included, reason: reason ?? null }) }),
  scanScope: () => call<ScanScopeInfo>('/scan-scope'),
  scopeImpact: (x: Omit<ExclusionInput, 'reason' | 'confirm'>) =>
    call<ScopeImpact>('/scan-scope/impact' + qs({ kind: x.kind, effect: x.effect, shareId: x.shareId ?? undefined, path: x.path, extension: x.extension })),
  addExclusion: (x: ExclusionInput) => call<ScanExclusion>('/scan-scope/exclusions', { method: 'POST', body: JSON.stringify(x) }),
  updateExclusion: (id: number, change: { enabled?: boolean; reason?: string; confirm?: boolean }) =>
    call<ScanExclusion>(`/scan-scope/exclusions/${id}`, { method: 'PUT', body: JSON.stringify(change) }),
  removeExclusion: (id: number) => call<void>(`/scan-scope/exclusions/${id}`, { method: 'DELETE' }),
  folders: (shareId: string, parentId?: string, offset = 0) => call<Page<Folder>>(`/shares/${shareId}/folders` + qs({ parentId, limit: 200, offset })),
  files: (folderId: string, offset = 0) => call<Page<FileEntry>>(`/folders/${folderId}/files` + qs({ limit: 200, offset })),
  shareGaps: (shareId: string, offset = 0) => call<Page<Gap>>(`/shares/${shareId}/gaps` + qs({ limit: 200, offset })),
  coverage: (source?: SourceFilter) => call<Coverage>('/coverage' + qs({ source })),
  classificationSummary: (serverId?: string, standard?: string, source?: SourceFilter) =>
    call<ClassificationSummary>('/classification/summary' + qs({ serverId, standard, source })),
  classifiedFiles: (filters: FileFilters, offset = 0, limit = 100) =>
    call<Page<ClassifiedFile>>('/classification/files' + qs({ ...filters, limit, offset })),
  fileClassification: (fileId: string) => call<FileClassification>(`/files/${fileId}/classification`),
  overrideClassification: (fileId: string, sensitivity: Sensitivity | null, reason: string) =>
    call<FileClassification>(`/files/${fileId}/classification-override`, { method: 'POST', body: JSON.stringify({ sensitivity, reason }) }),

  directory: () => call<DirectoryStatus>('/directory'),
  syncDirectory: () => call<DirectorySnapshot>('/directory/sync', { method: 'POST' }),
  principals: (search?: string, kind?: PrincipalKind, offset = 0, limit = 50) =>
    call<Page<Principal>>('/directory/principals' + qs({ search: search || undefined, kind, limit, offset })),
  principal: (sid: string) => call<PrincipalDetail>(`/directory/principals/${encodeURIComponent(sid)}`),
  shareAccess: (shareId: string) => call<ShareAccess>(`/access/shares/${shareId}`),
  boundaries: (shareId: string, offset = 0, limit = 200) => call<Page<Boundary>>(`/access/shares/${shareId}/boundaries` + qs({ limit, offset })),
  folderAccess: (folderId: string) => call<FolderAccess>(`/access/folders/${folderId}`),
  folderUsers: (folderId: string, opts: { target?: AccessTarget; search?: string; offset?: number; limit?: number } = {}) =>
    call<Page<UserAccess>>(`/access/folders/${folderId}/users` + qs({ target: opts.target, search: opts.search || undefined, limit: opts.limit ?? 100, offset: opts.offset ?? 0 })),
  explain: (folderId: string, sid: string, target: AccessTarget = 'Files') =>
    call<AccessExplanation>(`/access/folders/${folderId}/explain` + qs({ sid, target })),
  fileAccess: (fileId: string) => call<FileAccess>(`/files/${fileId}/access`),
  exposure: (opts: { shareId?: string; minSensitivity?: 'Confidential' | 'Restricted'; status?: ExposureStatus; offset?: number; limit?: number } = {}) =>
    call<Exposure>('/access/exposure' + qs({ ...opts, limit: opts.limit ?? 100, offset: opts.offset ?? 0 })),
  complianceStandards: () => call<ComplianceStandards>('/compliance/standards'),
  complianceHistory: () => call<ComplianceChange[]>('/compliance/history'),
  setComplianceStandard: (id: string, change: ComplianceUpdate) =>
    call<ComplianceStandard>(`/compliance/standards/${encodeURIComponent(id)}`, { method: 'PUT', body: JSON.stringify(change) }),
  customRules: () => call<CustomRules>('/custom-rules'),
  customRuleHistory: () => call<CustomRuleChange[]>('/custom-rules/history'),
  addCustomRule: (draft: CustomRuleDraft & { enabled?: boolean; reason: string }) =>
    call<CustomRule>('/custom-rules', { method: 'POST', body: JSON.stringify(draft) }),
  updateCustomRule: (id: number, change: Partial<CustomRuleDraft> & { enabled?: boolean; reason: string }) =>
    call<CustomRule>(`/custom-rules/${id}`, { method: 'PUT', body: JSON.stringify(change) }),
  removeCustomRule: (id: number) => call<void>(`/custom-rules/${id}`, { method: 'DELETE' }),
  testCustomRule: (draft: Omit<CustomRuleDraft, 'name' | 'description'>, sample: string) =>
    call<CustomRuleTestResult>('/custom-rules/test', { method: 'POST', body: JSON.stringify({ ...draft, sample }) }),
  exposureMap: (scope: ExposureScope = {}) => call<ExposureMap>('/exposure-map' + qs({ ...scope })),
  personExposure: (sid: string, scope: ExposureScope = {}) => call<PersonExposure>(`/exposure-map/people/${encodeURIComponent(sid)}` + qs({ ...scope })),
  groupExposure: (sid: string, scope: ExposureScope = {}) => call<GroupExposure>(`/exposure-map/groups/${encodeURIComponent(sid)}` + qs({ ...scope })),
  reach: (sid: string, opts: { target?: AccessTarget; offset?: number; limit?: number } = {}) =>
    call<Reach>(`/access/principals/${encodeURIComponent(sid)}/reach` + qs({ target: opts.target, limit: opts.limit ?? 200, offset: opts.offset ?? 0 })),
  owners: (opts: { search?: string; serverId?: string; orphaned?: boolean; offset?: number; limit?: number } = {}) =>
    call<Page<OwnerAssignment>>('/owners' + qs({ search: opts.search || undefined, serverId: opts.serverId, orphaned: opts.orphaned || undefined, limit: opts.limit ?? 200, offset: opts.offset ?? 0 })),
  unowned: (offset = 0, limit = 200) => call<Page<UnownedShare>>('/owners/unowned' + qs({ limit, offset })),
  ownerSummary: () => call<OwnerSummary>('/owners/summary'),
  folderOwner: (folderId: string) => call<FolderOwner>(`/folders/${folderId}/owner`),
  assignOwner: (folderId: string, request: AssignOwnerRequest) =>
    call<OwnerAssignment>(`/folders/${folderId}/owner`, { method: 'PUT', body: JSON.stringify(request) }),
  endOwner: (folderId: string, reason: string) => call<void>(`/folders/${folderId}/owner/end`, { method: 'POST', body: JSON.stringify({ reason }) }),

  campaigns: () => call<Campaign[]>('/campaigns'),
  reviewExemptions: () => call<ReviewExemption[]>('/review-exemptions'),
  addReviewExemption: (sid: string, reason: string) =>
    call<ReviewExemption>('/review-exemptions', { method: 'POST', body: JSON.stringify({ sid, reason }) }),
  removeReviewExemption: (id: number) => call<void>(`/review-exemptions/${id}`, { method: 'DELETE' }),
  cleanup: (scope: CleanupScope, years?: number) => call<CleanupSummary>(`/cleanup${qs({ ...scope, years })}`),
  staleFiles: (scope: CleanupScope & { folderId?: string; sensitive?: boolean }, years: number, sort: 'size' | 'oldest', offset = 0, limit = 50) =>
    call<Page<StaleFile>>(`/cleanup/stale-files${qs({ ...scope, years, sort, offset, limit })}`),
  duplicates: (scope: CleanupScope, offset = 0, limit = 50) => call<DuplicateList>(`/cleanup/duplicates${qs({ ...scope, offset, limit })}`),
  campaign: (id: string) => call<CampaignDetail>(`/campaigns/${id}`),
  createCampaign: (request: CampaignRequest) => call<Campaign>('/campaigns', { method: 'POST', body: JSON.stringify(request) }),
  closeCampaign: (id: string, note?: string) => call<Campaign>(`/campaigns/${id}/close`, { method: 'POST', body: JSON.stringify({ note }) }),
  verifyCampaign: (id: string) => call<CampaignVerification>(`/campaigns/${id}/verify`),
  evidenceUrl: (id: string, format: 'csv' | 'json') => `${BASE}/campaigns/${id}/evidence.${format}`,
  review: (id: string) => call<ReviewDetail>(`/reviews/${id}`),
  reviewItemUsers: (id: string, itemId: number, search?: string, offset = 0, limit = 100) =>
    call<ReviewItemUser[]>(`/reviews/${id}/items/${itemId}/users` + qs({ search: search || undefined, limit, offset })),
  reassignReview: (id: string, delegateSid: string | null) =>
    call<ReviewDetail>(`/reviews/${id}/delegate`, { method: 'POST', body: JSON.stringify({ delegateSid }) }),

  myReviews: () => call<ReviewSummary[]>('/my/reviews'),
  myReview: (id: string) => call<ReviewDetail>(`/my/reviews/${id}`),
  myReviewItemUsers: (id: string, itemId: number, search?: string, offset = 0, limit = 100) =>
    call<ReviewItemUser[]>(`/my/reviews/${id}/items/${itemId}/users` + qs({ search: search || undefined, limit, offset })),
  decide: (id: string, decisions: DecisionInput[]) =>
    call<ReviewDetail>(`/my/reviews/${id}/decisions`, { method: 'POST', body: JSON.stringify({ decisions }) }),
  submitReview: (id: string) => call<ReviewDetail>(`/my/reviews/${id}/submit`, { method: 'POST', body: JSON.stringify({ confirm: true }) }),
  delegateReview: (id: string, delegateSid: string | null) =>
    call<ReviewDetail>(`/my/reviews/${id}/delegate`, { method: 'POST', body: JSON.stringify({ delegateSid }) }),
  myOwned: () => call<OwnedResource[]>('/my/owned'),
  people: (search: string) => call<PersonRef[]>('/my/people' + qs({ search })),

  reports: () => call<ReportInfo[]>('/reports'),
  /** A GET the browser opens directly: CSV downloads, the page opens in a new tab for printing or saving as PDF. */
  reportUrl: (id: string, format: ReportFormat, scope: ReportScope = {}, days?: number, standard?: string) =>
    `${BASE}/reports/${encodeURIComponent(id)}` + qs({ format, serverId: scope.serverId, shareId: scope.shareId, days, standard }),
  dashboard: () => call<Dashboard>('/dashboard'),
  risks: (opts: { severity?: RiskSeverity; rule?: RiskRule; shareId?: string; serverId?: string; view?: RiskView; standard?: string } = {}) =>
    call<RiskList>('/risks' + qs({ ...opts, view: opts.view === 'open' ? undefined : opts.view })),
  decideRisk: (riskId: string, kind: RiskDecisionKind, until: string, note?: string) =>
    call<RiskDecision>('/risks/decisions', { method: 'POST', body: JSON.stringify({ riskId, kind, until, note: note || null }) }),
  reopenRisk: (decisionId: number) => call<void>(`/risks/decisions/${decisionId}`, { method: 'DELETE' }),
  activityEvents: (filters: ActivityFilters, cursor?: string, limit = 100) =>
    call<ActivityPage>('/activity/events' + qs({ ...filters, includeChecks: filters.includeChecks || undefined, cursor, limit })),
  activityEvent: (id: number) => call<ActivityEventDetail>(`/activity/events/${id}`),
  activitySummary: (days = 7) => call<ActivitySummary>('/activity/summary' + qs({ days, offsetMinutes: -new Date().getTimezoneOffset() })),
  activitySources: () => call<ActivitySource[]>('/activity/sources'),
  /** Progress over time (D165): the posture day by day over the last 7 to 1830 days. */
  progress: (days?: number) => call<Progress>('/progress' + qs({ days })),
  activityAlerts: (state: 'Open' | 'All' = 'All', limit = 50) => call<ActivityAlertList>('/activity/alerts' + qs({ state, limit })),
  activityUnusual: () => call<Unusual>('/activity/unusual'),
  setActivityAlert: (id: number, state: 'Open' | 'Acknowledged', note?: string) =>
    call<ActivityAlert>(`/activity/alerts/${id}`, { method: 'PUT', body: JSON.stringify({ state, note: note || null }) }),
  setActivitySource: (serverId: string, enabled: boolean) => call<void>(`/activity/sources/${serverId}`, { method: 'PUT', body: JSON.stringify({ enabled }) }),
  setShareLocalPath: (shareId: string, localPath: string | null) =>
    call<ShareMapping>(`/activity/shares/${shareId}/local-path`, { method: 'PUT', body: JSON.stringify({ localPath }) }),
  systemHealth: () => call<HealthReport>('/system-health'),
  systemSettings: () => call<SystemSettingsInfo>('/settings'),
  saveSystemSettings: (values: Record<string, SettingValue>) => call<SystemSettingsInfo>('/settings', { method: 'PUT', body: JSON.stringify({ values }) }),
  sendTestEmail: (to: string) => call<TestEmail>('/settings/test-email', { method: 'POST', body: JSON.stringify({ to }) }),
  testEmail: (id: number) => call<TestEmail>(`/settings/test-email/${id}`),
};
