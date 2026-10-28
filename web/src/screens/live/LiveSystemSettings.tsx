import { useEffect, useMemo, useState } from 'react';
import { Check, ChevronRight, Loader2, Mail, RotateCcw, Save } from 'lucide-react';
import { ApiError, api, type Setting, type SettingGroup, type SettingValue, type SystemSettingsInfo, type TestEmail, type Weekday, type ScanFrequency } from '../../api/client';
import { canManage, useBackend } from '../../api/backend';
import { useNav } from '../../App';
import { Button, Callout, Card, LinkArrow, PageHeader, Segmented, Tag, cx, filterCls, inputCls } from '../../ui/kit';
import { HOUR_STEPS, WEEKDAYS, fullScanText, hourLabel, parseTime, scheduleText, startsText, timeValue } from './schedule';
import { FullScanPicker } from './FullScanPicker';
import {
  SCHEDULE_PREFIX,
  changedCount,
  changesOf,
  current,
  emailReady,
  isChanged,
  newServerProblem,
  problemOf,
  resetText,
  scheduleDraft,
  scheduleOf,
  sourceText,
  splitGroups,
  valueText,
  warningsOf,
  type Draft,
} from './settings';
import { useData } from './useData';

/**
 * System settings (D123): the operational settings an administrator can change without editing datascope.json, one
 * card per group, each saved on its own. Auditors see the same screen without the controls. Per-server schedules stay on
 * Servers & scans; sign-in roles, the database, HTTPS and the mail server's password stay with the setup tool.
 */
export default function LiveSystemSettings() {
  const admin = canManage(useBackend());
  const loaded = useData(() => api.systemSettings(), []);
  const [info, setInfo] = useState<SystemSettingsInfo>();
  useEffect(() => setInfo(loaded.data), [loaded.data]);
  const groups = info?.groups ?? [];
  // Email, the new-server schedule and alerts come first; the rest stay folded under Advanced until opened.
  const { basic, advanced } = splitGroups(groups);
  const changed = info ? changedCount(info.settings, advanced) : 0;

  return (
    <>
      <PageHeader
        title="System settings"
        sub="Where emails come from, when new servers are scanned and who hears about alerts. Everything else has sensible defaults under Advanced. Changes are recorded in the audit log and apply without restarting anything."
      />
      {loaded.error && <Callout tone="bad" title="Can't load the settings">{loaded.error}</Callout>}
      {!info && !loaded.error && <p className="text-[13px] text-ink-3 flex items-center gap-2"><Loader2 size={14} className="animate-spin" /> Loading…</p>}
      {info && (
        <div className="space-y-4">
          {!admin && <Callout title="View only">Only Data Scope administrators can change these settings.</Callout>}
          {basic.map((g) => (
            <GroupCard key={g.id} group={g} info={info} admin={admin} onSaved={setInfo} />
          ))}
          {advanced.length > 0 && (
            <details className="group/adv rounded-xl border border-line bg-surface">
              <summary className="cursor-pointer list-none px-4 py-3 flex flex-wrap items-center gap-x-2 gap-y-1 [&::-webkit-details-marker]:hidden">
                <ChevronRight size={16} aria-hidden className="text-ink-3 transition-transform group-open/adv:rotate-90" />
                <span className="text-[13px] font-semibold">Advanced</span>
                <span className="text-[12.5px] text-ink-3">{advanced.map((g) => g.title).join(', ')}. The defaults suit most organisations.</span>
                {changed > 0 && <Tag tone="accent">{changed === 1 ? '1 changed from the default' : `${changed} changed from the default`}</Tag>}
              </summary>
              <div className="px-4 pb-4 space-y-4">
                {advanced.map((g) => (
                  <GroupCard key={g.id} group={g} info={info} admin={admin} onSaved={setInfo} />
                ))}
                <Card title="Not on this screen">
                  <ul className="text-[13px] text-ink-2 space-y-1 list-disc pl-5 max-w-3xl">
                    <li>Each server's own scan schedule, and which shares are scanned: on Servers & scans and File shares.</li>
                    <li>Who has which Data Scope role, the database connection, the HTTPS certificate and port: chosen with the setup tool.</li>
                    <li>The mail server's sign-in and password, and STARTTLS (always on): datascope.json, set with the setup tool.</li>
                    <li>The scanner's safety limits for large, nested or damaged files: built in and kept out of reach on purpose.</li>
                  </ul>
                </Card>
              </div>
            </details>
          )}
        </div>
      )}
    </>
  );
}

