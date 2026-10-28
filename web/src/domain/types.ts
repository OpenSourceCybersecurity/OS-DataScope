// Domain model for the Data Scope prototype.
// Mirrors docs/starter-spec/04-DATA-MODEL.md closely enough that the .NET API
// can return these shapes later without UI changes.

export const CLASSIFICATIONS = ['Unknown', 'Public', 'Internal', 'Confidential', 'Restricted'] as const;
export type Classification = (typeof CLASSIFICATIONS)[number];

export type Rights = 'None' | 'Read' | 'Modify' | 'FullControl';
export const RIGHTS_ORDER: Rights[] = ['None', 'Read', 'Modify', 'FullControl'];

export type PrincipalType = 'user' | 'group' | 'wellknown' | 'unresolved';

export interface Principal {
  sid: string;
  sam: string;
  displayName: string;
  type: PrincipalType;
  enabled: boolean;
  department?: string;
  title?: string;
  email?: string;
  /** SIDs of groups this principal is a direct member of. */
  memberOf: string[];
  lastLogon?: string;
}

export interface Ace {
  sid: string;
  type: 'allow' | 'deny';
  rights: Rights;
  inherited: boolean;
  /** Raw access mask as captured from Windows, kept for evidence. */
  mask: string;
}

export interface Folder {
  path: string; // relative to share root, e.g. "Payroll/2026"
  inheritanceBroken: boolean;
  /** Explicit NTFS ACL when inheritance is broken; otherwise inherits the share root ACL. */
  ntfsAcl?: Ace[];
}

export type ScanState = 'never' | 'queued' | 'running' | 'paused' | 'complete' | 'partial';

export interface ShareScan {
  state: ScanState;
  lastScan?: string;
  progress: number; // 0..1 while running
}

export interface Share {
  id: string;
  serverId: string;
  name: string;
  unc: string;
  description: string;
  shareAcl: Ace[];
  ntfsAcl: Ace[];
  folders: Folder[];
  scan: ShareScan;
  /** Admin marked this share as intentionally public content. */
  intendedPublic?: boolean;
}

export interface ManagedServer {
  id: string;
  hostname: string;
  fqdn: string;
  os: string;
  state: 'enrolled' | 'pending' | 'error';
  lastSeen: string;
  ip: string;
}

export type FileScanStatus = 'scanned' | 'unsupported' | 'encrypted' | 'denied' | 'corrupt' | 'pending';

export type Confidence = 'high' | 'medium' | 'low';

export interface Finding {
  ruleId: string;
  category: FindingCategory;
  confidence: Confidence;
  count: number;
  /** Redacted snippets only. Raw matches are never stored. */
  evidence: string[];
}

export type FindingCategory =
  | 'US SSN'
  | 'US ITIN'
  | 'Payment card'
  | 'Bank account'
  | 'Credentials'
  | 'Health information'
  | 'Date of birth'
  | "Driver's licence"
  | 'Passport'
  | 'Personal details'
  | 'Custom rule';

export interface ClassificationOverride {
  classification: Classification;
  by: string;
  reason: string;
  at: string;
}

export interface FileItem {
  id: string;
  shareId: string;
  path: string; // relative to share root
  name: string;
  ext: string;
  size: number;
  modified: string;
  created: string;
  ownerSid: string;
  scanStatus: FileScanStatus;
  findings: Finding[];
  policyClassification: Classification;
  override?: ClassificationOverride;
  /** Synthetic content used to exercise detectors. A real install never stores contents. */
  content?: string;
}

export interface OwnerAssignment {
  shareId: string;
  ownerSid?: string;
  backupOwnerSid?: string;
  department?: string;
  purpose?: string;
  cadence: 'Quarterly' | 'Semiannual' | 'Annual';
  confirmed: boolean;
  suggestedOwnerSid?: string;
  suggestionReason?: string;
}

export type ReviewDecision = 'Pending' | 'Keep' | 'Remove' | 'Investigate' | 'Exception';

export interface ReviewItem {
  id: string;
  campaignId: string;
  shareId: string;
  /** Folder whose own ACL grants this (broken inheritance), or '' for the share root. */
  scope: string;
  trusteeSid: string; // group or user from the frozen ACL
  rights: Rights;
  via: 'share+ntfs' | 'ntfs' | 'share';
  memberCount: number;
  members: string[]; // frozen effective member SIDs
  flags: string[];
  decision: ReviewDecision;
  justification?: string;
  decidedBy?: string;
  decidedAt?: string;
  history: { decision: ReviewDecision; by: string; at: string; justification?: string }[];
}

export interface ReviewCampaign {
  id: string;
  name: string;
  shareIds: string[];
  reviewerSid: string;
  backupReviewerSid?: string;
  createdBy: string;
  createdAt: string;
  snapshotAt: string;
  dueAt: string;
  status: 'Draft' | 'Active' | 'Completed' | 'Overdue';
  revision: number;
  emailSentAt?: string;
}

/** Windows Security events relevant to file activity (V1.5 collector preview). */
export interface AuditEvent {
  id: string;
  recordId: number;
  eventId: 4663 | 4656 | 4660 | 4670 | 5140 | 5145;
  time: string;
  serverId: string;
  shareId?: string;
  path?: string;
  principalSid: string;
  operation: 'Read' | 'Write' | 'Delete' | 'Permission change' | 'Share connect' | 'Share access check' | 'Handle request';
  /** How sure we are about what actually happened. */
  certainty: 'confirmed' | 'attempted' | 'access-check';
  outcome: 'success' | 'failure';
  accessMask?: string;
  correlationId?: string;
}

export interface ActivityAlert {
  id: string;
  rule: string;
  severity: Severity;
  time: string;
  principalSid: string;
  shareId: string;
  summary: string;
  eventCount: number;
  state: 'Open' | 'Acknowledged';
}

export type Severity = 'Critical' | 'High' | 'Medium' | 'Low';

export interface Role {
  id: string;
  sid: string;
  label: string;
  role: 'Platform Admin' | 'Security Analyst' | 'Auditor' | 'Data Owner';
}

export interface AppAuditEntry {
  at: string;
  actorSid: string;
  action: string;
  target: string;
}
