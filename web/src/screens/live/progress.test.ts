import { describe, expect, it } from 'vitest';
import type { ProgressPoint } from '../../api/client';
import { MEASURES, changeOf, formatValue, limitedDays, rangeOf, seriesOf, shortRange } from './progress';

const point = (day: string, over: Partial<ProgressPoint> = {}): ProgressPoint => ({
  day, takenAt: `${day}T18:00:00Z`, lastScanAt: null, risksHigh: 10, risksMedium: 20, risksLow: 5, risksAccepted: 0, risksSnoozed: 0, risksLimited: false,
  files: 1000, filesNotRead: 50, restricted: 30, confidential: 70, sensitiveOpen: 12,
  sharesUnowned: 3, ...over,
});

const measure = (id: string) => MEASURES.find((m) => m.id === id)!;

describe('progress over time (D165)', () => {
  it('reads the range from a link and falls back to 90 days', () => {
    expect(rangeOf('365')).toBe('365');
    expect(rangeOf('30')).toBe('30');
    expect(rangeOf('7')).toBe('90');
    expect(rangeOf(undefined)).toBe('90');
  });

  it('calls a fall in risks progress and a rise in files not read a step back', () => {
    const points = [point('2026-10-01'), point('2026-10-05', { risksHigh: 6, filesNotRead: 60 }), point('2026-10-09', { risksHigh: 4, filesNotRead: 55 })];
    expect(changeOf(points, measure('high'))).toEqual({ now: 4, since: { day: '2026-10-01', value: 10 }, tone: 'good', text: 'Down 6 since 1 Oct 2026, from 10' });
    expect(changeOf(points, measure('notread'))).toMatchObject({ now: 55, tone: 'bad', text: 'Up 5 since 1 Oct 2026, from 50' });
    expect(changeOf(points, measure('unowned'))).toMatchObject({ tone: 'neutral', text: 'No change since 1 Oct 2026' });
    const more = [point('2026-10-01'), point('2026-10-09', { sharesUnowned: 5, restricted: 9 })];
    expect(changeOf(more, measure('unowned'))).toMatchObject({ tone: 'bad', text: 'Up 2 since 1 Oct 2026, from 3' });
    // Finding more or fewer sensitive files is neither good nor bad by itself.
    expect(changeOf(more, measure('sensitive'))).toMatchObject({ tone: 'neutral', text: 'Down 21 since 1 Oct 2026, from 100' });
  });

  it('needs two days to show a change', () => {
    expect(changeOf([point('2026-10-01')], measure('high'))).toEqual({ now: 10, since: null, tone: 'neutral', text: 'Changes show from the next day measured' });
    expect(changeOf([], measure('high'))).toBeNull();
  });

  it('skips days a measure has no value on', () => {
    const custom = { ...measure('high'), value: (p: ProgressPoint) => (p.day === '2026-10-01' ? null : p.risksHigh) };
    const points = [point('2026-10-01'), point('2026-10-03', { risksHigh: 9 }), point('2026-10-08', { risksHigh: 4 })];
    expect(seriesOf(points, custom)).toEqual([{ day: '2026-10-03', value: 9 }, { day: '2026-10-08', value: 4 }]);
    expect(changeOf(points, custom)).toMatchObject({ since: { day: '2026-10-03', value: 9 }, tone: 'good' });
    expect(changeOf([point('2026-10-01')], custom)).toBeNull();
  });

  it('formats percentages and counts', () => {
    expect(formatValue({ percent: true }, 42.4)).toBe('42%');
    expect(formatValue(measure('notread'), 12345)).toBe('12,345');
  });

  it('says why a range is short and counts days whose risk check stopped early', () => {
    expect(shortRange({ from: '2026-07-24', keptSince: '2026-10-01', points: [] })).toBe(
      'Data Scope has kept progress since 1 Oct 2026, so the range starts there. Each day from now on is added.',
    );
    expect(shortRange({ from: '2026-07-24', keptSince: '2026-05-01', points: [] })).toBeNull();
    expect(shortRange({ from: '2026-07-24', keptSince: null, points: [] })).toBeNull();
    expect(limitedDays([point('2026-10-01', { risksLimited: true }), point('2026-10-02')])).toBe(1);
  });
});
