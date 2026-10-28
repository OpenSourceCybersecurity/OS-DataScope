// In-browser stand-in for the Data Scope API. Screens only talk to this
// module's state and actions, so swapping it for typed calls to the .NET API
// later keeps the UI unchanged.

import { useSyncExternalStore } from 'react';
import type {
  ActivityAlert,
  AppAuditEntry,
  AuditEvent,
  Classification,
  FileItem,
  ManagedServer,
  OwnerAssignment,
  ReviewCampaign,
  ReviewDecision,
  ReviewItem,
  Role,
  Share,
} from '../domain/types';
import { classify, scanText } from '../domain/detectors';
import { WELL_KNOWN, effectiveAccess, membersOf, type Directory } from '../domain/access';
import {
  GROUPS,
  NOW,
  SERVERS,
  buildActivity,
  buildCampaigns,
  buildDirectory,
  buildFiles,
  buildOwners,
  buildShares,
  userSid,
} from '../data/seed';

export const ROLES: Role[] = [
  { id: 'admin', sid: userSid('dwhitfield'), label: 'Dana Whitfield', role: 'Platform Admin' },
  { id: 'owner-fin', sid: userSid('mlee'), label: 'Marcus Lee', role: 'Data Owner' },
  { id: 'auditor', sid: userSid('ohaddad'), label: 'Omar Haddad', role: 'Auditor' },
];

export interface ScanJob {
  shareId: string;
  startedAt: number;
  total: number;
  done: number;
  state: 'running' | 'paused';
}

export interface Toast {
  id: number;
  text: string;
  tone: 'ok' | 'warn';
}

export interface AppState {
  dir: Directory;
  servers: ManagedServer[];
  shares: Share[];
  files: FileItem[];
  owners: OwnerAssignment[];
  campaigns: ReviewCampaign[];
  items: ReviewItem[];
  events: AuditEvent[];
  alerts: ActivityAlert[];
  jobs: ScanJob[];
  roleId: string;
  toasts: Toast[];
  appLog: AppAuditEntry[];
  version: number;
}

const SYSTEM_SIDS = new Set([WELL_KNOWN.system, WELL_KNOWN.builtinAdmins, WELL_KNOWN.creatorOwner]);

function scanFile(f: FileItem, share: Share): FileItem {
  if (f.scanStatus !== 'scanned' || !f.content) {
    return { ...f, findings: [], policyClassification: f.scanStatus === 'scanned' ? classify([], share.intendedPublic) : 'Unknown' };
  }
  const { findings } = scanText(f.content);
  return { ...f, findings, policyClassification: classify(findings, share.intendedPublic) };
}

/** Freeze the ACL + membership snapshot for a campaign into review items. */
function snapshotItems(dir: Directory, campaign: ReviewCampaign, shares: Share[]): ReviewItem[] {
  const items: ReviewItem[] = [];
  for (const shareId of campaign.shareIds) {
    const share = shares.find((s) => s.id === shareId)!;
    const scopes: [string, typeof share.ntfsAcl][] = [
      ['', share.ntfsAcl],
      ...share.folders.filter((f) => f.inheritanceBroken && f.ntfsAcl).map((f) => [f.path, f.ntfsAcl!] as [string, typeof share.ntfsAcl]),
    ];
    for (const [scope, acl] of scopes) {
      for (const ace of acl) {
        if (ace.type !== 'allow' || SYSTEM_SIDS.has(ace.sid)) continue;
        if (scope && share.ntfsAcl.some((a) => a.sid === ace.sid && a.rights === ace.rights)) continue; // same as root
        const p = dir.get(ace.sid);
        const members = p ? (p.type === 'user' ? [ace.sid] : membersOf(dir, ace.sid)) : [];
        const reachable = members.filter((m) => effectiveAccess(dir, share, m, scope || undefined).rights !== 'None');
        const flags: string[] = [];
        if (!p) flags.push('Unresolved SID');
        if (p?.type === 'user') flags.push('Direct user entry');
        if (p?.type === 'user' && !p.enabled) flags.push('Disabled account');
        if (p && p.type !== 'user' && members.some((m) => dir.get(m)?.enabled === false)) flags.push('Includes disabled accounts');
        if (ace.sid === WELL_KNOWN.everyone || ace.sid === WELL_KNOWN.authenticatedUsers || ace.sid.endsWith('-513')) flags.push('Everyone in the company');
        if (p && p.type === 'group' && [...dir.values()].some((g) => g.type === 'group' && g.memberOf.includes(ace.sid))) flags.push('Has nested groups');
        if (ace.rights === 'FullControl') flags.push('Full control');
        items.push({
          id: `${campaign.id}-${shareId}-${scope || 'root'}-${ace.sid}`,
          campaignId: campaign.id,
          shareId,
          scope,
          trusteeSid: ace.sid,
          rights: ace.rights,
          via: 'share+ntfs',
          memberCount: reachable.length,
          members: reachable,
          flags,
          decision: 'Pending',
          history: [],
        });
      }
    }
  }
  return items;
}

