import { describe, expect, it } from 'vitest';
import type { Risk, RiskRuleSummary } from '../../api/client';
import { addDays, fmtDay, groupByRule, raisedText, riskDetail, riskLinks, riskTags, severityCounts, standardNames, todayUtc, triageProblem } from './risks';

const risk = (over: Partial<Risk>): Risk => ({
  id: 'x',
  rule: 'open-sensitive',
  severity: 'High',
  title: 'Restricted files open to any domain user',
  where: '\\\\fs01\\Public',
  serverId: null,
  shareId: 'sh1',
  folderId: 'f1',
  restricted: 3,
  confidential: 0,
  who: 'Any domain user',
  whoSid: null,
  access: 'Read',
  certainty: 'Likely',
  count: 3,
  why: 'Why.',
  fix: 'Fix.',
  status: 'Open',
  decision: null,
  standards: [],
  raisedFrom: null,
  ...over,
});

describe('risks screen', () => {
  it('names the standards a risk involves and says when that raised its severity', () => {
    const on = [{ id: 'pci-dss', name: 'PCI DSS' }, { id: 'glba', name: 'GLBA' }];
    expect(standardNames(risk({}), on)).toEqual([]);
    expect(standardNames(risk({ standards: ['pci-dss', 'glba'] }), on)).toEqual(['PCI DSS', 'GLBA']);
    // A standard the list doesn't have (it couldn't load, or was turned off since) shows by its id.
    expect(standardNames(risk({ standards: ['hipaa'] }), on)).toEqual(['hipaa']);
    expect(raisedText(risk({ standards: ['pci-dss'] }), ['PCI DSS'])).toBeUndefined();
    expect(raisedText(risk({ raisedFrom: 'Medium' }), ['PCI DSS'])).toBe('Raised from Medium for PCI DSS');
    expect(raisedText(risk({ raisedFrom: 'Low' }), ['HIPAA', 'PCI DSS', 'GLBA'])).toBe('Raised from Low for HIPAA, PCI DSS and GLBA');
  });

  it('sends an overdue access review to Access reviews', () => {
    expect(riskLinks(risk({ rule: 'review-overdue', folderId: 'f1' }))).toEqual([
      { label: 'Start an access review', route: 'reviews' },
      { label: 'See who has access', route: 'access', param: 'folder:f1' },
      { label: 'Open the share', route: 'shares', param: 'sh1' },
    ]);
  });

  it('links each risk to where it can be looked into, owners first for ownership risks', () => {
    expect(riskLinks(risk({})).map((l) => [l.route, l.param])).toEqual([['access', 'folder:f1'], ['shares', 'sh1']]);
    expect(riskLinks(risk({ rule: 'no-owner' }))[0]).toEqual({ label: 'Assign an owner', route: 'shares', param: 'sh1' });
    expect(riskLinks(risk({ rule: 'secrets' }))[0].route).toBe('discovery');
    expect(riskLinks(risk({ rule: 'not-covered', folderId: null })).map((l) => l.label)).toEqual(['See what was missed']);
    expect(riskLinks(risk({ rule: 'stale-sensitive', shareId: 'sh1', folderId: null }))).toEqual([
      { label: 'See the old sensitive files', route: 'cleanup', param: 'sensitive:sh1' },
      { label: 'Open the share', route: 'shares', param: 'sh1' },
    ]);
    expect(riskLinks(risk({ rule: 'nesting-loop', shareId: null, folderId: null, whoSid: 'S-1-5-21-1-2-3-2005' }))).toEqual([
      { label: 'See the groups', route: 'access', param: 'user:S-1-5-21-1-2-3-2005' },
    ]);
  });

  it('groups by rule in the server order and skips empty rules', () => {
    const rules: RiskRuleSummary[] = [
      { id: 'open-sensitive', name: 'Open', high: 1, medium: 0, low: 0 },
      { id: 'secrets', name: 'Secrets', high: 0, medium: 0, low: 0 },
      { id: 'direct-user', name: 'Direct', high: 0, medium: 0, low: 2 },
    ];
    const groups = groupByRule([risk({ id: 'a', rule: 'direct-user' }), risk({ id: 'b' }), risk({ id: 'c', rule: 'direct-user' })], rules);
    expect(groups.map((g) => [g.rule.id, g.items.map((i) => i.id)])).toEqual([['open-sensitive', ['b']], ['direct-user', ['a', 'c']]]);
  });

  it('summarises who and how many files in one line', () => {
    expect(riskDetail(risk({}))).toBe('Any domain user: Read · 3 restricted files');
    expect(riskDetail(risk({ restricted: 1, confidential: 0 }))).toBe('Any domain user: Read · 1 restricted file');
    expect(riskDetail(risk({ who: null, access: null, restricted: 2, confidential: 1 }))).toBe('2 restricted, 1 confidential files');
    expect(riskDetail(risk({ rule: 'nesting-loop', who: 'A, B', restricted: null, confidential: null }))).toBe('');
  });
});

