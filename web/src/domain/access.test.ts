import { describe, expect, it } from 'vitest';
import { effectiveAccess, findCycles, resolveMembership, simulateRemoval, type Directory } from './access';
import { buildDirectory, buildShares, GROUPS, userSid } from '../data/seed';

const dir: Directory = new Map(buildDirectory().map((p) => [p.sid, p]));
const shares = buildShares();
const share = (id: string) => shares.find((s) => s.id === id)!;

describe('membership resolution', () => {
  it('explains nested membership with a chain', () => {
    const m = resolveMembership(dir, userSid('ohaddad'));
    expect(m.chains.get(GROUPS.Finance_Read)).toEqual([userSid('ohaddad'), GROUPS.Finance_Reviewers, GROUPS.Finance_Read]);
  });
  it('stops at nesting loops and reports them', () => {
    const m = resolveMembership(dir, userSid('arahman'));
    expect(m.cycles.length).toBeGreaterThan(0);
    expect(findCycles(dir)).toHaveLength(1);
  });
});

describe('effective access', () => {
  it('limits NTFS rights by the share permission (A8)', () => {
    // Finance_Read has NTFS Read on Sales, but the Sales share ACL only admits Sales_Team.
    const e = effectiveAccess(dir, share('sh-sales'), userSid('gholloway'));
    expect(e.ntfsRights).toBe('Read');
    expect(e.shareRights).toBe('None');
    expect(e.rights).toBe('None');
  });
  it('honours explicit deny over inherited/explicit allow', () => {
    const e = effectiveAccess(dir, share('sh-legal'), userSid('jstone'));
    expect(e.rights).toBe('None');
    expect(e.denies.length).toBeGreaterThan(0);
  });
  it('uses the folder ACL where inheritance is broken', () => {
    expect(effectiveAccess(dir, share('sh-hr'), userSid('byoder')).rights).toBe('None');
    expect(effectiveAccess(dir, share('sh-hr'), userSid('byoder'), 'Benefits').rights).toBe('Read');
  });
  it('marks access through a nesting loop as partial', () => {
    expect(effectiveAccess(dir, share('sh-eng'), userSid('tbecker')).confidence).toBe('partial');
  });
  it('flags disabled accounts that still hold access', () => {
    const e = effectiveAccess(dir, share('sh-fin'), userSid('gpalmer'));
    expect(e.rights).toBe('Modify');
    expect(e.notes.join(' ')).toMatch(/disabled/);
  });
  it('shows blast radius and alternative paths for a removal', () => {
    const impact = simulateRemoval(dir, share('sh-fin'), GROUPS.Finance_Read);
    expect(impact.loseAccess).toContain(userSid('gholloway'));
    expect(impact.keepAccess.map((k) => k.sid)).not.toContain(userSid('gholloway'));
  });
});
