import type { Risk, RiskDecisionKind, RiskRule, RiskRuleSummary } from '../../api/client';
import { cleanupParam } from './cleanup';

/** Where a risk can be looked into or fixed. Data Scope never changes permissions itself. */
export interface RiskLink {
  label: string;
  route: 'shares' | 'access' | 'discovery' | 'owners' | 'setup' | 'cleanup' | 'reviews';
  param?: string;
}

export function riskLinks(r: Risk): RiskLink[] {
  const links: RiskLink[] = [];
  const share = r.shareId ? { route: 'shares' as const, param: r.shareId } : undefined;
  switch (r.rule) {
    case 'no-owner':
    case 'owner-gone':
      if (share) links.push({ label: 'Assign an owner', ...share });
      break;
    case 'secrets':
      links.push({ label: 'Review the files', route: 'discovery' });
      break;
    case 'not-covered':
      if (share) links.push({ label: 'See what was missed', ...share });
      break;
    case 'nesting-loop':
      if (r.whoSid) links.push({ label: 'See the groups', route: 'access', param: `user:${r.whoSid}` });
      break;
    case 'stale-sensitive':
      links.push({ label: 'See the old sensitive files', route: 'cleanup', param: cleanupParam(r.shareId ?? undefined, true) });
      break;
    case 'review-overdue':
      links.push({ label: 'Start an access review', route: 'reviews' });
      break;
  }
  if (r.folderId && r.rule !== 'nesting-loop') links.push({ label: 'See who has access', route: 'access', param: `folder:${r.folderId}` });
  if (share && !links.some((l) => l.route === 'shares')) links.push({ label: 'Open the share', ...share });
  return links;
}

/** Risks grouped by rule, in the server's rule order, leaving out rules with nothing to show. */
export function groupByRule(items: Risk[], rules: RiskRuleSummary[]): { rule: RiskRuleSummary; items: Risk[] }[] {
  const byRule = new Map<RiskRule, Risk[]>();
  for (const r of items) byRule.set(r.rule, [...(byRule.get(r.rule) ?? []), r]);
  return rules.filter((rule) => byRule.has(rule.id)).map((rule) => ({ rule, items: byRule.get(rule.id)! }));
}

/** "3 restricted, 1 confidential files" and similar, for the line under a risk. */
export function riskDetail(r: Risk): string {
  const parts: string[] = [];
  if (r.restricted) parts.push(`${r.restricted} restricted`);
  if (r.confidential) parts.push(`${r.confidential} confidential`);
  const n = (r.restricted ?? 0) + (r.confidential ?? 0);
  const noun = n === 1 ? 'file' : 'files';
  const files = parts.length ? `${parts.join(', ')} ${noun}` : '';
  return [r.who && r.rule !== 'nesting-loop' ? `${r.who}${r.access ? `: ${r.access}` : ''}` : '', files].filter(Boolean).join(' · ');
}

/**
 * The names of the compliance standards a risk involves, from the standards the server lists, with the id as a fallback
 * for one the list doesn't have (for example when it couldn't be loaded).
 */
export function standardNames(r: Risk, standards: { id: string; name: string }[]): string[] {
  return r.standards.map((id) => standards.find((s) => s.id === id)?.name ?? id);
}

/** "Raised from Medium for PCI DSS", or undefined when the severity was not raised. */
export function raisedText(r: Risk, names: string[]): string | undefined {
  if (!r.raisedFrom) return undefined;
  const list = names.length <= 1 ? names.join('') : `${names.slice(0, -1).join(', ')} and ${names[names.length - 1]}`;
  return `Raised from ${r.raisedFrom} for ${list}`;
}

/** Most days an accept or a snooze can last; the server refuses longer. */
export const MAX_DAYS: Record<RiskDecisionKind, number> = { Accepted: 365, Snoozed: 90 };

/** Quick choices for how long a decision lasts, in days. */
export const DURATIONS: Record<RiskDecisionKind, { days: number; label: string }[]> = {
  Accepted: [
    { days: 90, label: '3 months' },
    { days: 182, label: '6 months' },
    { days: 365, label: '1 year' },
  ],
  Snoozed: [
    { days: 7, label: '1 week' },
    { days: 30, label: '30 days' },
    { days: 90, label: '90 days' },
  ],
};

/** Today's date in UTC as yyyy-mm-dd, which is how the server counts days. */
export const todayUtc = (now = new Date()) => now.toISOString().slice(0, 10);

/** A yyyy-mm-dd date some days after another. */
export function addDays(day: string, days: number): string {
  const d = new Date(`${day}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

/** A yyyy-mm-dd date as "Nov 9, 2026", without shifting it into the viewer's time zone. */
export const fmtDay = (day: string) =>
  new Date(`${day}T00:00:00Z`).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric', timeZone: 'UTC' });

/** Why the server would refuse this accept or snooze, or undefined when it is fine. Mirrors POST /risks/decisions. */
export function triageProblem(kind: RiskDecisionKind, note: string, until: string, today = todayUtc()): string | undefined {
  const text = note.trim();
  if (!/^\d{4}-\d{2}-\d{2}$/.test(until) || until <= today || until > addDays(today, MAX_DAYS[kind]))
    return kind === 'Accepted' ? 'Choose an end date after today and within a year.' : 'Choose an end date after today and within 90 days.';
  if (kind === 'Accepted' && text.length < 10) return 'Say why the risk is acceptable, in at least 10 characters.';
  if (text.length > 1000) return 'Keep the note under 1,000 characters.';
  return undefined;
}

/** Short labels for a risk row: accepted or snoozed, or more severe since. */
export function riskTags(r: Risk): { label: string; tone: 'neutral' | 'warn' | 'bad' | 'accent' }[] {
  const tags: { label: string; tone: 'neutral' | 'warn' | 'bad' | 'accent' }[] = [];
  if (r.decision?.superseded) tags.push({ label: `More severe since ${r.decision.kind === 'Accepted' ? 'accepted' : 'snoozed'}`, tone: 'bad' });
  else if (r.status === 'Accepted' && r.decision) tags.push({ label: `Accepted until ${fmtDay(r.decision.until)}`, tone: 'neutral' });
  else if (r.status === 'Snoozed' && r.decision) tags.push({ label: `Snoozed until ${fmtDay(r.decision.until)}`, tone: 'neutral' });
  return tags;
}

/** Severity counts for the risks the current view shows, from the per-rule counts. */
export function severityCounts(rules: RiskRuleSummary[]): { high: number; medium: number; low: number; total: number } {
  const high = rules.reduce((n, r) => n + r.high, 0);
  const medium = rules.reduce((n, r) => n + r.medium, 0);
  const low = rules.reduce((n, r) => n + r.low, 0);
  return { high, medium, low, total: high + medium + low };
}
