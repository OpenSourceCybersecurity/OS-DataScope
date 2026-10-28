// Effective-access engine: resolves nested AD groups and evaluates share and
// NTFS ACLs the way Windows does (canonical ACE order, deny/allow bits,
// most-restrictive of share vs NTFS). Results carry an explanation and a
// confidence so the UI never overstates certainty.

import type { Ace, Folder, Principal, Rights, Share } from './types';
import { RIGHTS_ORDER } from './types';

export const WELL_KNOWN = {
  everyone: 'S-1-1-0',
  authenticatedUsers: 'S-1-5-11',
  system: 'S-1-5-18',
  creatorOwner: 'S-1-3-0',
  builtinAdmins: 'S-1-5-32-544',
  builtinUsers: 'S-1-5-32-545',
};

/** SIDs that grant access to (almost) every account in the domain. */
export function isBroadSid(sid: string): boolean {
  return (
    sid === WELL_KNOWN.everyone ||
    sid === WELL_KNOWN.authenticatedUsers ||
    sid === WELL_KNOWN.builtinUsers ||
    sid.endsWith('-513') // Domain Users
  );
}

export type Directory = Map<string, Principal>;

export interface Membership {
  /** group SID -> chain from the user to that group, inclusive. */
  chains: Map<string, string[]>;
  unresolved: string[];
  cycles: string[][];
}

/** Breadth-first walk of group membership. Cycles and unknown SIDs are recorded, never looped on. */
export function resolveMembership(dir: Directory, userSid: string): Membership {
  const chains = new Map<string, string[]>();
  const unresolved = new Set<string>();
  const cycles: string[][] = [];
  const user = dir.get(userSid);
  if (!user) return { chains, unresolved: [userSid], cycles };

  chains.set(userSid, [userSid]);
  const queue: string[] = [userSid];
  while (queue.length) {
    const current = queue.shift()!;
    const chain = chains.get(current)!;
    const p = dir.get(current);
    if (!p) {
      unresolved.add(current);
      continue;
    }
    for (const parent of p.memberOf) {
      if (chain.includes(parent)) {
        cycles.push([...chain.slice(chain.indexOf(parent)), parent]);
        continue;
      }
      if (chains.has(parent)) continue; // already reached by a shorter path
      chains.set(parent, [...chain, parent]);
      if (!dir.has(parent)) unresolved.add(parent);
      else queue.push(parent);
    }
  }
  // Implicit well-known memberships.
  if (user.type === 'user') {
    chains.set(WELL_KNOWN.everyone, [userSid, WELL_KNOWN.everyone]);
    if (user.enabled) chains.set(WELL_KNOWN.authenticatedUsers, [userSid, WELL_KNOWN.authenticatedUsers]);
  }
  return { chains, unresolved: [...unresolved], cycles };
}

// Rights as bits: R=1, W=2, P=4 (change permissions / take ownership)
const ALLOW_BITS: Record<Rights, number> = { None: 0, Read: 1, Modify: 3, FullControl: 7 };
const DENY_BITS: Record<Rights, number> = { None: 0, Read: 1, Modify: 2, FullControl: 7 };

function bitsToRights(bits: number): Rights {
  if (!(bits & 1)) return 'None';
  if (bits & 4) return 'FullControl';
  if (bits & 2) return 'Modify';
  return 'Read';
}

export const minRights = (a: Rights, b: Rights): Rights =>
  RIGHTS_ORDER[Math.min(RIGHTS_ORDER.indexOf(a), RIGHTS_ORDER.indexOf(b))];
export const rightsAtLeast = (a: Rights, b: Rights) => RIGHTS_ORDER.indexOf(a) >= RIGHTS_ORDER.indexOf(b);

/** Explicit deny, explicit allow, inherited deny, inherited allow. */
function canonical(acl: Ace[]): Ace[] {
  const order = (a: Ace) => (a.inherited ? 2 : 0) + (a.type === 'allow' ? 1 : 0);
  return [...acl].sort((a, b) => order(a) - order(b));
}

export interface AclGrant {
  acl: 'share' | 'ntfs';
  ace: Ace;
  chain: string[];
}

export interface AclEvaluation {
  rights: Rights;
  grants: AclGrant[];
  denies: AclGrant[];
}

export function evaluateAcl(acl: Ace[], m: Membership, which: 'share' | 'ntfs'): AclEvaluation {
  let granted = 0;
  let denied = 0;
  const grants: AclGrant[] = [];
  const denies: AclGrant[] = [];
  for (const ace of canonical(acl)) {
    const chain = m.chains.get(ace.sid);
    if (!chain) continue;
    if (ace.type === 'deny') {
      const bits = DENY_BITS[ace.rights] & ~granted & ~denied;
      denied |= DENY_BITS[ace.rights] & ~granted;
      if (bits) denies.push({ acl: which, ace, chain });
    } else {
      const bits = ALLOW_BITS[ace.rights] & ~denied;
      granted |= bits;
      if (bits) grants.push({ acl: which, ace, chain });
    }
  }
  return { rights: bitsToRights(granted), grants, denies };
}

export type AccessConfidence = 'likely' | 'partial';

export interface EffectiveAccess {
  userSid: string;
  rights: Rights;
  shareRights: Rights;
  ntfsRights: Rights;
  grants: AclGrant[];
  denies: AclGrant[];
  confidence: AccessConfidence;
  notes: string[];
}

