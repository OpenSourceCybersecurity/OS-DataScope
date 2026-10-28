import { describe, expect, it } from 'vitest';
import type { ActivityAlert, ActivityEvent, ActivitySource } from '../../api/client';
import { CERTAINTY, OPERATION_FILTERS, SERIES, alertFilters, dayTotal, gapText, logHealth, sourceStatus, unusualLine, whereText } from './activity';

const base: ActivityEvent = {
  id: 1, serverId: 's', hostname: 'fs01', eventId: 4663, recordId: 10, time: '2026-10-10T09:00:00Z', principalSid: 'S-1-5-21-1-2-3-1104',
  principalName: 'NORTHWIND\\alice', operation: 'Read', label: 'Read', certainty: 'Performed', outcome: 'Success', shareId: 'sh', shareName: 'Finance',
  relativePath: 'Budget\\2026.xlsx', objectPath: 'D:\\Shares\\Finance\\Budget\\2026.xlsx', objectKind: 'File', pathSource: 'Event', correlatedRecordId: null, clientAddress: null,
};

describe('activity wording', () => {
  it('never calls an access check, a request or a share connection a read', () => {
    for (const c of ['AccessCheck', 'Requested', 'ShareSession'] as const) {
      expect(CERTAINTY[c].label).not.toMatch(/read|recorded|confirmed/i);
    }
    expect(CERTAINTY.AccessCheck.help).toMatch(/not proof/);
    expect(CERTAINTY.Performed.label).toBe('Recorded');
  });

  it('offers each operation once', () => {
    expect(new Set(OPERATION_FILTERS.map((o) => o.id)).size).toBe(OPERATION_FILTERS.length);
  });
});

describe('whereText', () => {
  it('shows the share path, and says when it came from another event', () => {
    expect(whereText(base)).toEqual({ path: 'Finance\\Budget\\2026.xlsx', note: null });
    expect(whereText({ ...base, eventId: 4660, operation: 'Deleted', pathSource: 'Correlated', correlatedRecordId: 9 }).note).toBe('path from event #9 on the same handle');
    expect(whereText({ ...base, relativePath: '' }).path).toBe('Finance (the share itself)');
  });

  it('falls back to the server path for unmatched events', () => {
    expect(whereText({ ...base, shareId: null, shareName: null, relativePath: null })).toEqual({ path: 'D:\\Shares\\Finance\\Budget\\2026.xlsx', note: 'not in a scanned share' });
  });

  it('explains a delete without a path rather than guessing', () => {
    const w = whereText({ ...base, eventId: 4660, operation: 'Deleted', shareId: null, shareName: null, relativePath: null, objectPath: null, pathSource: 'None' });
    expect(w.path).toBeNull();
    expect(w.note).toMatch(/does not name/);
  });
});

describe('chart and health', () => {
  it('keeps series in a fixed order and sums a day', () => {
    expect(SERIES.map((s) => s.key)).toEqual(['reads', 'writes', 'deletes', 'permissionChanges']);
    expect(dayTotal({ date: '2026-10-10', reads: 3, writes: 2, deletes: 1, permissionChanges: 1, denied: 5 })).toBe(7);
  });

  it('puts errors first and says when collection is off', () => {
    const s: ActivitySource = {
      serverId: 's', hostname: 'fs01', enabled: true, state: 'Error', lastReadAt: null, lastEventAt: null, nextReadAt: null, lastError: 'Access is denied.',
      lastErrorAt: null, eventsStored: 0, eventsIgnored: 0, eventsUnreadable: 0, unmatched: 0, shares: [], log: null, gaps: [],
    };
    expect(sourceStatus(s)).toEqual({ text: 'Access is denied.', tone: 'bad' });
    expect(sourceStatus({ ...s, enabled: false }).text).toBe('Off');
    expect(sourceStatus({ ...s, state: 'Ok' }).tone).toBe('good');
  });
});

