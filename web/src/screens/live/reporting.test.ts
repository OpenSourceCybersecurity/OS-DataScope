import { describe, expect, it } from 'vitest';
import type { Dashboard, Server, Share } from '../../api/client';
import { api } from '../../api/client';
import { REPORT_PERIODS, coverageNote, exposureNote, fmtDay, niceMax, parseScope, pickStandard, plot, readPercent, scopeOptions } from './reporting';

const server = (id: string, hostname: string): Server => ({
  id, hostname, state: 'Enrolled', lastSeenAt: null, lastError: null, createdAt: '',
  schedule: { frequency: 'Off', startHour: 1, startMinute: 0, everyHours: null, day: null, stopHour: null, nextScanAt: null, missedAt: null, timeZone: 'UTC', fullScan: 'Off', fullScanDay: null, nextFullScanAt: null },
});
const share = (id: string, serverId: string, name: string, kind: Share['kind'] = 'Disk'): Share =>
  ({ id, serverId, hostname: '', name, uncPath: `\\\\host\\${name}`, remark: null, kind, included: true }) as Share;

const dashboard = (over: Partial<Dashboard['coverage']> = {}, exposure: Partial<Dashboard['exposure']> = {}): Dashboard => ({
  generatedAt: '',
  domain: null,
  directorySyncedAt: null,
  coverage: { servers: 1, sharesIncluded: 4, sharesScanned: 3, sharesPartial: 1, sharesUnavailable: 0, sharesNotScanned: 0, files: 10, bytes: 0, filesRead: 9, filesNotRead: 1, gaps: 1, lastScanAt: null, ...over },
  sensitive: { restricted: 0, confidential: 0, policyVersion: 'v' },
  exposure: { open: 0, possiblyOpen: 0, unknown: 0, sensitiveFilesOpen: 0, truncated: false, top: [], ...exposure },
  owners: { owned: 0, unowned: 0, orphanedOwners: 0 },
  reviews: { openCampaigns: 0, reviewsOpen: 0, reviewsOverdue: 0 },
  age: [],
  trend: [],
});

describe('report scope', () => {
  it('parses each kind of scope value', () => {
    expect(parseScope('')).toEqual({});
    expect(parseScope('server:a')).toEqual({ serverId: 'a' });
    expect(parseScope('share:b')).toEqual({ shareId: 'b' });
    expect(parseScope('nonsense')).toEqual({});
  });

  it('lists servers in order with their disk shares, never admin shares', () => {
    const options = scopeOptions([server('2', 'fs02'), server('1', 'fs01')], [share('s1', '1', 'HR'), share('s2', '1', 'C$', 'Special'), share('s3', '2', 'Ops')]);
    expect(options.map((o) => o.value)).toEqual(['', 'server:1', 'share:s1', 'server:2', 'share:s3']);
  });

  it('asks for a period only for reports over a period, within what the server accepts', () => {
    expect(api.reportUrl('scan-coverage', 'csv', { serverId: 'a' })).toBe('/api/v1/reports/scan-coverage?format=csv&serverId=a');
    expect(api.reportUrl('file-activity', 'html', {}, 90)).toBe('/api/v1/reports/file-activity?format=html&days=90');
    expect(REPORT_PERIODS.map((p) => p.days)).toContain(30);
    expect(REPORT_PERIODS.every((p) => p.days >= 1 && p.days <= 3660)).toBe(true);
  });

  it('names the standard for a compliance evidence report', () => {
    expect(api.reportUrl('compliance-evidence', 'html', {}, undefined, 'pci-dss')).toBe('/api/v1/reports/compliance-evidence?format=html&standard=pci-dss');
    expect(api.reportUrl('compliance-evidence', 'csv', { shareId: 's' }, undefined, 'glba')).toBe('/api/v1/reports/compliance-evidence?format=csv&shareId=s&standard=glba');
  });

  it('only picks a standard that is turned on, keeping the one chosen while it still is', () => {
    const standards = [
      { id: 'hipaa', enabled: false },
      { id: 'glba', enabled: true },
      { id: 'pci-dss', enabled: true },
    ];
    expect(pickStandard(standards, '')).toBe('glba');
    expect(pickStandard(standards, 'pci-dss')).toBe('pci-dss');
    expect(pickStandard(standards, 'hipaa')).toBe('glba');
    expect(pickStandard([{ id: 'hipaa', enabled: false }], 'hipaa')).toBeUndefined();
    expect(pickStandard([], '')).toBeUndefined();
  });
});

describe('dashboard text', () => {
  it('rounds read coverage down so a gap is never shown as 100%', () => {
    expect(readPercent(dashboard({ filesRead: 996, filesNotRead: 4 }))).toBe(99);
    expect(readPercent(dashboard({ filesRead: 0, filesNotRead: 0 }))).toBeNull();
  });

  it('names partial and unscanned shares', () => {
    expect(coverageNote(dashboard())).toBe('3 of 4 shares scanned completely, 1 partly');
    expect(coverageNote(dashboard({ sharesIncluded: 0 }))).toBe('No shares included yet');
  });

  it('keeps possibly open folders visible', () => {
    expect(exposureNote(dashboard({}, { open: 1, possiblyOpen: 2 }))).toBe('1 folder open, 2 possibly open');
    expect(exposureNote(dashboard({}, { unknown: 3 }))).toBe('3 folders with unreadable permissions');
    expect(exposureNote(dashboard())).toBe('No sensitive folder is open to everyone');
  });
});

describe('trend helpers', () => {
  const point = (day: string, files: number) => ({ day, shares: 1, files, bytes: 0, restricted: 0, confidential: 0, filesNotRead: 0 });

  it('rounds the axis top up to 1, 2 or 5 times a power of ten', () => {
    expect([0, 1, 3, 7, 10, 11, 180, 2001].map(niceMax)).toEqual([1, 1, 5, 10, 10, 20, 200, 5000]);
  });

  it('spaces points by date, not by index', () => {
    const pts = plot([point('2026-01-01', 0), point('2026-01-02', 5), point('2026-01-11', 10)], (p) => p.files, 100, 50, 10);
    expect(pts.map((p) => Math.round(p.x))).toEqual([0, 10, 100]);
    expect(pts.map((p) => p.y)).toEqual([50, 25, 0]);
  });

  it('centres a single point and shows days in UTC', () => {
    expect(plot([point('2026-03-01', 1)], (p) => p.files, 100, 50, 1)[0].x).toBe(50);
    expect(fmtDay('2026-10-03')).toBe('3 Oct 2026');
  });
});
