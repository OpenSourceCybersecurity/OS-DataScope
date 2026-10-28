import { describe, expect, it } from 'vitest';
import type { DirectoryStatus, DirectorySnapshot } from '../../api/client';
import { EXPOSURE, directoryFreshness, openText, openTone, parseAccessParam, rangeText } from './access';

const snap = (over: Partial<DirectorySnapshot> = {}): DirectorySnapshot => ({
  id: 's1', state: 'Completed', domain: 'northwind.test', serverName: 'dc01', requestedAt: '2026-10-09T10:00:00Z', startedAt: null,
  completedAt: '2026-10-09T10:01:00Z', attempts: 1, users: 1200, disabledUsers: 30, groups: 310, computers: 40, foreignPrincipals: 2,
  memberships: 5000, unresolvedMembers: 0, cycles: 0, truncated: false, warnings: [], lastError: null, ...over,
});
const status = (over: Partial<DirectoryStatus> = {}): DirectoryStatus => ({ latest: snap(), pending: null, lastFailure: null, ...over });

describe('access ranges', () => {
  it('shows one value only when Data Scope is sure', () => {
    expect(rangeText({ rights: 'Modify', upTo: 'Modify', certainty: 'Likely' })).toBe('Modify');
  });

  it('never presents a possible right as a certain one', () => {
    expect(rangeText({ rights: 'No access', upTo: 'Read', certainty: 'Partial' })).toBe('Possibly read');
    expect(rangeText({ rights: 'Read', upTo: 'Full control', certainty: 'Partial' })).toBe('Read, possibly full control');
    expect(rangeText({ rights: 'No access', upTo: 'Full control', certainty: 'Unknown' })).toBe('Unknown');
  });

  it('rates open access by what any domain user can certainly or possibly do', () => {
    expect(openTone({ rights: 'Read', upTo: 'Read', certainty: 'Likely' })).toBe('bad');
    expect(openText({ rights: 'Read', upTo: 'Read', certainty: 'Likely' })).toBe('Anyone in the domain can read');
    expect(openText({ rights: 'Modify', upTo: 'Modify', certainty: 'Likely' })).toBe('Anyone in the domain can modify');
    expect(openTone({ rights: 'No access', upTo: 'Read', certainty: 'Partial' })).toBe('warn');
    expect(openTone({ rights: 'No access', upTo: 'No access', certainty: 'Likely' })).toBe('good');
    // Unreadable permissions are not "safe".
    expect(openTone({ rights: 'No access', upTo: 'Full control', certainty: 'Unknown' })).toBe('neutral');
    expect(openText({ rights: 'No access', upTo: 'Full control', certainty: 'Unknown' })).toMatch(/Unknown/);
  });

  it('labels every exposure status with an explanation', () => {
    expect(EXPOSURE.Open.tone).toBe('bad');
    expect(EXPOSURE.PossiblyOpen.help).toMatch(/could not rule it out/);
    expect(EXPOSURE.Unknown.tone).toBe('neutral');
  });
});

describe('directory freshness', () => {
  const now = new Date('2026-10-09T12:00:00Z').getTime();

  it('summarises a recent sync', () => {
    expect(directoryFreshness(status(), now)).toEqual({ tone: 'good', text: 'Directory synced from northwind.test: 1,200 users, 310 groups.' });
  });

  it('warns when nothing has been synced, and says when the first sync is running', () => {
    expect(directoryFreshness(status({ latest: null }), now).tone).toBe('warn');
    expect(directoryFreshness(status({ latest: null, pending: snap({ state: 'Running' }) }), now).text).toMatch(/first directory sync is running/);
  });

  it('shows a failure, and keeps using the previous snapshot when there is one', () => {
    const failed = snap({ state: 'Failed', lastError: 'The domain controller refused the sign-in.' });
    expect(directoryFreshness(status({ latest: null, lastFailure: failed }), now)).toEqual({ tone: 'bad', text: 'The directory sync failed: The domain controller refused the sign-in.' });
    expect(directoryFreshness(status({ lastFailure: failed }), now).text).toMatch(/Showing the previous one/);
  });

  it('flags data older than two days', () => {
    expect(directoryFreshness(status({ latest: snap({ completedAt: '2026-10-06T11:00:00Z' }) }), now).tone).toBe('warn');
  });
});

describe('deep links', () => {
  it('opens a folder or a person', () => {
    expect(parseAccessParam('folder:f1')).toEqual({ folderId: 'f1' });
    expect(parseAccessParam('user:S-1-5-21-1-2-3-1001')).toEqual({ sid: 'S-1-5-21-1-2-3-1001', kind: 'User' });
    expect(parseAccessParam('group:S-1-5-21-1-2-3-2001')).toEqual({ sid: 'S-1-5-21-1-2-3-2001', kind: 'Group' });
    expect(parseAccessParam('S-1-5-21-1')).toEqual({});
    expect(parseAccessParam('source:mail')).toEqual({});
    expect(parseAccessParam(undefined)).toEqual({});
  });
});