function init(): AppState {
  const dir: Directory = new Map(buildDirectory().map((p) => [p.sid, p]));
  const shares = buildShares();
  const files = buildFiles().map((f) => scanFile(f, shares.find((s) => s.id === f.shareId)!));
  const campaigns = buildCampaigns();
  const items = campaigns.flatMap((c) => snapshotItems(dir, c, shares));
  // Pre-recorded decisions so the demo shows a campaign in flight.
  const decide = (campaignId: string, trustee: string, decision: ReviewDecision, by: string, at: string, justification?: string) => {
    for (const it of items.filter((i) => i.campaignId === campaignId && i.trusteeSid === trustee)) {
      it.decision = decision;
      it.decidedBy = by;
      it.decidedAt = at;
      it.justification = justification;
      it.history.push({ decision, by, at, justification });
    }
  };
  const mlee = userSid('mlee');
  const t = (d: number) => new Date(NOW.getTime() - d * 864e5).toISOString();
  decide('cmp-fin-q4', GROUPS.Finance_Write, 'Keep', mlee, t(3), 'Finance staff need to edit workpapers.');
  decide('cmp-fin-q4', GROUPS.Finance_Read, 'Keep', mlee, t(3));
  decide('cmp-fin-q4', GROUPS.IT_Admins, 'Keep', mlee, t(3), 'File server administrators.');
  for (const it of items.filter((i) => i.campaignId === 'cmp-hr-q3')) {
    it.decision = 'Keep';
    it.decidedBy = userSid('rdelgado');
    it.decidedAt = t(60);
    it.history.push({ decision: 'Keep', by: it.decidedBy, at: it.decidedAt });
  }
  items.push({
    id: 'hist-1', campaignId: 'cmp-hr-q3', shareId: 'sh-hr', scope: '', trusteeSid: GROUPS.Sales_Team, rights: 'Read', via: 'share+ntfs',
    memberCount: 3, members: [userSid('cdonovan'), userSid('lfitch'), userSid('byoder')], flags: [], decision: 'Remove', decidedBy: userSid('rdelgado'),
    decidedAt: t(62), justification: 'Sales has no need for HR records.', history: [{ decision: 'Remove', by: userSid('rdelgado'), at: t(62), justification: 'Sales has no need for HR records.' }],
  });
  const { events, alerts } = buildActivity(shares, files);
  return {
    dir,
    servers: SERVERS,
    shares,
    files,
    owners: buildOwners(),
    campaigns,
    items,
    events,
    alerts,
    jobs: [],
    roleId: 'admin',
    toasts: [],
    appLog: [
      { at: t(5), actorSid: userSid('dwhitfield'), action: 'Launched access review', target: 'Finance access review, Q4 2026' },
      { at: t(24), actorSid: userSid('dwhitfield'), action: 'Launched access review', target: 'Legal access review, H2 2026' },
      { at: t(40), actorSid: userSid('dwhitfield'), action: 'Enrolled server', target: 'NW-FS02' },
    ],
    version: 0,
  };
}

// ---------- tiny external store ----------
let state: AppState = init();
const listeners = new Set<() => void>();
const emit = () => listeners.forEach((l) => l());
function set(fn: (s: AppState) => Partial<AppState>) {
  state = { ...state, ...fn(state), version: state.version + 1 };
  emit();
}
export const getState = () => state;
export function useApp(): AppState {
  return useSyncExternalStore(
    (l) => (listeners.add(l), () => listeners.delete(l)),
    () => state,
  );
}

const nowIso = () => new Date().toISOString();
const actor = () => ROLES.find((r) => r.id === state.roleId)!.sid;
const log = (action: string, target: string): AppAuditEntry => ({ at: nowIso(), actorSid: actor(), action, target });
let toastId = 1;

