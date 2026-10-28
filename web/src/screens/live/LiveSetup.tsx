import { useState } from 'react';
import { AlertTriangle, CalendarClock, HardDrive, Loader2, Pause, Play, Plus, ScanLine, ShieldCheck, Square, Trash2 } from 'lucide-react';
import { api, type FullScanFrequency, type ScanFrequency, type ScanJob, type ScanKind, type ScanScheduleInput, type Server, type Weekday } from '../../api/client';
import { canManage, useBackend } from '../../api/backend';
import { Button, Callout, Card, Field, Meter, PageHeader, Segmented, Tag, Td, Th, ago, filterCls, fmtDateTime, fmtNum, inputCls } from '../../ui/kit';
import { HOUR_STEPS, WEEKDAYS, fullScanProblem, fullScanText, hourLabel, jobOrigin, kindLabel, parseTime, scheduleProblem, scheduleText, startsText, timeValue, windowHours } from './schedule';
import { FullScanPicker } from './FullScanPicker';
import { useData } from './useData';
import { DirectoryStrip } from './LiveAccess';

const ACTIVE = new Set(['Queued', 'Running', 'Paused']);

export default function LiveSetup() {
  const backend = useBackend();
  const admin = canManage(backend);
  const servers = useData(() => api.servers(true), []);
  const jobs = useData(() => api.jobs(undefined, 50), [], 2000);
  const [adding, setAdding] = useState(false);
  const [busy, setBusy] = useState<string>();
  const [problem, setProblem] = useState<string>();

  const act = async (key: string, fn: () => Promise<unknown>) => {
    setBusy(key);
    setProblem(undefined);
    try {
      await fn();
      await Promise.all([jobs.reload(), servers.reload()]);
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(undefined);
    }
  };

  const everyServer = servers.data?.items ?? [];
  const list = everyServer.filter((s) => s.state !== 'Retired');
  const fileServers = list;
  const removed = everyServer.filter((s) => s.state === 'Retired');
  const allJobs = jobs.data?.items ?? [];
  const latest = (id: string) => allJobs.find((j) => j.serverId === id);

  return (
    <>
      <PageHeader
        title="Servers & scans"
        sub="Your Windows file servers and Active Directory. Scans only read; they never change, move or delete files. Each server can be scanned on a schedule, so risks and reviews stay current."
        actions={admin && <Button variant="primary" onClick={() => setAdding(true)}><Plus size={16} /> Add file server</Button>}
      />
      {problem && <div className="mb-4"><Callout tone="bad" title="That didn't work">{problem}</Callout></div>}
      {(servers.error || jobs.error) && <div className="mb-4"><Callout tone="bad" title="Can't reach the Data Scope server">{servers.error ?? jobs.error}</Callout></div>}
      {adding && <AddServer onDone={async () => { setAdding(false); await servers.reload(); }} onCancel={() => setAdding(false)} />}

      {servers.data && fileServers.length === 0 && !adding && (
        <Card>
          <div className="text-center py-8">
            <HardDrive className="mx-auto text-ink-3" size={28} />
            <div className="font-medium mt-2">No file servers yet</div>
            <p className="text-ink-2 text-[13.5px] mt-1 max-w-md mx-auto">Add a Windows file server by its host name. Data Scope lists its shares on the first scan.</p>
            {admin && <div className="mt-4"><Button variant="primary" onClick={() => setAdding(true)}><Plus size={16} /> Add file server</Button></div>}
          </div>
        </Card>
      )}

      <div className="eyebrow mb-2">Active Directory</div>
      <DirectoryStrip />

      {fileServers.length > 0 && <div className="eyebrow mb-2 flex items-center gap-1.5"><HardDrive size={13} aria-hidden /> File servers</div>}
      <div className="grid lg:grid-cols-2 gap-4 mb-4">
        {fileServers.map((sv) => (
          <ServerCard key={sv.id} server={sv} job={latest(sv.id)} admin={admin} busy={busy} act={act} />
        ))}
      </div>

      {removed.length > 0 && (
        <p className="text-[12.5px] text-ink-3 mb-4">
          Removed: {removed.map((s) => s.hostname).join(', ')}. Their history is kept. Add one again by name to bring it back.
        </p>
      )}

      <RecentScans jobs={allJobs} servers={everyServer} />
    </>
  );
}

