import { describe, expect, it } from 'vitest';
import type { Setting } from '../../api/client';
import {
  changedCount,
  changesOf,
  current,
  emailReady,
  isChanged,
  newServerProblem,
  parseList,
  problemOf,
  resetText,
  scheduleDraft,
  scheduleOf,
  sourceText,
  splitGroups,
  valueText,
  warningsOf,
} from './settings';

const setting = (key: string, o: Partial<Setting> = {}): Setting => ({
  key,
  group: 'g',
  label: key,
  help: '',
  kind: 'Whole',
  value: null,
  values: null,
  default: null,
  min: 0,
  max: 100,
  zeroLabel: null,
  optional: false,
  unit: null,
  choices: [],
  applies: '',
  source: 'Default',
  overridesFile: false,
  updatedAt: null,
  updatedByName: null,
  ...o,
});

const years = setting('Cleanup:StaleAfterYears', { value: '3', default: '3', min: 1, max: 30, unit: 'years' });
const folders = setting('Scanner:MaxFoldersPerSecond', { value: '0', default: '0', min: 1, max: 100_000, zeroLabel: 'No limit', unit: 'folders a second' });
const emailTo = setting('Activity:Alerts:EmailTo', { kind: 'EmailList', values: ['it@yourdomain.local'], optional: true });
const host = setting('Notifications:Smtp:Host', { kind: 'Text', optional: true });
const from = setting('Notifications:Smtp:From', { kind: 'Email', optional: true });
const url = setting('Notifications:PublicUrl', { kind: 'Url', optional: true });
const alerts = setting('Activity:Alerts:Enabled', { kind: 'Toggle', value: 'true', default: 'true' });
const frequency = setting('Schedule:NewServer:Frequency', {
  kind: 'Choice',
  value: 'Daily',
  choices: [
    { value: 'Off', label: 'By hand' },
    { value: 'Daily', label: 'Daily' },
  ],
});
const activityDays = setting('Retention:ActivityDays', { group: 'retention', label: 'File activity', value: '365', min: 30, max: 36_500, zeroLabel: 'Keep forever' });

const schedule = [
  setting('Schedule:NewServer:Frequency', { kind: 'Choice', value: 'Daily', choices: ['Hourly', 'Daily', 'Weekly', 'Off'].map((v) => ({ value: v, label: v })) }),
  setting('Schedule:NewServer:StartHour', { value: '1', max: 23 }),
  setting('Schedule:NewServer:StartMinute', { value: '0', max: 59 }),
  setting('Schedule:NewServer:EveryHours', { optional: true, min: 1, max: 12 }),
  setting('Schedule:NewServer:Day', { kind: 'Choice', optional: true }),
  setting('Schedule:NewServer:StopHour', { value: '7', optional: true, max: 23 }),
];