export function folderAcl(share: Share, folderPath?: string): { acl: Ace[]; folder?: Folder } {
  if (!folderPath) return { acl: share.ntfsAcl };
  // Nearest folder with broken inheritance wins; otherwise the share root ACL is inherited.
  const candidates = share.folders
    .filter((f) => f.inheritanceBroken && (folderPath === f.path || folderPath.startsWith(f.path + '/')))
    .sort((a, b) => b.path.length - a.path.length);
  const folder = candidates[0];
  return folder?.ntfsAcl ? { acl: folder.ntfsAcl, folder } : { acl: share.ntfsAcl.map((a) => ({ ...a, inherited: true })) };
}

export function effectiveAccess(dir: Directory, share: Share, userSid: string, folderPath?: string): EffectiveAccess {
  const m = resolveMembership(dir, userSid);
  const shareEval = evaluateAcl(share.shareAcl, m, 'share');
  const { acl } = folderAcl(share, folderPath);
  const ntfsEval = evaluateAcl(acl, m, 'ntfs');
  const rights = minRights(shareEval.rights, ntfsEval.rights);
  const notes: string[] = [];
  let confidence: AccessConfidence = 'likely';

  if (m.unresolved.length) {
    confidence = 'partial';
    notes.push(`${m.unresolved.length} group(s) in this user's membership could not be resolved, so access may be higher than shown.`);
  }
  if (m.cycles.length) {
    confidence = 'partial';
    notes.push('Group nesting contains a loop. Data Scope stopped at the loop instead of following it.');
  }
  if (rights !== ntfsEval.rights && ntfsEval.rights !== 'None') {
    notes.push(`NTFS allows ${label(ntfsEval.rights)}, but the share permission limits this to ${label(shareEval.rights)}.`);
  }
  if (rights !== shareEval.rights && shareEval.rights !== 'None' && ntfsEval.rights !== 'None') {
    notes.push(`The share allows ${label(shareEval.rights)}, but NTFS limits this to ${label(ntfsEval.rights)}.`);
  }
  if (shareEval.denies.length || ntfsEval.denies.length) {
    notes.push('A deny entry removes some rights for this user.');
  }
  const user = dir.get(userSid);
  if (user && !user.enabled) {
    notes.push('Account is disabled. It cannot sign in now, but this access returns if the account is re-enabled.');
  }
  return {
    userSid,
    rights,
    shareRights: shareEval.rights,
    ntfsRights: ntfsEval.rights,
    grants: [...shareEval.grants, ...ntfsEval.grants],
    denies: [...shareEval.denies, ...ntfsEval.denies],
    confidence,
    notes,
  };
}

export function label(r: Rights): string {
  return r === 'FullControl' ? 'Full control' : r === 'None' ? 'No access' : r;
}

export const users = (dir: Directory) => [...dir.values()].filter((p) => p.type === 'user');

export function whoHasAccess(dir: Directory, share: Share, folderPath?: string): EffectiveAccess[] {
  return users(dir)
    .map((u) => effectiveAccess(dir, share, u.sid, folderPath))
    .filter((e) => e.rights !== 'None')
    .sort((a, b) => RIGHTS_ORDER.indexOf(b.rights) - RIGHTS_ORDER.indexOf(a.rights));
}

/** Users who are effective members of a trustee (group/user/well-known). */
export function membersOf(dir: Directory, trusteeSid: string): string[] {
  return users(dir)
    .filter((u) => resolveMembership(dir, u.sid).chains.has(trusteeSid))
    .map((u) => u.sid);
}

export interface RemovalImpact {
  loseAccess: string[];
  keepAccess: { sid: string; via: string[] }[];
}

/** What happens if a trustee's entries are removed from a share (both ACLs). */
export function simulateRemoval(dir: Directory, share: Share, trusteeSid: string): RemovalImpact {
  const without: Share = {
    ...share,
    shareAcl: share.shareAcl.filter((a) => a.sid !== trusteeSid),
    ntfsAcl: share.ntfsAcl.filter((a) => a.sid !== trusteeSid),
  };
  const loseAccess: string[] = [];
  const keepAccess: { sid: string; via: string[] }[] = [];
  for (const sid of membersOf(dir, trusteeSid)) {
    const before = effectiveAccess(dir, share, sid);
    if (before.rights === 'None') continue;
    const after = effectiveAccess(dir, without, sid);
    if (after.rights === 'None') loseAccess.push(sid);
    else
      keepAccess.push({
        sid,
        via: [...new Set(after.grants.filter((g) => g.acl === 'ntfs').map((g) => g.ace.sid))],
      });
  }
  return { loseAccess, keepAccess };
}

/** All group nesting loops in the directory, deduplicated. */
export function findCycles(dir: Directory): string[][] {
  const seen = new Set<string>();
  const out: string[][] = [];
  for (const p of dir.values()) {
    if (p.type !== 'group') continue;
    const stack: string[] = [];
    const visit = (sid: string) => {
      if (stack.includes(sid)) {
        const loop = stack.slice(stack.indexOf(sid));
        const key = [...loop].sort().join('|');
        if (!seen.has(key)) {
          seen.add(key);
          out.push([...loop, sid]);
        }
        return;
      }
      const g = dir.get(sid);
      if (!g || stack.length > 32) return;
      stack.push(sid);
      g.memberOf.forEach(visit);
      stack.pop();
    };
    visit(p.sid);
  }
  return out;
}
