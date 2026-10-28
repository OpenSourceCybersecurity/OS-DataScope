import type { FullScanFrequency, ScanFrequency, ScanScheduleInput, Setting, SettingValue, Weekday } from '../../api/client';
import { fmtDate, fmtNum } from '../../ui/kit';
import { fullScanProblem, scheduleProblem } from './schedule';

/**
 * System settings (D123): the edits an administrator has made and not saved yet, by key. Scalars are text ('' empties an
 * optional setting); address lists are the text of the box, one address per line or separated by commas.
 */
export type Draft = Record<string, string>;

export const SCHEDULE_PREFIX = 'Schedule:NewServer:';
export const EMAIL_KEYS = { host: 'Notifications:Smtp:Host', from: 'Notifications:Smtp:From' } as const;

/** What the box shows for a setting before it is edited. */
export function shownText(s: Setting): string {
  return s.kind === 'EmailList' ? (s.values ?? []).join('\n') : s.value ?? '';
}

/** The value a setting has with the unsaved edits applied. */
export function current(s: Setting, draft: Draft): string {
  return draft[s.key] ?? shownText(s);
}

/** Addresses from the box: one per line, or separated by commas or semicolons. */
export function parseList(text: string): string[] {
  return text
    .split(/[\n,;]+/)
    .map((a) => a.trim())
    .filter((a) => a.length > 0);
}

const sameList = (a: string[], b: string[]) => a.length === b.length && a.every((x, i) => x === b[i]);

/** Whether the edit changes what is in effect. */
export function isChanged(s: Setting, draft: Draft): boolean {
  if (!(s.key in draft)) return false;
  return s.kind === 'EmailList' ? !sameList(parseList(draft[s.key]), s.values ?? []) : draft[s.key].trim() !== (s.value ?? '');
}

/** The settings with edits to save, as the API takes them. */
export function changesOf(settings: Setting[], draft: Draft): Record<string, SettingValue> {
  const out: Record<string, SettingValue> = {};
  for (const s of settings) {
    if (!isChanged(s, draft)) continue;
    out[s.key] = s.kind === 'EmailList' ? parseList(draft[s.key]) : draft[s.key].trim();
  }
  return out;
}