describe('system settings', () => {
  it('saves only what changed, in the form the API takes', () => {
    const all = [years, folders, emailTo, host];
    expect(changesOf(all, {})).toEqual({});
    // Typing the same value back, or the same addresses differently spaced, is not a change.
    const same = { [years.key]: ' 3 ', [emailTo.key]: '  it@yourdomain.local\n' };
    expect(changesOf(all, same)).toEqual({});
    expect(isChanged(years, same)).toBe(false);
    const draft = { [years.key]: '12', [emailTo.key]: 'it@yourdomain.local, security@yourdomain.local', [host.key]: '' };
    expect(changesOf(all, draft)).toEqual({ [years.key]: '12', [emailTo.key]: ['it@yourdomain.local', 'security@yourdomain.local'] });
    expect(current(years, draft)).toBe('12');
    expect(current(folders, draft)).toBe('0');
    expect(current(emailTo, {})).toBe('it@yourdomain.local');
  });

  it('reads addresses one per line or separated by commas', () => {
    expect(parseList(' a@x.local\n\nb@x.local; c@x.local ,d@x.local ')).toEqual(['a@x.local', 'b@x.local', 'c@x.local', 'd@x.local']);
    expect(parseList('')).toEqual([]);
  });

  it('checks values as the server does', () => {
    expect(problemOf(years, '30')).toBeUndefined();
    expect(problemOf(years, '31')).toMatch(/1 to 30/);
    expect(problemOf(years, '2.5')).toMatch(/whole number/);
    expect(problemOf(years, '')).toMatch(/Fill this in/);
    expect(problemOf(folders, '0')).toBeUndefined();
    expect(problemOf(folders, '100001')).toMatch(/turn on no limit/);
    expect(problemOf(emailTo, 'a@x.local\nA@x.local')).toMatch(/only once/);
    expect(problemOf(emailTo, 'a@x.local, nope')).toMatch(/"nope" is not/);
    expect(problemOf(emailTo, Array.from({ length: 51 }, (_, i) => `u${i}@x.local`).join('\n'))).toMatch(/at most 50/);
    expect(problemOf(emailTo, '')).toBeUndefined();
    expect(problemOf(host, 'mail.yourdomain.local')).toBeUndefined();
    expect(problemOf(host, 'mail:25')).toMatch(/server name/);
    expect(problemOf(from, 'Data Scope <ds@yourdomain.local>')).toMatch(/email address/);
    expect(problemOf(url, 'https://ds01.yourdomain.local/')).toBeUndefined();
    expect(problemOf(url, 'http://ds01.yourdomain.local/')).toMatch(/https/);
    expect(problemOf(url, 'javascript:alert(1)')).toMatch(/https/);
    expect(problemOf(frequency, 'Monthly')).toMatch(/options/);
  });

  it('words values and where they come from plainly', () => {
    expect(valueText(folders, '0')).toBe('No limit');
    expect(valueText(folders, '250')).toBe('250 folders a second');
    expect(valueText(folders, '1')).toBe('1 folder a second');
    expect(valueText(years, '1')).toBe('1 year');
    expect(valueText(years, '12')).toBe('12 years');
    expect(valueText(alerts, 'false')).toBe('Off');
    expect(valueText(frequency, 'Off')).toBe('By hand');
    expect(valueText(emailTo, '')).toBe('Nobody');
    expect(valueText(url, '')).toBe('This server');
    expect(sourceText(years)).toBe('Built-in value');
    expect(sourceText({ ...years, source: 'File' })).toBe('Set in datascope.json');
    expect(sourceText({ ...years, source: 'Here', updatedByName: 'LAB\\admin', updatedAt: '2026-10-11T03:00:00Z', overridesFile: true })).toMatch(
      /^Changed by LAB\\admin, .+\. Overrides datascope.json$/,
    );
    expect(resetText({ ...years, source: 'Here' })).toBe('Go back to the built-in value (3 years)');
    expect(resetText({ ...years, source: 'Here', overridesFile: true })).toBe('Go back to the value in datascope.json');
  });

  it('warns before history is deleted or protection is switched off', () => {
    const all = [activityDays, alerts, frequency, { ...host, value: 'mail.yourdomain.local' }, years];
    expect(warningsOf(all, { [activityDays.key]: '400', [years.key]: '10' })).toEqual([]);
    const w = warningsOf(all, { [activityDays.key]: '90', [alerts.key]: 'false', [frequency.key]: 'Off', [host.key]: '' });
    expect(w).toHaveLength(3);
    expect(w[0]).toMatch(/older than 90 days is deleted at the next clear-up/);
    // Going from keep forever to a number deletes history too; keep forever deletes nothing.
    expect(warningsOf([{ ...activityDays, value: '0' }], { [activityDays.key]: '3650' })).toHaveLength(1);
    expect(warningsOf(all, { [activityDays.key]: '0' })).toEqual([]);
  });

  it('knows when email can be tested', () => {
    expect(emailReady([host, from])).toBe(false);
    expect(emailReady([{ ...host, value: 'mail.yourdomain.local' }, { ...from, value: 'ds@yourdomain.local' }])).toBe(true);
  });

  it('edits the schedule for new servers with the same rules as a server schedule', () => {
    expect(scheduleOf(schedule, {})).toEqual({ frequency: 'Daily', startHour: 1, startMinute: 0, everyHours: null, day: null, stopHour: 7, fullScan: 'Off', fullScanDay: null });
    expect(newServerProblem(schedule, {})).toBeUndefined();

    const weekly = scheduleDraft({ frequency: 'Weekly', startHour: 22, startMinute: 30, day: 'Saturday', stopHour: null });
    expect(weekly).toEqual({
      'Schedule:NewServer:Frequency': 'Weekly',
      'Schedule:NewServer:StartHour': '22',
      'Schedule:NewServer:StartMinute': '30',
      'Schedule:NewServer:StopHour': '',
      'Schedule:NewServer:Day': 'Saturday',
    });
    expect(scheduleOf(schedule, weekly)).toEqual({ frequency: 'Weekly', startHour: 22, startMinute: 30, everyHours: null, day: 'Saturday', stopHour: null, fullScan: 'Off', fullScanDay: null });
    expect(changesOf(schedule, weekly)).toEqual(weekly);

    // Weekly without a day, and hourly without hours apart, can't be saved.
    expect(newServerProblem(schedule, { 'Schedule:NewServer:Frequency': 'Weekly' })).toMatch(/day/);
    expect(newServerProblem(schedule, { 'Schedule:NewServer:Frequency': 'Hourly' })).toMatch(/hours apart/);
    expect(newServerProblem(schedule, { 'Schedule:NewServer:Frequency': 'Hourly', 'Schedule:NewServer:EveryHours': '4', 'Schedule:NewServer:StopHour': '' })).toBeUndefined();
  });

  it('edits the full scans new servers get', () => {
    const withFull = [
      ...schedule,
      setting('Schedule:NewServer:FullScan', { kind: 'Choice', value: 'Weekly', choices: ['Weekly', 'Monthly', 'Off'].map((v) => ({ value: v, label: v })) }),
      setting('Schedule:NewServer:FullScanDay', { kind: 'Choice', value: 'Saturday', optional: true }),
    ];
    expect(scheduleOf(withFull, {})).toMatchObject({ fullScan: 'Weekly', fullScanDay: 'Saturday' });
    expect(newServerProblem(withFull, {})).toBeUndefined();

    const monthly = scheduleDraft({ ...scheduleOf(withFull, {}), fullScan: 'Monthly', fullScanDay: 'Sunday' });
    expect(monthly).toMatchObject({ 'Schedule:NewServer:FullScan': 'Monthly', 'Schedule:NewServer:FullScanDay': 'Sunday' });
    expect(changesOf(withFull, monthly)).toEqual({ 'Schedule:NewServer:FullScan': 'Monthly', 'Schedule:NewServer:FullScanDay': 'Sunday' });

    // By hand needs no day; the day chosen before is kept for switching back.
    const off = scheduleDraft({ ...scheduleOf(withFull, {}), fullScan: 'Off', fullScanDay: null });
    expect(off['Schedule:NewServer:FullScan']).toBe('Off');
    expect('Schedule:NewServer:FullScanDay' in off).toBe(false);
    expect(scheduleOf(withFull, off)).toMatchObject({ fullScan: 'Off', fullScanDay: null });

    // Weekly or monthly without a day can't be saved.
    expect(newServerProblem(withFull, { 'Schedule:NewServer:FullScanDay': '' })).toMatch(/day of the full scan/);
  });
});