/** The latest scans of the servers given, newest first. */
export function RecentScans({ jobs, servers }: { jobs: ScanJob[]; servers: Server[] }) {
  if (!jobs.length) return null;
  return (
    <Card title="Recent scans" pad={false}>
      <div className="overflow-x-auto">
        <table className="w-full text-[13px] min-w-[640px]">
          <thead><tr><Th>Server</Th><Th>Scan</Th><Th>Status</Th><Th className="text-right">Folders listed</Th><Th>Started</Th><Th>Finished</Th></tr></thead>
          <tbody>
            {jobs.map((j) => (
              <tr key={j.id} className="border-t border-line">
                <Td className="font-medium">{servers.find((s) => s.id === j.serverId)?.hostname ?? '…'}</Td>
                <Td className="text-ink-2 whitespace-nowrap">{j.kind === 'Full' ? 'Full' : 'Quick'}</Td>
                <Td><JobTag job={j} />{jobOrigin(j) && <span className="ml-2 text-[12px] text-ink-3">{jobOrigin(j)}</span>}</Td>
                <Td className="text-right tnum">{fmtNum(j.itemsDone)}</Td>
                <Td className="text-ink-2">{j.startedAt ? fmtDateTime(j.startedAt) : '—'}</Td>
                <Td className="text-ink-2">{j.finishedAt ? fmtDateTime(j.finishedAt) : '—'}</Td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}

/** A file server, with its schedule, latest scan and scan buttons. */
export function ServerCard({ server, job, admin, busy, act }: {
  server: Server;
  job?: ScanJob;
  admin: boolean;
  busy?: string;
  act: (key: string, fn: () => Promise<unknown>) => Promise<void>;
}) {
  const active = job && ACTIVE.has(job.state);
  const progress = job && job.itemsTotal > 0 ? job.itemsDone / job.itemsTotal : 0;
  const [confirming, setConfirming] = useState(false);
  const [editing, setEditing] = useState(false);
  const sched = server.schedule;
  return (
    <Card>
      <div className="flex items-start gap-3">
        <div className="size-10 rounded-lg bg-surface-2 grid place-items-center text-ink-2 shrink-0"><HardDrive size={20} /></div>
        <div className="flex-1 min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-medium break-all">{server.hostname}</span>
            {server.state === 'Enrolled' && <Tag tone="good"><ShieldCheck size={12} /> Reachable</Tag>}
            {server.state === 'Pending' && <Tag>Not scanned yet</Tag>}
            {server.state === 'Error' && <Tag tone="bad"><AlertTriangle size={12} /> Unreachable</Tag>}
            {server.state === 'Retired' && <Tag>Retired</Tag>}
          </div>
          <div className="text-[12.5px] text-ink-3 mt-0.5">
            {server.lastSeenAt ? `Last contact ${ago(server.lastSeenAt)}` : 'Never contacted'}
          </div>
          {server.state === 'Error' && server.lastError && <div className="text-[12.5px] text-[var(--s-critical)] mt-1">{server.lastError}</div>}

          <div className="flex items-start gap-1.5 text-[12.5px] mt-2">
            <CalendarClock size={13} className="text-ink-3 shrink-0 mt-[3px]" aria-hidden />
            <div className="min-w-0">
              <span className="text-ink-2">{scheduleText(sched)}</span>
              {admin && !editing && (
                <button type="button" className="font-medium text-accent hover:underline ml-2" onClick={() => setEditing(true)} disabled={!!busy}>
                  Change
                </button>
              )}
              {sched.nextScanAt && <div className="text-ink-3">Next scan {fmtDateTime(sched.nextScanAt)}</div>}
              <div className="text-ink-2 mt-0.5">{fullScanText(sched, sched)}</div>
              {sched.nextFullScanAt && <div className="text-ink-3">Next full scan {fmtDateTime(sched.nextFullScanAt)}</div>}
            </div>
          </div>
          {sched.missedAt && (
            <div className="mt-2">
              <Callout tone="warn">
                The scan due {fmtDateTime(sched.missedAt)} didn't start because the Data Scope Worker service wasn't running then.
                {sched.nextScanAt ? ` The next one is ${fmtDateTime(sched.nextScanAt)}.` : ''} Start a quick scan to catch up.
              </Callout>
            </div>
          )}
          {admin && editing && (
            <ScheduleForm
              server={server}
              busy={busy === server.id + 's'}
              onCancel={() => setEditing(false)}
              onSave={(input) => act(server.id + 's', async () => { await api.setSchedule(server.id, input); setEditing(false); })}
            />
          )}

          {job && (
            <div className="mt-3">
              <div className="flex items-center gap-2 text-[12.5px] mb-1.5">
                <JobTag job={job} />
                <span className="text-ink-2">{kindLabel(job)}</span>
                {jobOrigin(job) && <span className="text-ink-3">{jobOrigin(job)}</span>}
                {active && job.itemsTotal > 0 && (
                  <span className="text-ink-2 tnum">
                    {job.phase === 'classify'
                      ? `Checking contents: ${fmtNum(job.itemsDone)} of ${fmtNum(job.itemsTotal)} files`
                      : `Listing folders: ${fmtNum(job.itemsDone)} of ${fmtNum(job.itemsTotal)} found so far`}
                  </span>
                )}
                {!active && job.finishedAt && <span className="text-ink-3">{ago(job.finishedAt)}</span>}
              </div>
              {active && <Meter value={progress} label="Scan progress" />}
              {job.state === 'Failed' && job.lastError && <div className="text-[12.5px] text-ink-2 mt-1">{job.lastError}</div>}
              {job.state === 'Queued' && job.lastError && <div className="text-[12.5px] text-ink-2 mt-1">Retrying: {job.lastError}</div>}
            </div>
          )}

          {admin && (
            <div className="flex flex-wrap gap-2 mt-3">
              {!active && (['Quick', 'Full'] as ScanKind[]).map((kind) => (
                <Button
                  key={kind}
                  size="sm"
                  variant={kind === 'Quick' ? 'primary' : undefined}
                  disabled={!!busy}
                  title={kind === 'Quick' ? 'Reads only new and changed files' : 'Reads every file again, with more time for each one'}
                  onClick={() => act(server.id + kind, () => api.startScan(server.id, kind))}
                >
                  {busy === server.id + kind ? <Loader2 size={14} className="animate-spin" /> : <ScanLine size={14} />} {kind === 'Quick' ? 'Quick scan' : 'Full scan'}
                </Button>
              ))}
              {job?.state === 'Running' && !job.requestedState && (
                <Button size="sm" disabled={!!busy} onClick={() => act(job.id, () => api.controlScan(job.id, 'pause'))}><Pause size={14} /> Pause</Button>
              )}
              {job?.state === 'Paused' && (
                <Button size="sm" disabled={!!busy} onClick={() => act(job.id, () => api.controlScan(job.id, 'resume'))}><Play size={14} /> Resume</Button>
              )}
              {active && !job.requestedState && (
                <Button size="sm" variant="ghost" disabled={!!busy} onClick={() => act(job.id + 'c', () => api.controlScan(job.id, 'cancel'))}><Square size={13} /> Cancel</Button>
              )}
              {job?.state !== 'Running' && !confirming && (
                <Button size="sm" variant="ghost" className="ml-auto" disabled={!!busy} onClick={() => setConfirming(true)}><Trash2 size={13} /> Remove</Button>
              )}
            </div>
          )}

          {admin && confirming && (
            <div className="mt-3 rounded-lg border border-line bg-surface-2 p-3 text-[13px]" role="group" aria-label={`Remove ${server.hostname}`}>
              <div className="font-medium">Remove {server.hostname} from Data Scope?</div>
              <p className="text-ink-2 mt-1">
                It won't be scanned again, its waiting scans and file activity collection stop, and its shares leave every screen, report and new access review.
                Nothing on the server is touched and nothing is deleted from Data Scope's history. Add it again by name to bring it back.
              </p>
              <div className="flex gap-2 mt-3">
                <Button size="sm" variant="danger" disabled={!!busy} onClick={() => act(server.id + 'r', () => api.removeServer(server.id))}>
                  {busy === server.id + 'r' ? <Loader2 size={14} className="animate-spin" /> : <Trash2 size={14} />} Remove server
                </Button>
                <Button size="sm" variant="ghost" onClick={() => setConfirming(false)}>Keep it</Button>
              </div>
            </div>
          )}
        </div>
      </div>
    </Card>
  );
}

function ScheduleForm({ server, busy, onSave, onCancel }: {
  server: Server;
  busy: boolean;
  onSave: (input: ScanScheduleInput) => Promise<void>;
  onCancel: () => void;
}) {
  const s = server.schedule;
  const [frequency, setFrequency] = useState<ScanFrequency>(s.frequency === 'Off' ? 'Daily' : s.frequency);
  const [off, setOff] = useState(s.frequency === 'Off');
  const [time, setTime] = useState(timeValue(s.startHour, s.startMinute ?? 0));
  const [everyHours, setEveryHours] = useState(s.everyHours ?? 4);
  const [day, setDay] = useState<Weekday>(s.day ?? 'Saturday');
  const [stopHour, setStopHour] = useState<number | null>(s.frequency === 'Off' && s.stopHour == null ? 7 : s.stopHour);
  const [fullScan, setFullScan] = useState<FullScanFrequency>(s.fullScan);
  const [fullScanDay, setFullScanDay] = useState<Weekday | null>(s.fullScanDay ?? 'Saturday');
  const start = parseTime(time);
  const startHour = start?.hour ?? -1;
  const startMinute = start?.minute ?? 0;
  const hourly = frequency === 'Hourly';
  const full = { fullScan, fullScanDay: fullScan === 'Off' ? null : fullScanDay };
  const input: ScanScheduleInput = off
    ? { frequency: 'Off', startHour: Math.max(startHour, 0), startMinute, day: null, stopHour, ...full }
    : { frequency, startHour, startMinute, everyHours: hourly ? everyHours : null, day: frequency === 'Weekly' ? day : null, stopHour, ...full };
  const problem = scheduleProblem(input) ?? fullScanProblem(input);
  const hours = windowHours(startHour, stopHour);
  const hourOptions = Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{hourLabel(h)}</option>);
  const choose = (v: 'Off' | ScanFrequency) => {
    if (v === 'Off') return setOff(true);
    setOff(false);
    // A nightly pause time would cut scans every few hours down to a couple: start them around the clock instead.
    if (v === 'Hourly' && frequency !== 'Hourly') setStopHour(null);
    setFrequency(v);
  };
  return (
    <form
      className="mt-3 rounded-lg border border-line bg-surface-2 p-3 text-[13px] space-y-3"
      aria-label={`Scan schedule for ${server.hostname}`}
      onSubmit={(e) => (e.preventDefault(), !problem && void onSave(input))}
    >
      <Segmented<'Off' | ScanFrequency>
        value={off ? 'Off' : frequency}
        onChange={choose}
        options={[
          { id: 'Hourly', label: 'Hourly' },
          { id: 'Daily', label: 'Daily' },
          { id: 'Weekly', label: 'Weekly' },
          { id: 'Off', label: 'By hand' },
        ]}
      />
      {!off && (
        <div className="flex flex-wrap items-end gap-3">
          {hourly && (
            <Field label="Every" htmlFor={`every-${server.id}`}>
              <select id={`every-${server.id}`} className={filterCls} value={everyHours} onChange={(e) => setEveryHours(Number(e.target.value))}>
                {HOUR_STEPS.map((h) => <option key={h} value={h}>{h === 1 ? '1 hour' : `${h} hours`}</option>)}
              </select>
            </Field>
          )}
          {frequency === 'Weekly' && (
            <Field label="Day" htmlFor={`day-${server.id}`}>
              <select id={`day-${server.id}`} className={filterCls} value={day} onChange={(e) => setDay(e.target.value as Weekday)}>
                {WEEKDAYS.map((d) => <option key={d} value={d}>{d}</option>)}
              </select>
            </Field>
          )}
          <Field label={hourly ? 'Starting at' : 'Start at'} htmlFor={`start-${server.id}`}>
            <input id={`start-${server.id}`} type="time" step={60} required className={filterCls} value={time} onChange={(e) => setTime(e.target.value)} />
          </Field>
          <Field label={hourly ? 'Until' : 'Pause at, if not finished'} htmlFor={`stop-${server.id}`}>
            <select id={`stop-${server.id}`} className={filterCls} value={stopHour ?? ''} onChange={(e) => setStopHour(e.target.value === '' ? null : Number(e.target.value))}>
              <option value="">{hourly ? 'Around the clock' : "Don't pause; run until done"}</option>
              {hourOptions}
            </select>
          </Field>
        </div>
      )}
      <p className="text-[12px] text-ink-3">
        {off
          ? 'Quick scans run only when someone starts one. Risks and IT fix checks are only as current as the last scan.'
          : problem
            ? `Times are in the Data Scope server's time zone: ${server.schedule.timeZone}.`
            : hourly
              ? `Scans start at ${startsText(input)}. ${stopHour != null ? `One still running at ${hourLabel(stopHour)} pauses and picks up at the next start. ` : ''}A scan is never started twice, and each one opens only new or changed files. Times are in the Data Scope server's time zone: ${server.schedule.timeZone}.`
              : `${scheduleText(input)}. ${hours != null ? `A scan that runs longer than ${hours} hour${hours === 1 ? '' : 's'} continues where it stopped the next time. ` : ''}Times are in the Data Scope server's time zone: ${server.schedule.timeZone}. A scan already under way is never started twice.`}
      </p>
      {off && (
        <Field label="Full scans start at" htmlFor={`full-start-${server.id}`}>
          <input id={`full-start-${server.id}`} type="time" step={60} required className={filterCls} value={time} onChange={(e) => setTime(e.target.value)} />
        </Field>
      )}
      <FullScanPicker
        id={`full-day-${server.id}`}
        frequency={fullScan}
        day={fullScanDay}
        onChange={(f, d) => {
          setFullScan(f);
          setFullScanDay(d);
        }}
      />
      {!problem && fullScan !== 'Off' && (
        <p className="text-[12px] text-ink-3">
          {fullScanText(input, input)}{!off && stopHour != null ? `, pausing at ${hourLabel(stopHour)} if not finished and continuing at the next scheduled scan` : ', running until done'}.
        </p>
      )}
      {problem && <p className="text-[12.5px] text-[var(--s-critical)]">{problem}</p>}
      <div className="flex gap-2">
        <Button size="sm" type="submit" variant="primary" disabled={busy || !!problem}>
          {busy && <Loader2 size={14} className="animate-spin" />} Save schedule
        </Button>
        <Button size="sm" variant="ghost" disabled={busy} onClick={onCancel}>Cancel</Button>
      </div>
    </form>
  );
}

function JobTag({ job }: { job: ScanJob }) {
  if (job.requestedState === 'Paused') return <Tag tone="accent">Pausing…</Tag>;
  if (job.requestedState === 'Cancelled') return <Tag tone="warn">Cancelling…</Tag>;
  switch (job.state) {
    case 'Queued': return <Tag>Waiting for the scanner</Tag>;
    case 'Running': return <Tag tone="accent"><Loader2 size={12} className="animate-spin" /> Scanning</Tag>;
    case 'Paused': return <Tag tone="warn">Paused</Tag>;
    case 'Completed': return <Tag tone="good">Finished</Tag>;
    case 'Failed': return <Tag tone="bad">Failed</Tag>;
    case 'Cancelled': return <Tag>Cancelled</Tag>;
  }
}

function AddServer({ onDone, onCancel }: { onDone: () => void; onCancel: () => void }) {
  const [hostname, setHostname] = useState('');
  const [error, setError] = useState<string>();
  const [saving, setSaving] = useState(false);
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setSaving(true);
    setError(undefined);
    try {
      await api.enroll(hostname.trim());
      onDone();
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setSaving(false);
    }
  };
  return (
    <Card className="mb-4" title="Add a file server">
      <form onSubmit={submit} className="flex flex-col gap-3 max-w-lg">
        <Field label="Host name" htmlFor="live-hostname" hint="The server's DNS or NetBIOS name, for example fs01 or fs01.yourdomain.local. IP addresses are not accepted because Windows sign-in to the share needs the name.">
          <input id="live-hostname" className={inputCls} value={hostname} onChange={(e) => setHostname(e.target.value)} autoFocus autoComplete="off" spellCheck={false} />
        </Field>
        {error && <div className="text-[13px] text-[var(--s-critical)]">{error}</div>}
        <div className="flex gap-2">
          <Button type="submit" variant="primary" disabled={saving || !hostname.trim()}>{saving ? <Loader2 size={14} className="animate-spin" /> : <Plus size={14} />} Add server</Button>
          <Button variant="ghost" onClick={onCancel}>Cancel</Button>
        </div>
      </form>
    </Card>
  );
}
