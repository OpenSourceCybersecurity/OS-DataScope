import { describe, expect, it } from 'vitest';
import type { FileItem, Share } from '../../domain/types';
import { activityNote, cleanupParam, cutoffOf, filesText, demoSource, parseCleanupParam, percent, sensitiveText, yearChoices, yearsLabel } from './cleanup';

const NOW = new Date('2026-10-09T17:00:00Z');
const daysAgo = (d: number) => new Date(NOW.getTime() - d * 864e5).toISOString();
const share = (id: string, unc: string, serverId = 'srv1'): Share =>
  ({ id, serverId, name: id, unc, description: '', shareAcl: [], ntfsAcl: [], folders: [], scan: { state: 'complete', lastScan: daysAgo(1), progress: 1 } }) as Share;
const file = (id: string, shareId: string, path: string, size: number, age: number, o: Partial<FileItem> = {}): FileItem => ({
  id,
  shareId,
  path,
  name: path.split('/').pop()!,
  ext: path.split('.').pop()!,
  size,
  modified: daysAgo(age),
  created: daysAgo(age),
  ownerSid: 'S-1',
  scanStatus: 'scanned',
  findings: [],
  policyClassification: 'Internal',
  ...o,
});

// Made-up shares and files.
const shares = [share('fin', '\\\\fs01\\Finance'), share('hr', '\\\\fs01\\HR')];
const files = [
  file('a', 'fin', 'Archive/payroll-2019.csv', 50_000, 2000, { policyClassification: 'Restricted' }),
  file('b', 'fin', 'Archive/notes.txt', 2_000, 1300),
  file('c', 'fin', 'Archive/copied.pdf', 70_000, 2000, { created: daysAgo(20) }),
  file('d', 'fin', 'Current/budget.xlsx', 40_000, 10),
  file('e', 'fin', 'Current/Handbook.pdf', 200_000, 300),
  file('f', 'hr', 'handbook.pdf', 200_000, 300),
  file('g', 'fin', 'tiny.txt', 10, 5),
  file('h', 'hr', 'tiny.txt', 10, 5),
];
const source = demoSource({ files, shares, now: NOW });

describe('stale and duplicate data', () => {
  it('words the numbers plainly', () => {
    expect([percent(0, 10), percent(1, 1000), percent(5, 10), percent(999, 1000), percent(10, 10)]).toEqual(['0%', '<1%', '50%', '>99%', '100%']);
    expect([yearsLabel(1), yearsLabel(3)]).toEqual(['1 year', '3 years']);
    expect(sensitiveText(1, 0)).toBe('1 restricted');
    expect(sensitiveText(2, 1_200)).toBe('2 restricted, 1,200 confidential');
    expect(cutoffOf(3, NOW)).toBe('2023-10-09T17:00:00.000Z');
  });

  it('says what file activity can and cannot show', () => {
    expect(activityNote({ servers: 2, activityServers: 0, years: 3 })).toMatch(/isn't collected on these servers/);
    expect(activityNote({ servers: 1, activityServers: 0, years: 3 })).toMatch(/this server/);
    expect(activityNote({ servers: 3, activityServers: 1, years: 2 })).toMatch(/1 of 3 servers.*last 2 years/);
    expect(activityNote({ servers: 2, activityServers: 2, years: 1 })).toMatch(/last 1 year, as recorded/);
    expect(activityNote({ servers: 0, activityServers: 0, years: 3 })).toBe('');
  });

  it('counts old files as stale, but not a recent copy of an old file', async () => {
    const s = await source.summary({});
    expect(s.years).toBe(3);
    expect([s.totals.staleFiles, s.totals.staleBytes, s.totals.staleRestricted]).toEqual([2, 52_000, 1]);
    expect(s.shares[0]).toMatchObject({ shareId: 'fin', staleFiles: 2 });
    expect(s.folders).toEqual([expect.objectContaining({ uncPath: '\\\\fs01\\Finance\\Archive', staleFiles: 2, restricted: 1 })]);
    expect((await source.summary({}, 5)).totals.staleFiles).toBe(1);
    expect((await source.summary({ shareId: 'hr' }, 1)).totals.staleFiles).toBe(0);
  });

  it('lists stale files largest or oldest first, by folder and a page at a time', async () => {
    expect((await source.staleFiles({}, 3, 'size', 0)).items.map((f) => f.name)).toEqual(['payroll-2019.csv', 'notes.txt']);
    expect((await source.staleFiles({}, 3, 'oldest', 0)).items.map((f) => f.name)).toEqual(['payroll-2019.csv', 'notes.txt']);
    const page = await source.staleFiles({ folderId: 'fin|Archive' }, 3, 'size', 1);
    expect([page.total, page.items.map((f) => f.name)]).toEqual([2, ['notes.txt']]);
  });

  it('lists only the sensitive stale files when asked', async () => {
    const sensitive = await source.staleFiles({ sensitive: true }, 3, 'size', 0);
    expect(sensitive.items.map((f) => [f.name, f.label])).toEqual([['payroll-2019.csv', 'Restricted']]);
    expect(sensitive.total).toBe(1);
  });

  it('offers thresholds up to 30 years, with the server default added if it is unusual', () => {
    expect(yearChoices(3)).toEqual([1, 2, 3, 5, 7, 10, 15, 20, 25, 30]);
    expect(yearChoices(12)).toEqual([1, 2, 3, 5, 7, 10, 12, 15, 20, 25, 30]);
    expect(yearsLabel(25)).toBe('25 years');
  });

  it('opens on a share and on the sensitive files from a link', () => {
    expect(cleanupParam('sh1', true)).toBe('sensitive:sh1');
    expect(cleanupParam(undefined, true)).toBe('sensitive');
    expect(cleanupParam('sh1', false)).toBe('sh1');
    expect(cleanupParam(undefined, false)).toBeUndefined();
    expect(parseCleanupParam('sensitive:sh1')).toEqual({ shareId: 'sh1', sensitive: true });
    expect(parseCleanupParam('sensitive')).toEqual({ shareId: '', sensitive: true });
    expect(parseCleanupParam('sh1')).toEqual({ shareId: 'sh1', sensitive: false });
    expect(parseCleanupParam(undefined)).toEqual({ shareId: '', sensitive: false });
    expect(parseCleanupParam('source:mail')).toEqual({ shareId: '', sensitive: false });
  });

  it('groups copies by name and size across shares and leaves out tiny files', async () => {
    const d = await source.duplicates({}, 0);
    expect([d.total, d.copies, d.wasted]).toEqual([1, 1, 200_000]);
    expect(d.items[0]).toMatchObject({ copies: 2, shares: 2, sameModified: true });
    expect(d.items[0].locations.map((l) => l.path)).toEqual(['\\\\fs01\\Finance\\Current', '\\\\fs01\\HR']);
    expect((await source.duplicates({ shareId: 'fin' }, 0)).total).toBe(0);
  });

  it('says file or files', () => {
    expect(filesText(1)).toBe('1 file');
    expect(filesText(0)).toBe('0 files');
    expect(filesText(18420)).toBe('18,420 files');
  });
});