describe('basic and advanced settings', () => {
  const g = (id: string) => ({ id });
  const all = ['scanning', 'new-servers', 'activity', 'email', 'cleanup', 'retention', 'directory', 'risks'].map(g);

  it('shows email, the new-server schedule and alerts first, and the rest under Advanced', () => {
    const { basic, advanced } = splitGroups(all);
    expect(basic.map((x) => x.id)).toEqual(['email', 'new-servers', 'activity']);
    expect(advanced.map((x) => x.id)).toEqual(['scanning', 'cleanup', 'retention', 'directory', 'risks']);
  });

  it('never drops a group the server sends, even one it does not know', () => {
    const { basic, advanced } = splitGroups([...all, g('future')]);
    expect(basic.length + advanced.length).toBe(all.length + 1);
    expect(advanced.at(-1)?.id).toBe('future');
  });

  it('copes with a basic group missing', () => {
    expect(splitGroups([g('email'), g('retention')])).toEqual({ basic: [g('email')], advanced: [g('retention')] });
  });

  it('counts advanced settings changed from their default, here or in datascope.json', () => {
    const settings = [
      setting('a', { group: 'retention', source: 'Here' }),
      setting('b', { group: 'scanning', source: 'File' }),
      setting('c', { group: 'scanning', source: 'Default' }),
      setting('d', { group: 'email', source: 'Here' }),
    ];
    expect(changedCount(settings, [g('scanning'), g('retention')])).toBe(2);
    expect(changedCount(settings, [])).toBe(0);
  });
});