describe('risk triage', () => {
  const today = '2026-10-10';
  const decision = (over: Partial<NonNullable<Risk['decision']>> = {}): NonNullable<Risk['decision']> => ({
    id: 1, kind: 'Accepted', note: 'Made-up reason here', until: '2026-11-09', severity: 'High', decidedByName: 'GOV\\rita', decidedAt: '2026-10-10T12:00:00Z', superseded: false, ...over,
  });

  it('counts days in UTC and shows dates without shifting them', () => {
    expect(addDays(today, 30)).toBe('2026-11-09');
    expect(addDays('2026-12-31', 1)).toBe('2027-01-01');
    expect(addDays('2028-02-28', 1)).toBe('2028-02-29');
    expect(todayUtc(new Date('2026-10-10T23:30:00-05:00'))).toBe('2026-10-11');
    expect(fmtDay('2026-11-09')).toBe('Nov 9, 2026');
  });

  it('asks for a reason to accept and keeps dates inside the limits the server enforces', () => {
    expect(triageProblem('Accepted', 'Only made-up records here.', addDays(today, 365), today)).toBeUndefined();
    expect(triageProblem('Accepted', 'Only made-up records here.', addDays(today, 366), today)).toMatch(/within a year/);
    expect(triageProblem('Accepted', 'too short', addDays(today, 30), today)).toMatch(/why/);
    expect(triageProblem('Accepted', '   padded   ', addDays(today, 30), today)).toMatch(/why/);
    expect(triageProblem('Snoozed', '', addDays(today, 90), today)).toBeUndefined();
    expect(triageProblem('Snoozed', '', addDays(today, 91), today)).toMatch(/90 days/);
    expect(triageProblem('Snoozed', '', today, today)).toMatch(/after today/);
    expect(triageProblem('Snoozed', '', '', today)).toMatch(/after today/);
    expect(triageProblem('Snoozed', 'x'.repeat(1001), addDays(today, 3), today)).toMatch(/1,000/);
  });

  it('tags accepted, snoozed and more severe risks', () => {
    expect(riskTags(risk({}))).toEqual([]);
    expect(riskTags(risk({ status: 'Accepted', decision: decision() }))).toEqual([{ label: 'Accepted until Nov 9, 2026', tone: 'neutral' }]);
    expect(riskTags(risk({ status: 'Snoozed', decision: decision({ kind: 'Snoozed' }) }))[0].label).toBe('Snoozed until Nov 9, 2026');
    expect(riskTags(risk({ status: 'Open', decision: decision({ severity: 'Medium', superseded: true }) }))).toEqual([{ label: 'More severe since accepted', tone: 'bad' }]);
  });

  it('counts severities for whichever view is showing', () => {
    expect(severityCounts([
      { id: 'open-sensitive', name: 'Open', high: 2, medium: 0, low: 0 },
      { id: 'no-owner', name: 'No owner', high: 0, medium: 1, low: 3 },
    ])).toEqual({ high: 2, medium: 1, low: 3, total: 6 });
    expect(severityCounts([])).toEqual({ high: 0, medium: 0, low: 0, total: 0 });
  });
});