describe('log health', () => {
  const now = Date.parse('2026-10-10T12:00:00Z');
  const log = { oldestRecordId: 1, newestRecordId: 1000, recordCount: 1000, oldestEventAt: '2026-10-07T12:00:00Z', sizeBytes: 20 * 1048576, maxSizeBytes: 128 * 1048576, full: false, checkedAt: null, behind: 10, atRisk: false };

  it('says how far back the log goes and how full it is', () => {
    expect(logHealth(log, now)).toEqual({ text: 'The Security log goes back about 3 days · 20 MB of 128 MB', warning: null });
    expect(logHealth({ ...log, oldestEventAt: '2026-10-10T07:00:00Z', maxSizeBytes: null }, now).text).toBe('The Security log goes back about 5 hours · 20 MB');
  });

  it('warns when the log could wrap before it is read, or is full', () => {
    expect(logHealth({ ...log, behind: 600, atRisk: true }, now).warning).toMatch(/^600 log records not read yet.*overwrite/);
    expect(logHealth({ ...log, full: true }, now).warning).toMatch(/full/);
  });

  it('describes gaps without claiming every missing record was a file event', () => {
    const gap = { id: 1, reason: 'Overwritten' as const, firstRecordId: 101, lastRecordId: 150, records: 50, detectedAt: '2026-10-10T10:00:00Z', logOldestEventAt: null };
    expect(gapText(gap)).toBe('50 log records were overwritten or cleared before Data Scope read them (records 101 to 150). Some may not have been file events.');
    expect(gapText({ ...gap, lastRecordId: 101, records: 1 })).toMatch(/^1 log record was .* read it/);
    expect(gapText({ ...gap, reason: 'LogReset', lastRecordId: null, records: null })).toMatch(/started again/);
  });
});

describe('alertFilters', () => {
  const alert: ActivityAlert = {
    id: 7, rule: 'MassDelete', title: 'Many files deleted', severity: 'High', state: 'Open', serverId: 'srv', hostname: 'fs01',
    principalSid: 'S-1-5-21-1-2-3-1104', principalName: 'NORTHWIND\\jordan', firstEventAt: '2026-10-10T09:00:00.000Z', lastEventAt: '2026-10-10T09:04:00.000Z',
    eventCount: 60, description: '', operation: 'Deleted', outcome: null, createdAt: '2026-10-10T09:05:00Z', acknowledgedByName: null, acknowledgedAt: null, note: null,
    unusual: false, usual: null, detail: null,
  };

  it('shows the events behind an alert: server, person, kind and time span', () => {
    expect(alertFilters(alert)).toEqual({
      filters: { serverId: 'srv', operation: 'Deleted', outcome: undefined, from: '2026-10-10T08:59:59.000Z', to: '2026-10-10T09:04:01.000Z', includeChecks: undefined },
      person: { sid: 'S-1-5-21-1-2-3-1104', name: 'NORTHWIND\\jordan' },
    });
  });

  it('includes access checks for refusals, and has nothing to show for missed events', () => {
    expect(alertFilters({ ...alert, rule: 'ManyDenied', operation: null, outcome: 'Failure' })?.filters).toMatchObject({ outcome: 'Failure', includeChecks: true });
    expect(alertFilters({ ...alert, rule: 'EventsMissed', principalSid: null })).toBeNull();
  });

  it('includes access checks for unusual behaviour, which counts them as touching a file (D166)', () => {
    const odd = { ...alert, rule: 'UnusualHours' as const, operation: null, unusual: true, usual: 9, detail: '02:00 to 03:00' };
    expect(alertFilters(odd)?.filters).toMatchObject({ operation: undefined, includeChecks: true, from: '2026-10-10T08:59:59.000Z' });
  });
});

describe('unusualLine', () => {
  const source = { serverId: 'srv', hostname: 'fs01', learnedOn: '2026-10-21', people: 40, judged: 31 };

  it('says how many people are judged and how many are still being learned', () => {
    expect(unusualLine(source, 5)).toBe('fs01: 31 of 40 people judged against their own past; 9 still being learned.');
    expect(unusualLine({ ...source, judged: 40 }, 5)).toBe('fs01: 40 of 40 people judged against their own past.');
    expect(unusualLine({ ...source, judged: 0, people: 1 }, 5)).toBe('fs01: learning 1 person. Each is judged once they have been active on 5 days.');
    expect(unusualLine({ ...source, people: 0, judged: 0 }, 5)).toBe('fs01: no activity to learn from yet.');
  });
});