export const actions = {
  toast(text: string, tone: Toast['tone'] = 'ok') {
    const id = toastId++;
    set((s) => ({ toasts: [...s.toasts, { id, text, tone }] }));
    setTimeout(() => set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) })), 4200);
  },

  setRole(roleId: string) {
    set(() => ({ roleId }));
  },

  overrideClassification(fileId: string, classification: Classification, reason: string) {
    const f = state.files.find((x) => x.id === fileId)!;
    set((s) => ({
      files: s.files.map((x) => (x.id === fileId ? { ...x, override: { classification, reason, by: actor(), at: nowIso() } } : x)),
      appLog: [log(`Set classification to ${classification}`, f.name), ...s.appLog],
    }));
    actions.toast(`${f.name} is now ${classification}. The original policy result is kept in history.`);
  },

  clearOverride(fileId: string) {
    set((s) => ({ files: s.files.map((x) => (x.id === fileId ? { ...x, override: undefined } : x)) }));
  },

  setOwner(shareId: string, patch: Partial<OwnerAssignment>) {
    const share = state.shares.find((s) => s.id === shareId)!;
    set((s) => ({
      owners: s.owners.map((o) => (o.shareId === shareId ? { ...o, ...patch, confirmed: true } : o)),
      appLog: [log('Confirmed owner', share.name), ...s.appLog],
    }));
    actions.toast(`Owner confirmed for ${share.name}.`);
  },

  createCampaign(input: { name: string; shareIds: string[]; reviewerSid: string; backupReviewerSid?: string; dueDays: number }) {
    const id = `cmp-${Date.now().toString(36)}`;
    const now = new Date();
    const campaign: ReviewCampaign = {
      id,
      name: input.name,
      shareIds: input.shareIds,
      reviewerSid: input.reviewerSid,
      backupReviewerSid: input.backupReviewerSid,
      createdBy: actor(),
      createdAt: now.toISOString(),
      snapshotAt: now.toISOString(),
      dueAt: new Date(now.getTime() + input.dueDays * 864e5).toISOString(),
      status: 'Active',
      revision: 1,
      emailSentAt: now.toISOString(),
    };
    const items = snapshotItems(state.dir, campaign, state.shares);
    set((s) => ({ campaigns: [campaign, ...s.campaigns], items: [...s.items, ...items], appLog: [log('Launched access review', input.name), ...s.appLog] }));
    const reviewer = state.dir.get(input.reviewerSid)!;
    actions.toast(`Review launched. ${reviewer.displayName} gets a sign-in link at ${reviewer.email}.`);
    return id;
  },

  decide(itemIds: string[], decision: ReviewDecision, justification?: string) {
    const by = actor();
    const at = nowIso();
    const items = state.items.map((it) => {
      if (!itemIds.includes(it.id)) return it;
      return { ...it, decision, justification, decidedBy: by, decidedAt: at, history: [...it.history, { decision, by, at, justification }] };
    });
    const campaigns = state.campaigns.map((c) => {
      const mine = items.filter((i) => i.campaignId === c.id);
      return mine.length && mine.every((i) => i.decision !== 'Pending') ? { ...c, status: 'Completed' as const } : c;
    });
    set((s) => ({ items, campaigns, appLog: [log(`Recorded ${decision} on ${itemIds.length} item(s)`, 'Access review'), ...s.appLog] }));
    actions.toast(`Saved ${itemIds.length} decision${itemIds.length > 1 ? 's' : ''}. Nothing changes in AD until IT acts.`);
  },

  startScan(shareId: string) {
    if (state.jobs.some((j) => j.shareId === shareId)) return;
    const total = state.files.filter((f) => f.shareId === shareId).length;
    set((s) => ({
      jobs: [...s.jobs, { shareId, startedAt: Date.now(), total, done: 0, state: 'running' }],
      shares: s.shares.map((sh) => (sh.id === shareId ? { ...sh, scan: { ...sh.scan, state: 'running', progress: 0 } } : sh)),
    }));
    tickScans();
  },

  pauseScan(shareId: string) {
    set((s) => ({
      jobs: s.jobs.map((j) => (j.shareId === shareId ? { ...j, state: j.state === 'running' ? 'paused' : 'running' } : j)),
      shares: s.shares.map((sh) => (sh.id === shareId ? { ...sh, scan: { ...sh.scan, state: sh.scan.state === 'running' ? 'paused' : 'running' } } : sh)),
    }));
    tickScans();
  },

  enrollServer(input: { hostname: string; shareNames: string[] }) {
    const id = `srv-${input.hostname.toLowerCase()}`;
    const server: ManagedServer = {
      id, hostname: input.hostname.toUpperCase(), fqdn: `${input.hostname.toLowerCase()}.northwind.local`, os: 'Windows Server 2022 Standard',
      state: 'enrolled', lastSeen: nowIso(), ip: '10.20.1.23',
    };
    const newShares: Share[] = input.shareNames.map((n) => ({
      id: `sh-${n.toLowerCase().replace(/\W+/g, '-')}`,
      serverId: id,
      name: n,
      unc: `\\\\${server.hostname}\\${n}`,
      description: 'Newly enrolled',
      shareAcl: [{ sid: WELL_KNOWN.everyone, type: 'allow', rights: 'FullControl', inherited: false, mask: '0x1F01FF' }],
      ntfsAcl: [
        { sid: GROUPS.AllStaff, type: 'allow', rights: 'Modify', inherited: false, mask: '0x1301BF' },
        { sid: GROUPS.IT_Admins, type: 'allow', rights: 'FullControl', inherited: false, mask: '0x1F01FF' },
      ],
      folders: [],
      scan: { state: 'queued', progress: 0 },
    }));
    const newFiles: FileItem[] = newShares.flatMap((sh, si) =>
      Array.from({ length: 36 }, (_, i) => ({
        id: `n${si}-${i}`,
        shareId: sh.id,
        path: i === 3 && si === 0 ? 'Campaigns/contest-entrants.csv' : `Campaigns/asset-${String(i).padStart(3, '0')}.${['docx', 'xlsx', 'pdf', 'png'][i % 4]}`,
        name: i === 3 && si === 0 ? 'contest-entrants.csv' : `asset-${String(i).padStart(3, '0')}.${['docx', 'xlsx', 'pdf', 'png'][i % 4]}`,
        ext: i === 3 && si === 0 ? 'csv' : ['docx', 'xlsx', 'pdf', 'png'][i % 4],
        size: (20 + i * 13) * 1024,
        modified: new Date(NOW.getTime() - i * 3 * 864e5).toISOString(),
        created: new Date(NOW.getTime() - i * 5 * 864e5).toISOString(),
        ownerSid: userSid('lfitch'),
        scanStatus: (i % 4 === 3 && !(i === 3 && si === 0) ? 'unsupported' : 'scanned') as FileItem['scanStatus'],
        findings: [],
        policyClassification: 'Unknown' as Classification,
        content: i === 3 && si === 0 ? 'Entrant,Card on file,Exp\nNora Pike,4111 1111 1111 1111,04/28\nEli Gray,5555 5555 5555 4444,09/27' : undefined,
      })),
    );
    set((s) => ({
      servers: [...s.servers, server],
      shares: [...s.shares, ...newShares],
      files: [...s.files, ...newFiles],
      owners: [...s.owners, ...newShares.map((sh) => ({ shareId: sh.id, cadence: 'Semiannual' as const, confirmed: false }))],
      appLog: [log('Enrolled server', server.hostname), ...s.appLog],
    }));
    newShares.forEach((sh) => actions.startScan(sh.id));
    return newShares.map((s) => s.id);
  },

  ackAlert(alertId: string) {
    set((s) => ({ alerts: s.alerts.map((a) => (a.id === alertId ? { ...a, state: 'Acknowledged' } : a)) }));
  },

  logExport(report: string) {
    set((s) => ({ appLog: [log('Exported report', report), ...s.appLog] }));
  },
};

