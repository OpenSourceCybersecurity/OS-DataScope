// Pure helpers for owners and access reviews, kept apart from the components so they can be unit tested.
// The server enforces every rule here; these only guide people before they press a button.

import type { DecisionKind, ReviewFlag, ReviewState, ReviewSummary } from '../../api/client';

export type Tone = 'bad' | 'warn' | 'good' | 'neutral' | 'accent';

export const DECISIONS: Record<DecisionKind, { label: string; help: string; tone: Tone; needsReason: boolean }> = {
  Keep: { label: 'Keep', help: 'The access is still needed.', tone: 'good', needsReason: false },
  Remove: { label: 'Remove', help: 'Ask IT to take this entry away. Nothing changes until IT acts.', tone: 'bad', needsReason: true },
  Investigate: { label: 'Investigate', help: "You're not sure. Ask IT or security to look into it.", tone: 'warn', needsReason: true },
  Exception: { label: 'Exception', help: 'It breaks a rule but is accepted for a stated reason until a date.', tone: 'accent', needsReason: true },
};

export const MIN_JUSTIFICATION = 10;
export const MAX_TEXT = 1000;
export const MAX_EXCEPTION_DAYS = 366;

const addDays = (iso: string, days: number) => {
  const d = new Date(iso + 'T00:00:00Z');
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
};

/** The same checks the server makes on one decision. Returns a message for people, or null when it is acceptable. */
export function decisionProblem(kind: DecisionKind, justification: string | undefined, expires: string | undefined, today: string): string | null {
  const text = (justification ?? '').trim();
  if (text.length > MAX_TEXT) return `Keep the justification under ${MAX_TEXT} characters.`;
  if (DECISIONS[kind].needsReason && text.length < MIN_JUSTIFICATION) {
    return kind === 'Remove'
      ? 'Say why this access should be removed (at least 10 characters).'
      : kind === 'Investigate'
        ? 'Say what should be looked into (at least 10 characters).'
        : 'Give the business reason for the exception (at least 10 characters). Auditors will read it.';
  }
  if (kind === 'Exception') {
    if (!expires) return 'Choose when the exception ends. It must be reviewed again by then.';
    if (expires <= today || expires > addDays(today, MAX_EXCEPTION_DAYS)) return 'The exception must end after today and within a year.';
  }
  return null;
}

/** Plain markers on review entries, written for a manager who is not an IT person. */
export const FLAGS: Record<ReviewFlag, { label: string; help: string; tone: Tone }> = {
  Broad: { label: 'Everyone', help: 'A built-in group that covers most or all accounts, such as Everyone or Authenticated Users.', tone: 'bad' },
  DirectUser: { label: 'Given directly', help: 'A single person was given access directly rather than through a group.', tone: 'warn' },
  DeletedAccount: { label: 'Deleted account', help: 'The account no longer exists. The entry is left over and usually safe to remove.', tone: 'warn' },
  OtherDomain: { label: 'Other domain', help: 'An account Data Scope could not look up, usually from another domain or a trust.', tone: 'warn' },
  DisabledAccounts: { label: 'Disabled accounts', help: 'Some of the people reached have disabled accounts.', tone: 'neutral' },
  UnresolvedMembers: { label: 'Members uncertain', help: 'Some group members could not be resolved, so they are shown as possible.', tone: 'warn' },
  CanChangePermissions: { label: 'Full control', help: 'Can change permissions and take ownership, not just edit files.', tone: 'bad' },
  Conditional: { label: 'Conditional', help: 'The entry has a condition Data Scope does not evaluate, so access is shown as possible.', tone: 'warn' },
  NoMembers: { label: 'No members', help: 'Nobody in the directory reaches this entry at the moment.', tone: 'neutral' },
  NoDecisionNeeded: { label: 'Managed by IT', help: "On the Review exclusions list in Settings, so owners are not asked about it and do not see it.", tone: 'neutral' },
};

export const REVIEW_STATE: Record<ReviewState, { label: string; tone: Tone }> = {
  Pending: { label: 'Not started', tone: 'neutral' },
  InProgress: { label: 'In progress', tone: 'accent' },
  Submitted: { label: 'Submitted', tone: 'good' },
};

export const ROLE_TEXT: Record<string, string> = {
  Owner: 'You own this',
  Delegate: 'Handed to you',
  Backup: 'Escalated to you as backup owner',
};

/** How far a review has got: decided out of required. A review with nothing to decide is complete. */
export function progress(r: Pick<ReviewSummary, 'decided' | 'decisionsRequired'>): number {
  return r.decisionsRequired === 0 ? 1 : Math.min(1, r.decided / r.decisionsRequired);
}

/** "Due in 3 days", "Due today", "2 days overdue". Dates are ISO yyyy-mm-dd. */
export function dueText(dueOn: string, today: string): string {
  const days = Math.round((Date.parse(dueOn + 'T00:00:00Z') - Date.parse(today + 'T00:00:00Z')) / 86_400_000);
  if (days === 0) return 'Due today';
  if (days === 1) return 'Due tomorrow';
  if (days > 1) return `Due in ${days} days`;
  return days === -1 ? '1 day overdue' : `${-days} days overdue`;
}

export const today = () => new Date().toISOString().slice(0, 10);

/** Splits a person's reviews into what needs doing and what is finished, most urgent first. */
export function splitReviews(reviews: ReviewSummary[]): { todo: ReviewSummary[]; done: ReviewSummary[] } {
  const open = (r: ReviewSummary) => r.state !== 'Submitted' && r.campaignState === 'Open';
  return {
    todo: reviews.filter(open).sort((a, b) => a.dueOn.localeCompare(b.dueOn) || a.resource.localeCompare(b.resource)),
    done: reviews.filter((r) => !open(r)).sort((a, b) => (b.submittedAt ?? b.dueOn).localeCompare(a.submittedAt ?? a.dueOn)),
  };
}
