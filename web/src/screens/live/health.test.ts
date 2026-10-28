import { describe, expect, it } from 'vitest';
import type { HealthCheck, HealthReport } from '../../api/client';
import { byUrgency, headline, statusTone } from './health';

const check = (id: string, status: HealthCheck['status']): HealthCheck => ({ id, area: 'a', title: id, status, detail: '' });
const report = (...checks: HealthCheck[]): HealthReport => ({ overall: 'Ok', checkedAt: '2026-10-24T12:00:00Z', checks });

describe('system health', () => {
  it('puts problems first, then warnings, keeping the order otherwise', () => {
    expect(byUrgency([check('a', 'Ok'), check('b', 'Warning'), check('c', 'Problem'), check('d', 'Unknown'), check('e', 'Problem')]).map((c) => c.id)).toEqual(['c', 'e', 'b', 'd', 'a']);
  });

  it('sums up in one line', () => {
    expect(headline(report(check('a', 'Ok'), check('b', 'Unknown')))).toBe('Everything Data Scope checks is working.');
    expect(headline(report(check('a', 'Problem'), check('b', 'Warning'), check('c', 'Warning')))).toBe('1 problem and 2 things to look at.');
    expect(headline(report(check('a', 'Warning')))).toBe('1 thing to look at.');
  });

  it('colours each status', () => {
    expect([statusTone('Ok'), statusTone('Warning'), statusTone('Problem'), statusTone('Unknown')]).toEqual(['good', 'warn', 'bad', 'neutral']);
  });
});