let ticking = false;
function tickScans() {
  if (ticking) return;
  ticking = true;
  const step = () => {
    const running = state.jobs.filter((j) => j.state === 'running');
    if (!running.length) {
      ticking = false;
      return;
    }
    const finished: string[] = [];
    const jobs = state.jobs.map((j) => {
      if (j.state !== 'running') return j;
      const done = Math.min(j.total, j.done + Math.max(3, Math.round(j.total / 28)));
      if (done >= j.total) finished.push(j.shareId);
      return { ...j, done };
    });
    set((s) => ({
      jobs: jobs.filter((j) => !finished.includes(j.shareId)),
      shares: s.shares.map((sh) => {
        const j = jobs.find((x) => x.shareId === sh.id);
        if (!j) return sh;
        return finished.includes(sh.id)
          ? { ...sh, scan: { state: 'complete', progress: 1, lastScan: nowIso() } }
          : { ...sh, scan: { ...sh.scan, progress: j.done / j.total } };
      }),
      files: finished.length ? s.files.map((f) => (finished.includes(f.shareId) ? scanFile(f, s.shares.find((x) => x.id === f.shareId)!) : f)) : s.files,
    }));
    if (finished.length) finished.forEach((id) => actions.toast(`Scan finished on ${state.shares.find((s) => s.id === id)!.name}.`));
    setTimeout(step, 160);
  };
  setTimeout(step, 160);
}
