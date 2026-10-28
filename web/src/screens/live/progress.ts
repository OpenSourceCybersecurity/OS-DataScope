// Pure helpers for Progress over time (D165), kept apart from the page so they can be unit tested.

import type { Progress, ProgressPoint } from '../../api/client';
import type { Route } from '../../App';
import { fmtNum } from '../../ui/kit';
import { fmtDay } from './reporting';

/** The ranges the page offers, in days. */
export const RANGES: { id: '30' | '90' | '365'; label: string }[] = [
  { id: '30', label: '30 days' },
  { id: '90', label: '90 days' },
  { id: '365', label: '1 year' },
];

/** The range from a link ("365"), or 90 days when the link names none the page offers. */
export const rangeOf = (param: string | undefined): '30' | '90' | '365' => RANGES.find((r) => r.id === param)?.id ?? '90';

export interface Measure {
  id: string;
  title: string;
  /** Null when the day didn't measure it. */
  value: (p: ProgressPoint) => number | null;
  /** Which way is progress; neutral when a rise is neither good nor bad, such as more sensitive files found. */
  better: 'down' | 'up' | 'neutral';
  percent?: boolean;
  link?: { route: Route; param?: string };
  about: string;
}

export const MEASURES: Measure[] = [
  { id: 'high', title: 'Open high risks', value: (p) => p.risksHigh, better: 'down', link: { route: 'risks' }, about: 'High risks not accepted or snoozed' },
  { id: 'medium', title: 'Open medium risks', value: (p) => p.risksMedium, better: 'down', link: { route: 'risks' }, about: 'Medium risks not accepted or snoozed' },
  { id: 'open', title: 'Sensitive files open to everyone', value: (p) => p.sensitiveOpen, better: 'down', link: { route: 'exposure' }, about: 'On file shares any domain user can open' },
  { id: 'notread', title: 'Files not read', value: (p) => p.filesNotRead, better: 'down', about: 'Coverage gaps: files whose contents could not be read' },
  { id: 'unowned', title: 'Shares with no owner', value: (p) => p.sharesUnowned, better: 'down', link: { route: 'owners' }, about: 'Included shares nobody is responsible for' },
  { id: 'sensitive', title: 'Sensitive files', value: (p) => p.restricted + p.confidential, better: 'neutral', link: { route: 'discovery' }, about: 'Restricted and Confidential, as scans found them' },
];

/** The days a measure has a value on, for its chart. */
export const seriesOf = (points: ProgressPoint[], m: Measure) =>
  points.flatMap((p) => {
    const value = m.value(p);
    return value === null ? [] : [{ day: p.day, value }];
  });

export const formatValue = (m: Pick<Measure, 'percent'>, n: number) => (m.percent ? `${Math.round(n)}%` : fmtNum(n));

export interface Change {
  now: number;
  /** The first day in the range the measure has a value on, and its value then; null with fewer than two such days. */
  since: { day: string; value: number } | null;
  tone: 'good' | 'bad' | 'neutral';
  text: string;
}

/** How a measure moved from the first day in the range to the last, in words, with whether that is progress. */
export function changeOf(points: ProgressPoint[], m: Measure): Change | null {
  const series = seriesOf(points, m);
  if (series.length === 0) return null;
  const last = series[series.length - 1];
  if (series.length < 2) return { now: last.value, since: null, tone: 'neutral', text: 'Changes show from the next day measured' };
  const first = series[0];
  const delta = last.value - first.value;
  const since = `since ${fmtDay(first.day)}`;
  if (delta === 0) return { now: last.value, since: first, tone: 'neutral', text: `No change ${since}` };
  const amount = m.percent ? `${Math.abs(delta)} ${Math.abs(delta) === 1 ? 'point' : 'points'}` : fmtNum(Math.abs(delta));
  const tone = m.better === 'neutral' ? 'neutral' : (delta < 0) === (m.better === 'down') ? 'good' : 'bad';
  return { now: last.value, since: first, tone, text: `${delta < 0 ? 'Down' : 'Up'} ${amount} ${since}, from ${formatValue(m, first.value)}` };
}

/** Days in the range whose risk check stopped early, so their risk counts may be low. */
export const limitedDays = (points: ProgressPoint[]) => points.filter((p) => p.risksLimited).length;

/** Why the range is shorter than asked, or null when it isn't. */
export function shortRange(d: Pick<Progress, 'from' | 'keptSince' | 'points'>): string | null {
  if (!d.keptSince || d.keptSince <= d.from) return null;
  return `Data Scope has kept progress since ${fmtDay(d.keptSince)}, so the range starts there. Each day from now on is added.`;
}
