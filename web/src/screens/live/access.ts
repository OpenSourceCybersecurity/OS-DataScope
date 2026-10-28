// Pure helpers for the live Who has access screen, kept apart from the components so they can be unit tested.

import type { AccessRange, Certainty, DirectoryStatus, ExposureStatus, RightsLabel, TrusteeKind } from '../../api/client';

export const RIGHTS_ORDER: RightsLabel[] = ['No access', 'Read', 'Write', 'Modify', 'Full control'];
export const rank = (r: RightsLabel) => RIGHTS_ORDER.indexOf(r);

type Range = Pick<AccessRange, 'rights' | 'upTo' | 'certainty'>;

/**
 * One phrase for an access range. A range is only shown as a single value when Data Scope is sure of it; otherwise
 * it says what is certain and how far it could go, so a reader never mistakes a guess for a fact.
 */
export function rangeText(r: Range): string {
  if (r.certainty === 'Unknown') return 'Unknown';
  if (r.rights === r.upTo) return r.rights;
  if (r.rights === 'No access') return `Possibly ${r.upTo === 'Full control' ? 'full control' : r.upTo.toLowerCase()}`;
  return `${r.rights}, possibly ${r.upTo === 'Full control' ? 'full control' : r.upTo.toLowerCase()}`;
}

export type Tone = 'bad' | 'warn' | 'good' | 'neutral';

/** How alarming "any domain user" access is: certain access is bad, possible access a warning. */
export function openTone(r: Range): Tone {
  if (r.certainty === 'Unknown') return 'neutral';
  if (rank(r.rights) >= rank('Read')) return 'bad';
  if (rank(r.upTo) >= rank('Read')) return 'warn';
  return 'good';
}

export function openText(r: Range): string {
  switch (openTone(r)) {
    case 'bad':
      return `Anyone in the domain can ${r.rights === 'Read' ? 'read' : `${r.rights.toLowerCase()}`}`;
    case 'warn':
      return 'Anyone in the domain might be able to read';
    case 'good':
      return 'Not open to everyone';
    default:
      return 'Unknown: permissions not read';
  }
}

export const EXPOSURE: Record<ExposureStatus, { label: string; tone: Tone; help: string }> = {
  Open: { label: 'Open to everyone', tone: 'bad', help: 'Any ordinary domain user can open these files, through Everyone, Authenticated Users, Domain Users or a similar group.' },
  PossiblyOpen: {
    label: 'Possibly open',
    tone: 'warn',
    help: 'Data Scope could not rule it out: the share permissions, a group, or the server’s local groups could not be read. Fix the gap to get a firm answer.',
  },
  Unknown: { label: 'Unknown', tone: 'neutral', help: 'The scan account could not read this folder’s permissions, so nobody can say who has access.' },
};

export const CERTAINTY: Record<Certainty, { label: string; help: string }> = {
  Likely: { label: 'Likely', help: 'Computed from complete data the way Windows evaluates it. Not a live Windows access check.' },
  Partial: { label: 'Partial', help: 'Something needed was missing, so the true answer lies in the range shown.' },
  Unknown: { label: 'Unknown', help: 'The permissions themselves could not be read.' },
};

export const TRUSTEE_KIND: Record<TrusteeKind, string> = {
  User: 'User',
  Group: 'Group',
  Computer: 'Computer',
  ForeignSecurityPrincipal: 'Other domain',
  LocalGroup: 'Local group on the server',
  WellKnown: 'Built-in',
  Deleted: 'Deleted account',
  Unknown: 'Not in this domain',
};

/** Whether the directory data behind every answer is present and fresh. Older than two days is flagged. */
export function directoryFreshness(d: DirectoryStatus | undefined, now = Date.now()): { tone: Tone; text: string } {
  if (!d) return { tone: 'neutral', text: 'Checking the directory…' };
  if (!d.latest) {
    if (d.pending) return { tone: 'warn', text: 'The first directory sync is running. Group-based access appears when it finishes.' };
    if (d.lastFailure) return { tone: 'bad', text: `The directory sync failed: ${d.lastFailure.lastError ?? 'unknown error'}` };
    return { tone: 'warn', text: 'Active Directory has not been synced yet, so access through groups cannot be shown.' };
  }
  const age = now - new Date(d.latest.completedAt ?? d.latest.requestedAt).getTime();
  if (d.lastFailure) return { tone: 'warn', text: `The last directory sync failed (${d.lastFailure.lastError ?? 'unknown error'}). Showing the previous one.` };
  if (age > 2 * 24 * 3600 * 1000) return { tone: 'warn', text: 'The directory data is more than two days old. Group changes since then are not reflected.' };
  return { tone: 'good', text: `Directory synced from ${d.latest.domain ?? 'the domain'}: ${d.latest.users.toLocaleString('en-US')} users, ${d.latest.groups.toLocaleString('en-US')} groups.` };
}

/** Deep links into the screen: "folder:<id>" opens a folder, "user:<sid>" a person, "group:<sid>" a group. */
export function parseAccessParam(param?: string): { folderId?: string; sid?: string; kind?: 'User' | 'Group' } {
  if (!param) return {};
  if (param.startsWith('folder:')) return { folderId: param.slice(7) };
  if (param.startsWith('user:')) return { sid: param.slice(5), kind: 'User' };
  if (param.startsWith('group:')) return { sid: param.slice(6), kind: 'Group' };
  return {};
}