/** One group of settings with its own Save, so a change in one place never saves edits made elsewhere. */
export function GroupCard({ group, info, admin, onSaved }: { group: SettingGroup; info: SystemSettingsInfo; admin: boolean; onSaved: (info: SystemSettingsInfo) => void }) {
  const settings = useMemo(() => info.settings.filter((s) => s.group === group.id), [info, group.id]);
  const [draft, setDraft] = useState<Draft>({});
  const [busy, setBusy] = useState<string>();
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [problem, setProblem] = useState<string>();
  const [saved, setSaved] = useState(false);
  const schedule = group.id === 'new-servers';

  const edit = (patch: Draft) => {
    setDraft((d) => ({ ...d, ...patch }));
    setSaved(false);
  };
  const changes = changesOf(settings, draft);
  const changed = Object.keys(changes).length;
  const problems = Object.fromEntries(settings.map((s) => [s.key, problemOf(s, current(s, draft))]).filter(([, p]) => p)) as Record<string, string>;
  const scheduleIssue = schedule ? newServerProblem(settings, draft) : undefined;
  const blocked = Object.keys(problems).length > 0 || !!scheduleIssue;
  const email = group.id === 'email';
  const warnings = warningsOf(settings, draft);

  const send = async (key: string, values: Record<string, SettingValue>) => {
    setBusy(key);
    setProblem(undefined);
    setErrors({});
    try {
      onSaved(await api.saveSystemSettings(values));
      setDraft((d) => Object.fromEntries(Object.entries(d).filter(([k]) => !(k in values))));
      setSaved(true);
    } catch (e) {
      if (e instanceof ApiError && Object.keys(e.errors).length) setErrors(e.errors);
      else setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(undefined);
    }
  };
  const save = (e: React.FormEvent) => {
    e.preventDefault();
    if (changed && !blocked) void send('save', changes);
  };
  const serverError = (key: string) => errors[key]?.[0] ?? (key.startsWith(SCHEDULE_PREFIX) ? errors['Schedule:NewServer']?.[0] : undefined);
  const otherErrors = Object.entries(errors).filter(([k]) => !settings.some((s) => s.key === k) && !(schedule && k === 'Schedule:NewServer'));

  return (
    <Card id={`settings-${group.id}`} title={group.title}>
      <p className="text-[13px] text-ink-2 max-w-3xl">{group.description}</p>
      <form onSubmit={save} aria-label={group.title}>
        {schedule ? (
          <NewServerSchedule
            settings={settings}
            draft={draft}
            admin={admin}
            busy={!!busy}
            timeZone={info.timeZone}
            problem={scheduleIssue ?? errors['Schedule:NewServer']?.[0] ?? settings.map((x) => errors[x.key]?.[0]).find(Boolean)}
            onEdit={edit}
            onReset={() => void send('reset', Object.fromEntries(settings.filter((x) => x.source === 'Here').map((x) => [x.key, null])))}
          />
        ) : (
          <ul className="mt-3 divide-y divide-line border-y border-line">
            {settings.map((s) => (
              <SettingRow
                key={s.key}
                s={s}
                text={current(s, draft)}
                changed={isChanged(s, draft)}
                problem={problems[s.key] ?? serverError(s.key)}
                admin={admin}
                busy={!!busy}
                onEdit={(v) => edit({ [s.key]: v })}
                onReset={() => void send(`reset:${s.key}`, { [s.key]: null })}
              />
            ))}
          </ul>
        )}
        {email && (
          <>
            {info.emailSignIn && <p className="mt-3 text-[12.5px] text-ink-3">The mail server sign-in is set in datascope.json with the setup tool; it isn't shown here.</p>}
            {admin && (
              <TestEmailForm ready={emailReady(info.settings)} unsaved={changed > 0} />
            )}
          </>
        )}
        {warnings.length > 0 && (
          <div className="mt-3">
            <Callout tone="warn" title="Before you save">
              <ul className="list-disc pl-5 space-y-0.5">{warnings.map((w) => <li key={w}>{w}</li>)}</ul>
            </Callout>
          </div>
        )}
        {problem && <div className="mt-3"><Callout tone="bad" title="That didn't save">{problem}</Callout></div>}
        {otherErrors.length > 0 && <div className="mt-3"><Callout tone="bad" title="That didn't save">{otherErrors.map(([, m]) => m[0]).join(' ')}</Callout></div>}
        {admin && (
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <Button type="submit" variant="primary" size="sm" disabled={!changed || blocked || !!busy}>
              {busy === 'save' ? <Loader2 size={14} className="animate-spin" /> : <Save size={14} />} {changed > 1 ? `Save ${changed} changes` : 'Save'}
            </Button>
            {changed > 0 && (
              <Button size="sm" variant="ghost" disabled={!!busy} onClick={() => (setDraft({}), setErrors({}))}>
                Undo changes
              </Button>
            )}
            {saved && !changed && <span className="text-[12.5px] text-[var(--s-good)] inline-flex items-center gap-1" role="status"><Check size={14} /> Saved</span>}
          </div>
        )}
      </form>
    </Card>
  );
}

/** One setting: what it is, where its value comes from, and its control. */
function SettingRow({ s, text, changed, problem, admin, busy, onEdit, onReset }: {
  s: Setting;
  text: string;
  changed: boolean;
  problem?: string;
  admin: boolean;
  busy: boolean;
  onEdit: (text: string) => void;
  onReset: () => void;
}) {
  const id = `setting-${s.key.replace(/[^A-Za-z0-9]/g, '-')}`;
  return (
    <li className="py-3 grid gap-2 md:grid-cols-[minmax(0,1fr)_minmax(0,20rem)] md:gap-6">
      <div className="min-w-0">
        <label htmlFor={id} className="text-[13.5px] font-medium flex flex-wrap items-center gap-2">
          {s.label}
          {changed && <Tag tone="accent">Not saved</Tag>}
        </label>
        <p className="text-[12.5px] text-ink-2 mt-0.5">{s.help}</p>
        <p className="text-[12px] text-ink-3 mt-1 flex flex-wrap items-center gap-x-2 gap-y-1">
          <span>{sourceText(s)}.</span>
          <span>{s.applies}</span>
          {admin && s.source === 'Here' && !changed && (
            <button type="button" disabled={busy} onClick={onReset} title={resetText(s)} className="inline-flex items-center gap-1 text-accent hover:underline disabled:opacity-50">
              <RotateCcw size={12} /> Reset
            </button>
          )}
        </p>
      </div>
      <div className="min-w-0">
        <Control id={id} s={s} text={text} admin={admin} invalid={!!problem} onEdit={onEdit} />
        {problem && <p className="mt-1 text-[12.5px] text-[var(--s-critical)]" role="alert">{problem}</p>}
      </div>
    </li>
  );
}

function Control({ id, s, text, admin, invalid, onEdit }: { id: string; s: Setting; text: string; admin: boolean; invalid: boolean; onEdit: (text: string) => void }) {
  if (!admin) return <div id={id} className="text-[13.5px] break-words">{valueText(s, text)}</div>;
  const describe = invalid ? { 'aria-invalid': true } : {};
  switch (s.kind) {
    case 'Toggle':
      return (
        <label className="inline-flex items-center gap-2 text-[13.5px] cursor-pointer">
          <input id={id} type="checkbox" className="size-4 accent-[var(--accent)]" checked={text === 'true'} onChange={(e) => onEdit(e.target.checked ? 'true' : 'false')} />
          {text === 'true' ? 'On' : 'Off'}
        </label>
      );
    case 'Choice':
      return (
        <select id={id} className={inputCls} value={text} onChange={(e) => onEdit(e.target.value)} {...describe}>
          {s.optional && <option value="">Not set</option>}
          {s.choices.map((c) => <option key={c.value} value={c.value}>{c.label}</option>)}
        </select>
      );
    case 'EmailList':
      return (
        <textarea
          id={id}
          rows={3}
          className={cx(inputCls, 'h-auto py-2 leading-snug')}
          value={text}
          placeholder="it@yourdomain.local, one per line"
          onChange={(e) => onEdit(e.target.value)}
          {...describe}
        />
      );
    case 'Whole':
      return <WholeNumber id={id} s={s} text={text} onEdit={onEdit} invalid={invalid} />;
    default:
      return (
        <input
          id={id}
          className={inputCls}
          value={text}
          type={s.kind === 'Email' ? 'email' : s.kind === 'Url' ? 'url' : 'text'}
          inputMode={s.kind === 'Url' ? 'url' : undefined}
          maxLength={253}
          placeholder={s.kind === 'Url' ? 'https://this-server/' : s.optional ? 'Not set' : undefined}
          onChange={(e) => onEdit(e.target.value)}
          {...describe}
        />
      );
  }
}

/** A number with its unit; where 0 means something ("No limit", "Keep forever") that is a checkbox, not a number to type. */
function WholeNumber({ id, s, text, invalid, onEdit }: { id: string; s: Setting; text: string; invalid: boolean; onEdit: (text: string) => void }) {
  const zero = !!s.zeroLabel && text.trim() === '0';
  const fallback = s.default && s.default !== '0' ? s.default : String(s.min);
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
      {!zero && (
        <span className="inline-flex items-center gap-2">
          <input
            id={id}
            className={cx(filterCls, 'w-28 tnum')}
            inputMode="numeric"
            value={text}
            onChange={(e) => onEdit(e.target.value)}
            aria-invalid={invalid || undefined}
            placeholder={s.optional ? 'Not set' : undefined}
          />
          {s.unit && <span className="text-[13px] text-ink-2">{s.unit}</span>}
        </span>
      )}
      {s.zeroLabel && (
        <label className="inline-flex items-center gap-2 text-[13px] cursor-pointer">
          <input id={zero ? id : undefined} type="checkbox" className="size-4 accent-[var(--accent)]" checked={zero} onChange={(e) => onEdit(e.target.checked ? '0' : fallback)} />
          {s.zeroLabel}
        </label>
      )}
    </div>
  );
}