const EMAIL = /^[^\s@<>,;"]+@[^\s@<>,;"]+\.[^\s@<>,;"]+$/;

/** Why the server would refuse a value, or undefined. Mirrors SystemSettings.Problem for quick feedback; the server checks again. */
export function problemOf(s: Setting, text: string): string | undefined {
  if (s.kind === 'EmailList') {
    const list = parseList(text);
    if (list.length > 50) return 'Add at most 50 addresses.';
    const bad = list.find((a) => !EMAIL.test(a) || a.length > 254);
    if (bad) return `"${bad.length > 60 ? bad.slice(0, 60) + '…' : bad}" is not an email address.`;
    return new Set(list.map((a) => a.toLowerCase())).size === list.length ? undefined : 'Each address only once.';
  }
  const v = text.trim();
  if (v === '') return s.optional ? undefined : 'Fill this in.';
  switch (s.kind) {
    case 'Whole': {
      if (!/^\d+$/.test(v)) return 'Enter a whole number.';
      const n = Number(v);
      if (n === 0 && s.zeroLabel) return undefined;
      return n < s.min || n > s.max ? `Enter ${fmtNum(s.min)} to ${fmtNum(s.max)}${s.zeroLabel ? `, or turn on ${s.zeroLabel.toLowerCase()}` : ''}.` : undefined;
    }
    case 'Toggle':
      return v === 'true' || v === 'false' ? undefined : 'Use on or off.';
    case 'Choice':
      return s.choices.some((c) => c.value === v) ? undefined : 'Choose one of the options.';
    case 'Email':
      return EMAIL.test(v) && v.length <= 254 ? undefined : 'Enter an email address, for example datascope@yourdomain.local.';
    case 'Url': {
      try {
        const u = new URL(v);
        return u.protocol === 'https:' && !u.username && !u.password && !u.search && !u.hash ? undefined : 'Enter an https:// address, for example https://ds01.yourdomain.local/.';
      } catch {
        return 'Enter an https:// address, for example https://ds01.yourdomain.local/.';
      }
    }
    case 'Text':
      return /[\s/\\@:]/.test(v) || v.length > 253 ? 'Enter a server name, for example mail.yourdomain.local.' : undefined;
  }
}

/** A value as people read it: "No limit", "Every 5 minutes", "On", "3 years". */
export function valueText(s: Setting, text: string): string {
  if (s.kind === 'EmailList') {
    const list = parseList(text);
    return list.length ? list.join(', ') : 'Nobody';
  }
  if (text === '') return s.kind === 'Url' ? 'This server' : 'Not set';
  if (s.kind === 'Toggle') return text === 'true' ? 'On' : 'Off';
  if (s.kind === 'Choice') return s.choices.find((c) => c.value === text)?.label ?? text;
  if (s.kind === 'Whole') {
    if (text === '0' && s.zeroLabel) return s.zeroLabel;
    const n = Number(text);
    return s.unit ? `${fmtNum(n)} ${n === 1 ? s.unit.replace(/^(\w+?)s\b/, '$1') : s.unit}` : String(n);
  }
  return text;
}

/** Where the value in effect comes from, in plain words. */
export function sourceText(s: Setting): string {
  if (s.source === 'Here') {
    const who = s.updatedByName ? `Changed by ${s.updatedByName}` : 'Changed here';
    const when = s.updatedAt ? `, ${fmtDate(s.updatedAt)}` : '';
    return `${who}${when}${s.overridesFile ? '. Overrides datascope.json' : ''}`;
  }
  return s.source === 'File' ? 'Set in datascope.json' : 'Built-in value';
}

/** What going back from a saved value gives: the datascope.json value or the built-in one. */
export function resetText(s: Setting): string {
  return s.overridesFile ? 'Go back to the value in datascope.json' : `Go back to the built-in value (${valueText(s, s.kind === 'EmailList' ? '' : s.default ?? '')})`;
}

/**
 * Warnings shown before a change is saved: changes that delete history or switch off protection. Everything else is
 * routine. Returns one line per warning.
 */
export function warningsOf(settings: Setting[], draft: Draft): string[] {
  const out: string[] = [];
  for (const s of settings) {
    if (!isChanged(s, draft)) continue;
    const next = draft[s.key].trim();
    const was = s.value ?? '';
    if (s.group === 'retention' && s.key !== 'Retention:IntervalHours' && next !== '0' && (was === '0' || Number(next) < Number(was))) {
      out.push(`${s.label}: history older than ${fmtNum(Number(next))} days is deleted at the next clear-up and can't be brought back.`);
    }
    if (s.key === 'Activity:Alerts:Enabled' && next === 'false') out.push('Raise alerts: mass deletes, refused access and permission changes will no longer raise alerts.');
    if (s.key === EMAIL_KEYS.host && next === '' && was !== '') out.push('Email is turned off: review emails, reminders and alerts are not sent. Reviews still wait in Data Scope.');
  }
  return out;
}

/** Whether saved settings are enough to send email: the mail server's name and a From address. */
export function emailReady(settings: Setting[]): boolean {
  const get = (k: string) => settings.find((s) => s.key === k)?.value;
  return !!get(EMAIL_KEYS.host) && !!get(EMAIL_KEYS.from);
}

// ---------- The schedule new servers get ----------

const WEEKDAY_SET = new Set(['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']);

/** The schedule for new servers from the settings and edits. Missing or bad numbers come out as -1 so the schedule check catches them. */
export function scheduleOf(settings: Setting[], draft: Draft): ScanScheduleInput {
  const get = (name: string) => {
    const s = settings.find((x) => x.key === SCHEDULE_PREFIX + name);
    return s ? current(s, draft).trim() : '';
  };
  const num = (name: string) => (/^\d+$/.test(get(name)) ? Number(get(name)) : null);
  const frequency = (['Off', 'Hourly', 'Daily', 'Weekly'].includes(get('Frequency')) ? get('Frequency') : 'Daily') as ScanFrequency;
  const day = WEEKDAY_SET.has(get('Day')) ? (get('Day') as Weekday) : null;
  const fullScan = (['Off', 'Weekly', 'Monthly'].includes(get('FullScan')) ? get('FullScan') : 'Off') as FullScanFrequency;
  const fullScanDay = WEEKDAY_SET.has(get('FullScanDay')) ? (get('FullScanDay') as Weekday) : null;
  return {
    frequency,
    startHour: num('StartHour') ?? -1,
    startMinute: num('StartMinute') ?? -1,
    everyHours: frequency === 'Hourly' ? num('EveryHours') : null,
    day: frequency === 'Weekly' ? day : null,
    stopHour: num('StopHour'),
    fullScan,
    fullScanDay: fullScan === 'Off' ? null : fullScanDay,
  };
}

/** The edits that give new servers this schedule. Hours apart and day are kept when not in use, so switching back restores them. */
export function scheduleDraft(input: ScanScheduleInput): Draft {
  const d: Draft = {
    [SCHEDULE_PREFIX + 'Frequency']: input.frequency,
    [SCHEDULE_PREFIX + 'StartHour']: String(Math.max(0, input.startHour)),
    [SCHEDULE_PREFIX + 'StartMinute']: String(Math.max(0, input.startMinute ?? 0)),
    [SCHEDULE_PREFIX + 'StopHour']: input.stopHour == null ? '' : String(input.stopHour),
  };
  if (input.everyHours != null) d[SCHEDULE_PREFIX + 'EveryHours'] = String(input.everyHours);
  if (input.day != null) d[SCHEDULE_PREFIX + 'Day'] = input.day;
  if (input.fullScan) d[SCHEDULE_PREFIX + 'FullScan'] = input.fullScan;
  if (input.fullScanDay != null) d[SCHEDULE_PREFIX + 'FullScanDay'] = input.fullScanDay;
  return d;
}

/** Why the schedule for new servers can't be saved, or undefined. */
export function newServerProblem(settings: Setting[], draft: Draft): string | undefined {
  const input = scheduleOf(settings, draft);
  return scheduleProblem(input) ?? fullScanProblem(input);
}

/**
 * The setting groups most organisations set once at install: shown first. The rest suit nearly everyone at their
 * defaults and sit under Advanced, so the screen starts with three groups rather than eight.
 */
export const BASIC_GROUPS = ['email', 'new-servers', 'activity'] as const;

/** Splits groups into the basics, in BASIC_GROUPS order, and the advanced rest in the server's order. */
export function splitGroups<G extends { id: string }>(groups: G[]): { basic: G[]; advanced: G[] } {
  const basic = BASIC_GROUPS.map((id) => groups.find((g) => g.id === id)).filter((g): g is G => !!g);
  return { basic, advanced: groups.filter((g) => !basic.includes(g)) };
}

/** How many advanced settings no longer have their default, saved here or in datascope.json. */
export function changedCount(settings: Setting[], groups: { id: string }[]): number {
  const ids = new Set(groups.map((g) => g.id));
  return settings.filter((s) => ids.has(s.group) && s.source !== 'Default').length;
}
