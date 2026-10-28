// Explainable risk rules. Each risk says what we found, why it matters, the
// factors behind its severity, and what IT should do. No opaque scores.

import type { Classification, FileItem, OwnerAssignment, Principal, Severity, Share } from './types';
import { CLASSIFICATIONS } from './types';
import { WELL_KNOWN, findCycles, folderAcl, isBroadSid, users, whoHasAccess, type Directory } from './access';

export interface Risk {
  id: string;
  rule: string;
  severity: Severity;
  title: string;
  shareId: string;
  scope: string; // folder path or '' for the share root
  why: string;
  factors: { sensitivity: string; exposure: string; confidence: string };
  fix: string;
  affectedFiles: string[];
  principals: string[];
}

export const SEVERITY_ORDER: Severity[] = ['Critical', 'High', 'Medium', 'Low'];

export const effectiveClass = (f: FileItem): Classification => f.override?.classification ?? f.policyClassification;
export const classRank = (c: Classification) => CLASSIFICATIONS.indexOf(c);

export interface RiskContext {
  dir: Directory;
  shares: Share[];
  files: FileItem[];
  owners: OwnerAssignment[];
}

/** Which scope (broken-inheritance folder or root) governs a file's NTFS ACL. */
export function scopeOf(share: Share, path: string): string {
  return folderAcl(share, path.split('/').slice(0, -1).join('/')).folder?.path ?? '';
}