/** The schedule new servers get, with the same choices as a server's own schedule on Servers & scans. */
function NewServerSchedule({ settings, draft, admin, busy, timeZone, problem, onEdit, onReset }: {
  settings: Setting[];
  draft: Draft;
  admin: boolean;
  busy: boolean;
  timeZone: string;
  problem?: string;
  onEdit: (patch: Draft) => void;
  onReset: () => void;
}) {
  const { go } = useNav();
  const input = scheduleOf(settings, draft);
  const hourly = input.frequency === 'Hourly';
  const changed = settings.some((s) => isChanged(s, draft));
  const saved = settings.filter((s) => s.source !== 'Default');
  const set = (patch: Partial<typeof input>) => onEdit(scheduleDraft({ ...input, ...patch }));
  const choose = (f: ScanFrequency) =>
    // As on Servers & scans: a nightly pause would cut scans every few hours down to a couple, so they run around the clock.
    set({ frequency: f, stopHour: f === 'Hourly' && input.frequency !== 'Hourly' ? null : input.stopHour, everyHours: f === 'Hourly' ? input.everyHours ?? 4 : null, day: f === 'Weekly' ? input.day ?? 'Saturday' : null });
  const hourOptions = Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{hourLabel(h)}</option>);
  const summary = input.frequency === 'Off' ? 'Only when started by hand' : problem ? undefined : hourly ? `Scans start at ${startsText(input)}` : scheduleText(input);

  return (
    <div className="mt-3 space-y-3 text-[13px]">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">New servers are scanned:</span>
        <span>{summary ?? '…'}</span>
        {changed && <Tag tone="accent">Not saved</Tag>}
      </div>
      {admin && (
        <div className="rounded-lg border border-line bg-surface-2 p-3 space-y-3">
          <Segmented<ScanFrequency>
            value={input.frequency}
            onChange={choose}
            options={[
              { id: 'Hourly', label: 'Hourly' },
              { id: 'Daily', label: 'Daily' },
              { id: 'Weekly', label: 'Weekly' },
              { id: 'Off', label: 'By hand' },
            ]}
          />
          {input.frequency !== 'Off' && (
            <div className="flex flex-wrap items-end gap-3">
              {hourly && (
                <label className="block" htmlFor="ns-every">
                  <span className="block text-[13px] font-medium mb-1">Every</span>
                  <select id="ns-every" className={filterCls} value={input.everyHours ?? ''} onChange={(e) => set({ everyHours: Number(e.target.value) })}>
                    {input.everyHours == null && <option value="">Choose</option>}
                    {HOUR_STEPS.map((h) => <option key={h} value={h}>{h === 1 ? '1 hour' : `${h} hours`}</option>)}
                  </select>
                </label>
              )}
              {input.frequency === 'Weekly' && (
                <label className="block" htmlFor="ns-day">
                  <span className="block text-[13px] font-medium mb-1">Day</span>
                  <select id="ns-day" className={filterCls} value={input.day ?? ''} onChange={(e) => set({ day: e.target.value as Weekday })}>
                    {input.day == null && <option value="">Choose</option>}
                    {WEEKDAYS.map((d) => <option key={d} value={d}>{d}</option>)}
                  </select>
                </label>
              )}
              <label className="block" htmlFor="ns-start">
                <span className="block text-[13px] font-medium mb-1">{hourly ? 'Starting at' : 'Start at'}</span>
                <input
                  id="ns-start"
                  type="time"
                  step={60}
                  required
                  className={filterCls}
                  value={input.startHour >= 0 ? timeValue(input.startHour, Math.max(0, input.startMinute ?? 0)) : ''}
                  onChange={(e) => {
                    const t = parseTime(e.target.value);
                    if (t) set({ startHour: t.hour, startMinute: t.minute });
                  }}
                />
              </label>
              <label className="block" htmlFor="ns-stop">
                <span className="block text-[13px] font-medium mb-1">{hourly ? 'Until' : 'Pause at, if not finished'}</span>
                <select id="ns-stop" className={filterCls} value={input.stopHour ?? ''} onChange={(e) => set({ stopHour: e.target.value === '' ? null : Number(e.target.value) })}>
                  <option value="">{hourly ? 'Around the clock' : "Don't pause; run until done"}</option>
                  {hourOptions}
                </select>
              </label>
            </div>
          )}
          <FullScanPicker
            id="ns-full-day"
            frequency={input.fullScan ?? 'Weekly'}
            day={input.fullScanDay ?? null}
            onChange={(fullScan, fullScanDay) => set({ fullScan, fullScanDay })}
          />
          {problem && <p className="text-[12.5px] text-[var(--s-critical)]" role="alert">{problem}</p>}
        </div>
      )}
      {!problem && <div className="text-ink-2">{fullScanText(input, input)}.</div>}
      <p className="text-[12px] text-ink-3">
        Times are in the Data Scope server's time zone: {timeZone}. {settings[0]?.applies}{' '}
        {saved.length > 0 ? `${sourceText(saved[0])}.` : 'Built-in: every night at 1:00 AM, pausing at 7:00 AM, with a full scan every Saturday.'} Servers already added keep their own schedule.
      </p>
      <LinkArrow onClick={() => go('setup')}>Change an existing server's schedule on Servers & scans</LinkArrow>
      {admin && saved.some((x) => x.source === 'Here') && !changed && (
        <button type="button" disabled={busy} onClick={onReset} className="block text-[12.5px] text-accent hover:underline disabled:opacity-50">
          <RotateCcw size={12} className="inline -mt-0.5" /> Go back to the {saved.some((x) => x.overridesFile) ? 'schedule in datascope.json' : 'built-in schedule'}
        </button>
      )}
    </div>
  );
}

