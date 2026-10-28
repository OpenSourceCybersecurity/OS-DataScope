import type { HealthCheck, HealthReport, HealthStatus } from '../../api/client';

export const STATUS_LABEL: Record<HealthStatus, string> = {
  Ok: 'OK',
  Warning: 'Needs a look',
  Problem: 'Problem',
  Unknown: 'Not checked',
};

export const statusTone = (s: HealthStatus): 'good' | 'warn' | 'bad' | 'neutral' =>
  s === 'Ok' ? 'good' : s === 'Warning' ? 'warn' : s === 'Problem' ? 'bad' : 'neutral';

const RANK: Record<HealthStatus, number> = { Problem: 0, Warning: 1, Unknown: 2, Ok: 3 };

/** Problems first, then warnings, keeping the server's order within each. */
export const byUrgency = (checks: HealthCheck[]) => checks.map((c, i) => ({ c, i })).sort((a, b) => RANK[a.c.status] - RANK[b.c.status] || a.i - b.i).map((x) => x.c);

/** One line for the top of the page. */
export function headline(report: HealthReport): string {
  const problems = report.checks.filter((c) => c.status === 'Problem').length;
  const warnings = report.checks.filter((c) => c.status === 'Warning').length;
  if (problems === 0 && warnings === 0) return 'Everything Data Scope checks is working.';
  const parts = [problems && `${problems} problem${problems === 1 ? '' : 's'}`, warnings && `${warnings} thing${warnings === 1 ? '' : 's'} to look at`].filter(Boolean);
  return `${parts.join(' and ')}.`;
}
