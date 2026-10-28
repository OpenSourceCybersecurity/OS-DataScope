import type { Dashboard, ReportScope, Server, Share } from '../../api/client';

/** A scope choice as one select value: '' (everything), 'server:<id>' or 'share:<id>'. */
/** Periods offered for reports over file activity. The server takes 1 to 3,660 days. */
export const REPORT_PERIODS = [
  { days: 7, label: 'Last 7 days' },
  { days: 30, label: 'Last 30 days' },
  { days: 90, label: 'Last 90 days' },
  { days: 365, label: 'Last 12 months' },
];

/** The standard a report is made for: the one chosen while it is still turned on, else the first one turned on. */
export function pickStandard(on: { id: string; enabled: boolean }[], chosen: string): string | undefined {
  const enabled = on.filter((s) => s.enabled);
  return (enabled.find((s) => s.id === chosen) ?? enabled[0])?.id;
}

export function parseScope(value: string): ReportScope {
  if (value.startsWith('server:')) return { serverId: value.slice(7) };
  if (value.startsWith('share:')) return { shareId: value.slice(6) };
  return {};
}

export interface ScopeOption {
  value: string;
  label: string;
}

/** Everything, then each file server followed by its disk shares, in name order. Admin shares are never scanned. */
export function scopeOptions(servers: Server[], shares: Share[]): ScopeOption[] {
  const options: ScopeOption[] = [{ value: '', label: 'All file servers' }];
  for (const server of [...servers].sort((a, b) => a.hostname.localeCompare(b.hostname))) {
    options.push({ value: `server:${server.id}`, label: `Server ${server.hostname}` });
    shares
      .filter((s) => s.serverId === server.id && s.kind === 'Disk')
      .sort((a, b) => a.name.localeCompare(b.name))
      .forEach((s) => options.push({ value: `share:${s.id}`, label: ` ${s.uncPath}` }));
  }
  return options;
}

/** Share of current files whose contents were read, as a whole percentage; null before anything was scanned. */
export function readPercent(d: Dashboard): number | null {
  const total = d.coverage.filesRead + d.coverage.filesNotRead;
  if (total === 0) return null;
  // Never round a gap away: 99.6% read is shown as 99%, not 100%.
  return Math.floor((d.coverage.filesRead / total) * 100);
}

/** Plain-language summary of what was covered, for the coverage tile. */
export function coverageNote(d: Dashboard): string {
  const c = d.coverage;
  if (c.sharesIncluded === 0) return 'No shares included yet';
  const parts = [`${c.sharesScanned} of ${c.sharesIncluded} shares scanned completely`];
  if (c.sharesPartial) parts.push(`${c.sharesPartial} partly`);
  if (c.sharesUnavailable) parts.push(`${c.sharesUnavailable} unavailable`);
  if (c.sharesNotScanned) parts.push(`${c.sharesNotScanned} not scanned yet`);
  return parts.join(', ');
}

/** What the "open to everyone" tile says, keeping possibly-open folders visible rather than counting them as safe. */
export function exposureNote(d: Dashboard): string {
  const e = d.exposure;
  if (e.open === 0 && e.possiblyOpen === 0) return e.unknown ? `${e.unknown} folders with unreadable permissions` : 'No sensitive folder is open to everyone';
  const parts = [`${e.open} ${e.open === 1 ? 'folder' : 'folders'} open`];
  if (e.possiblyOpen) parts.push(`${e.possiblyOpen} possibly open`);
  if (e.unknown) parts.push(`${e.unknown} unknown`);
  return parts.join(', ');
}

/** The smallest 1, 2 or 5 times a power of ten at or above n: a round top for a chart's axis. */
export function niceMax(n: number): number {
  if (!(n > 0)) return 1;
  const power = 10 ** Math.floor(Math.log10(n));
  for (const step of [1, 2, 5, 10]) if (step * power >= n) return step * power;
  return 10 * power;
}

export interface PlotPoint {
  x: number;
  y: number;
  day: string;
  value: number;
}

/** Days (yyyy-mm-dd) as day numbers, so gaps between scans keep their real width. */
const dayNumber = (day: string) => Date.parse(`${day}T00:00:00Z`) / 86_400_000;

/** Places each point in a width by height box: x by date, y from 0 (bottom) to top (top). */
export function plot<T extends { day: string }>(points: T[], value: (p: T) => number, width: number, height: number, top: number): PlotPoint[] {
  if (points.length === 0) return [];
  const first = dayNumber(points[0].day);
  const span = dayNumber(points[points.length - 1].day) - first;
  return points.map((p) => ({
    x: span === 0 ? width / 2 : ((dayNumber(p.day) - first) / span) * width,
    y: height - (value(p) / top) * height,
    day: p.day,
    value: value(p),
  }));
}

/** "3 Oct 2026" for a yyyy-mm-dd day, in UTC so a point never shifts to the day before. */
export const fmtDay = (day: string) =>
  new Date(`${day}T00:00:00Z`).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', timeZone: 'UTC' });