/** Sends one email through the saved mail server settings and shows what happened. */
function TestEmailForm({ ready, unsaved }: { ready: boolean; unsaved: boolean }) {
  const [to, setTo] = useState('');
  const [sending, setSending] = useState(false);
  const [result, setResult] = useState<TestEmail>();
  const [problem, setProblem] = useState<string>();

  useEffect(() => {
    if (!result || result.state !== 'Pending') return;
    let tries = 0;
    const t = setInterval(async () => {
      tries++;
      try {
        const next = await api.testEmail(result.id);
        if (next.state !== 'Pending' || next.error !== result.error) setResult(next);
      } catch {
        /* keep the last state */
      }
      if (tries >= 60) clearInterval(t);
    }, 3000);
    return () => clearInterval(t);
  }, [result]);

  const send = async () => {
    setSending(true);
    setProblem(undefined);
    setResult(undefined);
    try {
      setResult(await api.sendTestEmail(to.trim()));
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setSending(false);
    }
  };
  const disabled = !ready || unsaved || sending || to.trim() === '';
  return (
    <div className="mt-4 rounded-lg border border-line bg-surface-2 p-3 space-y-2">
      <div className="text-[13px] font-medium">Send a test email</div>
      <div className="flex flex-wrap items-center gap-2">
        <input
          aria-label="Test email address"
          type="email"
          className={cx(filterCls, 'w-72')}
          placeholder="you@yourdomain.local"
          value={to}
          onChange={(e) => setTo(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && (e.preventDefault(), !disabled && void send())}
        />
        <Button size="sm" disabled={disabled} onClick={() => void send()}>
          {sending ? <Loader2 size={14} className="animate-spin" /> : <Mail size={14} />} Send test
        </Button>
      </div>
      <p className="text-[12px] text-ink-3" role="status">
        {!ready
          ? 'Save a mail server and From address first.'
          : unsaved
            ? 'Save your changes first; the test uses the saved settings.'
            : result
              ? testText(result)
              : 'Goes through your mail server only. The scanner service sends it within a minute.'}
      </p>
      {problem && <p className="text-[12.5px] text-[var(--s-critical)]">{problem}</p>}
    </div>
  );
}

function testText(t: TestEmail): string {
  switch (t.state) {
    case 'Sent':
      return 'Sent. If it does not arrive, check your junk mail folder and the mail server logs.';
    case 'Skipped':
      return `Not sent: ${t.error ?? 'email is not set up.'}`;
    case 'Failed':
      return `It failed: ${t.error ?? 'it was refused.'}`;
    default:
      return t.error ? `The last try failed: ${t.error} Trying again.` : 'Waiting for the scanner service to send it…';
  }
}