export function computeRisks({ dir, shares, files, owners }: RiskContext): Risk[] {
  const risks: Risk[] = [];
  const enabledUsers = users(dir).filter((u) => u.enabled).length;
  const name = (sid: string) => dir.get(sid)?.displayName ?? sid;

  for (const share of shares) {
    const shareFiles = files.filter((f) => f.shareId === share.id);
    const scopes = ['', ...share.folders.filter((f) => f.inheritanceBroken).map((f) => f.path)];

    // 1. Sensitive data open to most of the company
    for (const scope of scopes) {
      const scoped = shareFiles.filter((f) => scopeOf(share, f.path) === scope);
      const restricted = scoped.filter((f) => effectiveClass(f) === 'Restricted');
      const confidential = scoped.filter((f) => effectiveClass(f) === 'Confidential');
      if (!restricted.length && !confidential.length) continue;
      const access = whoHasAccess(dir, share, scope || undefined);
      const enabledWithAccess = access.filter((a) => dir.get(a.userSid)?.enabled).length;
      const ratio = enabledWithAccess / enabledUsers;
      const acl = folderAcl(share, scope || undefined).acl;
      const broad = acl.filter((a) => a.type === 'allow' && (isBroadSid(a.sid) || a.sid === groupSidBySam(dir, 'AllStaff')));
      if (ratio < 0.5 || !broad.length) continue;
      const sev: Severity = restricted.length ? 'Critical' : 'High';
      const where = scope ? `${share.unc}\\${scope.replace(/\//g, '\\')}` : share.unc;
      risks.push({
        id: `R-open-${share.id}-${scope || 'root'}`,
        rule: 'Sensitive data open to most of the company',
        severity: sev,
        title: `${plural(restricted.length || confidential.length, `${restricted.length ? 'restricted' : 'confidential'} file`)} readable by ${enabledWithAccess} of ${enabledUsers} people`,
        shareId: share.id,
        scope,
        why: `${where} grants access to ${broad.map((b) => name(b.sid)).join(', ')}. Combined with the share permission, ${Math.round(ratio * 100)}% of active accounts can open files that contain possible ${topCategories(restricted.length ? restricted : confidential)}.`,
        factors: {
          sensitivity: `${restricted.length} Restricted, ${confidential.length} Confidential`,
          exposure: `${enabledWithAccess} of ${enabledUsers} active accounts (${Math.round(ratio * 100)}%)`,
          confidence: 'Computed from share + NTFS snapshot; not a Windows access check',
        },
        fix: `Replace ${broad.map((b) => name(b.sid)).join(' and ')} on ${scope ? 'this folder' : 'this share'} with the department group that needs it, or move the sensitive files to a restricted folder.`,
        affectedFiles: (restricted.length ? restricted : confidential).map((f) => f.id),
        principals: broad.map((b) => b.sid),
      });
    }

    const sensitive = shareFiles.filter((f) => classRank(effectiveClass(f)) >= classRank('Confidential'));

    // 2. No confirmed owner
    const owner = owners.find((o) => o.shareId === share.id);
    if (!owner?.confirmed && sensitive.length) {
      risks.push({
        id: `R-owner-${share.id}`,
        rule: 'Sensitive share has no owner',
        severity: 'High',
        title: `${share.name} has ${plural(sensitive.length, 'sensitive file')} and nobody owns it`,
        shareId: share.id,
        scope: '',
        why: 'Without an owner, nobody is accountable for who has access, and the share cannot be included in access reviews.',
        factors: { sensitivity: `${sensitive.length} Confidential or Restricted files`, exposure: 'Excluded from access reviews', confidence: 'Owner registry' },
        fix: owner?.suggestedOwnerSid ? `Confirm ${name(owner.suggestedOwnerSid)} as owner (suggested) or pick someone else.` : 'Assign a department manager as owner.',
        affectedFiles: sensitive.map((f) => f.id),
        principals: [],
      });
    }

    const allAces = [...share.ntfsAcl, ...share.folders.flatMap((f) => f.ntfsAcl ?? [])];

    // 3. Disabled accounts that still hold access
    // Broad grants (Everyone) reach disabled accounts by definition; only flag specific paths.
    const disabled = whoHasAccess(dir, share).filter(
      (a) => dir.get(a.userSid)?.enabled === false && a.grants.some((g) => g.acl === 'ntfs' && !isBroadSid(g.ace.sid)),
    );
    if (disabled.length) {
      risks.push({
        id: `R-disabled-${share.id}`,
        rule: 'Disabled accounts still have access',
        severity: sensitive.length ? 'Medium' : 'Low',
        title: `${disabled.length} disabled account${disabled.length > 1 ? 's' : ''} still ${disabled.length > 1 ? 'have' : 'has'} access to ${share.name}`,
        shareId: share.id,
        scope: '',
        why: `${disabled.map((d) => name(d.userSid)).join(', ')} cannot sign in today, but access comes back the moment an account is re-enabled or its password is reset.`,
        factors: { sensitivity: `${sensitive.length} sensitive files on share`, exposure: `${disabled.length} dormant identities`, confidence: 'AD account status from last directory sync' },
        fix: 'Remove the accounts from the groups or ACL entries that grant access, then delete or archive them per your offboarding process.',
        affectedFiles: [],
        principals: disabled.map((d) => d.userSid),
      });
    }

    // 4. Unresolved SIDs on ACLs
    const orphans = [...new Set(allAces.filter((a) => !dir.has(a.sid)).map((a) => a.sid))];
    if (orphans.length) {
      risks.push({
        id: `R-orphan-${share.id}`,
        rule: 'Unknown account on permissions',
        severity: 'Medium',
        title: `${orphans.length} unresolved SID${orphans.length > 1 ? 's' : ''} on ${share.name} permissions`,
        shareId: share.id,
        scope: '',
        why: 'The account was likely deleted from AD. If the SID belongs to another domain or a restored account, access may still work. Data Scope cannot tell which.',
        factors: { sensitivity: `${sensitive.length} sensitive files on share`, exposure: 'Unknown', confidence: 'Partial: SID did not resolve in NORTHWIND' },
        fix: 'Confirm the SID is not from a trusted domain, then remove the entry.',
        affectedFiles: [],
        principals: orphans,
      });
    }

    // 5. Direct user entries
    const direct = [...new Set(allAces.filter((a) => dir.get(a.sid)?.type === 'user').map((a) => a.sid))];
    if (direct.length) {
      risks.push({
        id: `R-direct-${share.id}`,
        rule: 'Permissions granted to individuals',
        severity: 'Low',
        title: `${direct.length} person${direct.length > 1 ? 's' : ''} granted access directly on ${share.name}`,
        shareId: share.id,
        scope: '',
        why: `${direct.map(name).join(', ')} ${direct.length > 1 ? 'are' : 'is'} on the ACL by name. Direct entries survive job changes and are easy to miss in group reviews.`,
        factors: { sensitivity: `${sensitive.length} sensitive files on share`, exposure: `${direct.length} direct entries`, confidence: 'ACL snapshot' },
        fix: 'Grant access through a group instead and remove the direct entry.',
        affectedFiles: [],
        principals: direct,
      });
    }

    // 6. Write access through an unrelated nested group
    if (sensitive.some((f) => effectiveClass(f) === 'Restricted')) {
      const nested = new Map<string, Set<string>>();
      for (const a of whoHasAccess(dir, share)) {
        for (const g of a.grants) {
          if (g.acl !== 'ntfs' || g.ace.rights === 'Read' || g.chain.length < 3) continue;
          if (g.ace.sid === WELL_KNOWN.builtinAdmins || g.ace.sid === WELL_KNOWN.system) continue; // admin paths are expected
          const via = g.chain[g.chain.length - 2];
          if (dir.get(via)?.type !== 'group') continue;
          if (!nested.has(via)) nested.set(via, new Set());
          nested.get(via)!.add(a.userSid);
        }
      }
      for (const [via, people] of nested) {
        const target = allAces.find((a) => dir.get(via)?.memberOf.includes(a.sid));
        risks.push({
          id: `R-nested-${share.id}-${via}`,
          rule: 'Write access through a nested group',
          severity: 'Medium',
          title: `${name(via)} can modify ${share.name} through nesting`,
          shareId: share.id,
          scope: '',
          why: `${name(via)} is a member of ${target ? name(target.sid) : 'a group'} that has ${target?.rights ?? 'Modify'} on ${share.name}. That gives ${people.size} ${people.size === 1 ? 'person' : 'people'} write access to restricted files, which is easy to miss when looking at the share alone.`,
          factors: { sensitivity: 'Restricted files on share', exposure: `${people.size} people via nesting`, confidence: 'Group membership snapshot' },
          fix: `Remove ${name(via)} from ${target ? name(target.sid) : 'the parent group'} if it was not intentional.`,
          affectedFiles: [],
          principals: [via, ...people],
        });
      }
    }

    // 7. Coverage blind spots
    const unscanned = shareFiles.filter((f) => f.scanStatus !== 'scanned');
    if (unscanned.length / Math.max(1, shareFiles.length) > 0.25) {
      risks.push({
        id: `R-coverage-${share.id}`,
        rule: 'Low scan coverage',
        severity: 'Medium',
        title: `${Math.round((unscanned.length / shareFiles.length) * 100)}% of ${share.name} could not be scanned`,
        shareId: share.id,
        scope: '',
        why: `${unscanned.length} files are unsupported, encrypted, corrupt or access-denied. They are not clean; Data Scope simply could not read them.`,
        factors: { sensitivity: 'Unknown', exposure: describeExposure(dir, share, enabledUsers), confidence: 'Coverage gap' },
        fix: "Check each file's note for why it was not read, grant the scanner read access to denied folders, or exclude the share if intended.",
        affectedFiles: unscanned.map((f) => f.id),
        principals: [],
      });
    }
  }

  // 8. Credentials stored in files, wherever they are
  const creds = files.filter((f) => f.findings.some((x) => x.category === 'Credentials' && x.confidence !== 'low'));
  for (const share of shares) {
    const here = creds.filter((f) => f.shareId === share.id);
    if (!here.length) continue;
    risks.push({
      id: `R-creds-${share.id}`,
      rule: 'Passwords or keys stored in files',
      severity: 'High',
      title: `${here.length} file${here.length > 1 ? 's' : ''} on ${share.name} contain${here.length > 1 ? '' : 's'} passwords or private keys`,
      shareId: share.id,
      scope: '',
      why: `Anyone who can read these files can reuse the secrets. ${describeExposure(dir, share, enabledUsers)}.`,
      factors: { sensitivity: 'Credentials', exposure: describeExposure(dir, share, enabledUsers), confidence: 'Pattern match with redacted evidence' },
      fix: 'Rotate the exposed secrets first, then move them to a password vault and delete the files.',
      affectedFiles: here.map((f) => f.id),
      principals: [],
    });
  }

  // 9. Group nesting loops
  for (const loop of findCycles(dir)) {
    risks.push({
      id: `R-cycle-${loop[0]}`,
      rule: 'Group nesting loop',
      severity: 'Low',
      title: `${loop.slice(0, -1).map(name).join(' ↔ ')} are nested in each other`,
      shareId: '',
      scope: '',
      why: 'Circular nesting makes access hard to reason about and breaks some tools. Data Scope stops at the loop, so results through these groups are marked partial.',
      factors: { sensitivity: 'n/a', exposure: 'Membership ambiguity', confidence: 'Directory snapshot' },
      fix: 'Remove one direction of the nesting in Active Directory Users and Computers.',
      affectedFiles: [],
      principals: loop.slice(0, -1),
    });
  }

  return risks.sort((a, b) => SEVERITY_ORDER.indexOf(a.severity) - SEVERITY_ORDER.indexOf(b.severity));
}

function describeExposure(dir: Directory, share: Share, enabledUsers: number): string {
  const n = whoHasAccess(dir, share).filter((a) => dir.get(a.userSid)?.enabled).length;
  return `${n} of ${enabledUsers} active accounts can read ${share.name}`;
}

function groupSidBySam(dir: Directory, sam: string): string | undefined {
  for (const p of dir.values() as IterableIterator<Principal>) if (p.sam === sam) return p.sid;
  return undefined;
}

function topCategories(files: FileItem[]): string {
  const counts = new Map<string, number>();
  for (const f of files) for (const x of f.findings) counts.set(x.category, (counts.get(x.category) ?? 0) + 1);
  const top = [...counts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 3).map(([c]) => c);
  return top.length ? top.join(', ').replace(/, ([^,]*)$/, ' and $1') : 'sensitive data';
}

const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;
