import { useEffect, useMemo, useState, type PointerEvent } from 'react';
import { Activity as ActivityIcon, Info, X } from 'lucide-react';
import { api, type ActivityDay, type ActivityEvent, type ActivityEventDetail, type ActivityFilters, type ActivityOperation, type ActivitySource } from '../../api/client';
import { canManage, canTriageActivity, useBackend } from '../../api/backend';
import { Button, Callout, Card, Drawer, Empty, PageHeader, Tag, Td, Th, ago, cx, filterCls, fmtDateTime, fmtNum, inputCls } from '../../ui/kit';
import { CERTAINTY, OPERATION_FILTERS, SERIES, alertFilters, dayTotal, gapText, logHealth, sourceStatus, whereText } from './activity';
import { niceMax } from './reporting';
import { useData } from './useData';
import { AlertsCard, UnusualCard } from './ActivityAlerts';

/**
 * File activity from each file server's Security log (D158). Every row says how much it proves.
 */
export default function LiveActivity() {
  return (
    <>
      <PageHeader
        title="File activity"
        sub="Who read, changed, deleted or re-permissioned files, from each file server's Security log. Each event says how much it proves."
      />
      <FileShareActivity />
    </>
  );
}

/**
 * File activity from each file server's Security log. Every row says how much it proves: recorded operations and
 * open requests by default, share access checks and connections only when asked for, and never as reads (D97).
 */
function FileShareActivity() {
  const backend = useBackend();
  const admin = canManage(backend);
  const sources = useData(() => api.activitySources(), [], 15000);
  const summary = useData(() => api.activitySummary(7), [], 30000);
  const alerts = useData(() => api.activityAlerts('All', 30), [], 30000);
  const triage = canTriageActivity(backend);
  const [filters, setFilters] = useState<ActivityFilters>({});
  const [person, setPerson] = useState<{ sid: string; name: string }>();
  const [search, setSearch] = useState('');
  const [rows, setRows] = useState<ActivityEvent[]>([]);
  const [next, setNext] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const [openId, setOpenId] = useState<number>();
  const [problem, setProblem] = useState<string>();

  const query = useMemo<ActivityFilters>(() => ({ ...filters, principalSid: person?.sid }), [filters, person]);
  const load = async (cursor?: string) => {
    setLoading(true);
    try {
      const page = await api.activityEvents(query, cursor);
      setRows((r) => (cursor ? [...r, ...page.items] : page.items));
      setNext(page.next);
      setError(undefined);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  };
  useEffect(() => {
    void load();
  }, [query]); // eslint-disable-line react-hooks/exhaustive-deps

  const set = (patch: Partial<ActivityFilters>) => setFilters((f) => ({ ...f, ...patch }));
  const act = async (fn: () => Promise<unknown>) => {
    setProblem(undefined);
    try {
      await fn();
      await Promise.all([sources.reload(), alerts.reload()]);
      await load();
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    }
  };

  const fileAlerts = alerts.data?.items ?? [];
  const list = sources.data ?? [];
  const shares = list.flatMap((s) => s.shares.map((sh) => ({ ...sh, hostname: s.hostname, serverId: s.serverId })));
  const anyOn = list.some((s) => s.enabled);

  return (
    <>
      {(error || sources.error) && <div className="mb-4"><Callout tone="bad" title="Can't reach the Data Scope server">{error ?? sources.error}</Callout></div>}
      {problem && <div className="mb-4"><Callout tone="bad" title="That didn't work">{problem}</Callout></div>}
      {sources.data && !anyOn && (
        <div className="mb-4">
          <Callout tone="info" title="Collection is off">
            Data Scope reads file events (4656, 4660, 4663, 4670, 5140, 5145) from a file server's Security log once you turn collection on for it below.
            Windows only writes these events for folders with auditing turned on; Data Scope never changes audit settings. An administrator turns auditing on with
            <span className="font-mono"> Enable-DataScopeAuditing.ps1 </span>from the Data Scope package; see the install guide, "File activity".
          </Callout>
        </div>
      )}

      {fileAlerts.length > 0 && (
        <AlertsCard
          alerts={fileAlerts}
          triage={triage}
          onShow={(a) => {
            const view = alertFilters(a);
            if (!view) return;
            setFilters(view.filters);
            setPerson(view.person);
            setSearch('');
            document.getElementById('activity-events')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
          }}
          onSet={(a, state, note) => act(() => api.setActivityAlert(a.id, state, note))}
        />
      )}
      {anyOn && <UnusualCard />}

      <div className="grid lg:grid-cols-[1.1fr_1fr] gap-4 mb-4">
        <Card title="Collection">
          {list.length === 0 ? (
            <p className="text-[13px] text-ink-2">Add a file server under Servers &amp; scans first.</p>
          ) : (
            <ul className="flex flex-col divide-y divide-line">
              {list.map((s) => (
                <SourceRow key={s.serverId} source={s} admin={admin} onToggle={(on) => act(() => api.setActivitySource(s.serverId, on))}
                  onSetPath={(shareId, path) => act(() => api.setShareLocalPath(shareId, path))} />
              ))}
            </ul>
          )}
        </Card>
        <Card title="Recorded operations per day">
          {summary.data ? <DayChart days={summary.data.days} /> : <div className="h-40" />}
          {summary.data && summary.data.unmatched > 0 && (
            <p className="text-[12.5px] text-ink-2 mt-2">
              {summary.data.unmatched === 1 ? '1 event this week is' : `${fmtNum(summary.data.unmatched)} events this week are`} in a folder Data Scope has not matched to a share. Set the share's folder under Collection to match them.
            </p>
          )}
          {summary.data && summary.data.gaps > 0 && (
            <p className="text-[12.5px] text-[var(--s-high)] mt-2">
              Some events were gone from a Security log before Data Scope read them ({summary.data.gaps === 1 ? 'once' : `${fmtNum(summary.data.gaps)} times`} this week), so these totals may be low. See Collection.
            </p>
          )}
        </Card>
      </div>

      <div id="activity-events" className="flex flex-wrap items-center gap-2 mb-3 scroll-mt-4">
        <select aria-label="File server" className={filterCls} value={filters.serverId ?? ''} onChange={(e) => set({ serverId: e.target.value || undefined, shareId: undefined })}>
          <option value="">All file servers</option>
          {list.map((s) => <option key={s.serverId} value={s.serverId}>{s.hostname}</option>)}
        </select>
        <select aria-label="Share" className={filterCls} value={filters.shareId ?? ''} onChange={(e) => set({ shareId: e.target.value || undefined })}>
          <option value="">All shares</option>
          {shares.filter((s) => !filters.serverId || s.serverId === filters.serverId).map((s) => <option key={s.id} value={s.id}>{s.hostname}\{s.name}</option>)}
        </select>
        <select aria-label="Operation" className={filterCls} value={filters.operation ?? ''} onChange={(e) => set({ operation: (e.target.value || undefined) as ActivityOperation | undefined })}>
          <option value="">Any operation</option>
          {OPERATION_FILTERS.map((o) => <option key={o.id} value={o.id}>{o.label}</option>)}
        </select>
        <select aria-label="Outcome" className={filterCls} value={filters.outcome ?? ''} onChange={(e) => set({ outcome: (e.target.value || undefined) as 'Failure' | undefined })}>
          <option value="">Allowed and denied</option>
          <option value="Failure">Denied only</option>
        </select>
        <form className="flex-1 min-w-[180px] max-w-[320px]" onSubmit={(e) => { e.preventDefault(); set({ search: search.trim() || undefined }); }}>
          <input aria-label="Search paths" className={inputCls} placeholder="Search paths, then Enter" value={search} onChange={(e) => setSearch(e.target.value)} />
        </form>
        <label className="inline-flex items-center gap-2 text-[13px] text-ink-2">
          <input type="checkbox" className="size-4 accent-[var(--accent)]" checked={!!filters.includeChecks} onChange={(e) => set({ includeChecks: e.target.checked || undefined })} />
          Include share access checks and connections
        </label>
        {person && (
          <button className="inline-flex items-center gap-1 h-8 px-2.5 rounded-full bg-accent-soft text-[12.5px]" onClick={() => setPerson(undefined)} aria-label={`Stop filtering on ${person.name}`}>
            Only {person.name} <X size={13} />
          </button>
        )}
        {(filters.from || filters.to) && (
          <button className="inline-flex items-center gap-1 h-8 px-2.5 rounded-full bg-accent-soft text-[12.5px]" onClick={() => set({ from: undefined, to: undefined })} aria-label="Stop filtering on time">
            {filters.from ? fmtDateTime(filters.from) : 'start'} to {filters.to ? fmtDateTime(filters.to) : 'now'} <X size={13} />
          </button>
        )}
      </div>

      {rows.length === 0 && !loading ? (
        <Card><Empty icon={<ActivityIcon size={20} />} title="No file activity">{anyOn ? 'Nothing matches these filters yet.' : 'Turn collection on for a file server to see its activity here.'}</Empty></Card>
      ) : (
        <div className="bg-surface border border-line rounded-xl overflow-x-auto">
          <table className="w-full text-[13px] min-w-[900px]">
            <thead><tr><Th>When</Th><Th>Who</Th><Th>What</Th><Th>File or folder</Th><Th>Evidence</Th></tr></thead>
            <tbody>
              {rows.map((e) => {
                const c = CERTAINTY[e.certainty];
                const where = whereText(e);
                return (
                  <tr key={e.id} className="border-t border-line hover:bg-surface-2 cursor-pointer" onClick={() => setOpenId(e.id)}>
                    <Td className="whitespace-nowrap"><span title={fmtDateTime(e.time)}>{ago(e.time)}</span></Td>
                    <Td className="whitespace-nowrap">
                      {e.principalName && e.principalSid ? (
                        <button className="hover:underline text-left" title="Show only this person" onClick={(ev) => { ev.stopPropagation(); setPerson({ sid: e.principalSid!, name: e.principalName! }); }}>
                          {e.principalName}
                        </button>
                      ) : <span className="text-ink-3">{e.principalName ?? 'Unknown account'}</span>}
                    </Td>
                    <Td>
                      <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
                        {e.label}
                        {e.outcome === 'Failure' ? <Tag tone="bad">Denied</Tag> : <Tag tone={c.tone} title={c.help}>{c.label}</Tag>}
                      </span>
                    </Td>
                    <Td className="max-w-[360px]">
                      {where.path && <span className="font-mono text-[12px] break-all">{where.path}</span>}
                      {where.note && <span className={cx('text-[12px] text-ink-3', where.path && 'block')}>{where.note}</span>}
                    </Td>
                    <Td className="whitespace-nowrap font-mono text-[11.5px] text-ink-3"><span title={c.help}>{e.eventId || 'n/a'}</span> · #{e.recordId} · {e.hostname}</Td>
                  </tr>
                );
              })}
            </tbody>
          </table>
          {next && (
            <div className="border-t border-line px-4 py-2.5">
              <Button size="sm" variant="ghost" disabled={loading} onClick={() => load(next)}>Show older events</Button>
            </div>
          )}
        </div>
      )}
      <p className="text-[12px] text-ink-3 mt-2 flex gap-1.5">
        <Info size={14} className="shrink-0 mt-px" />
        A share access check (5145) means Windows checked whether someone may use a file through the share. It is not proof the file was opened or read, so Data Scope never shows it as a read.
      </p>
      {openId !== undefined && <EventDrawer id={openId} onClose={() => setOpenId(undefined)} />}
    </>
  );
}

function SourceRow({ source: s, admin, onToggle, onSetPath }: {
  source: ActivitySource; admin: boolean; onToggle: (on: boolean) => void; onSetPath: (shareId: string, path: string | null) => void;
}) {
  const [open, setOpen] = useState(false);
  const status = sourceStatus(s);
  const missing = s.shares.filter((sh) => !sh.localPath).length;
  const health = s.enabled && s.log ? logHealth(s.log) : null;
  const [showGaps, setShowGaps] = useState(false);
  return (
    <li className="py-3 first:pt-0 last:pb-0">
      <div className="flex flex-wrap items-start gap-x-3 gap-y-1">
        <div className="flex-1 min-w-0">
          <div className="font-medium">{s.hostname}</div>
          <div className={cx('text-[12.5px]', status.tone === 'bad' ? 'text-[var(--s-critical)]' : 'text-ink-2')}>{status.text}</div>
          {s.enabled && (
            <div className="text-[12px] text-ink-3 tnum">
              {fmtNum(s.eventsStored)} events stored
              {s.lastEventAt && <> · newest {ago(s.lastEventAt)}</>}
              {s.lastReadAt && <> · read {ago(s.lastReadAt)}</>}
              {s.eventsUnreadable > 0 && <> · {fmtNum(s.eventsUnreadable)} unreadable</>}
            </div>
          )}
          {health && <div className="text-[12px] text-ink-3 tnum">{health.text}</div>}
          {health?.warning && <div className="text-[12.5px] text-[var(--s-high)] mt-0.5">{health.warning}</div>}
        </div>
        <Tag tone={status.tone === 'bad' ? 'bad' : status.tone}>{s.enabled ? (s.state === 'Error' ? 'Error' : 'On') : 'Off'}</Tag>
        {admin && <Button size="sm" onClick={() => onToggle(!s.enabled)}>{s.enabled ? 'Turn off' : 'Turn on'}</Button>}
      </div>
      {s.gaps.length > 0 && (
        <div className="mt-1.5 text-[12.5px]">
          <button className="text-[var(--s-high)] hover:underline" onClick={() => setShowGaps(!showGaps)} aria-expanded={showGaps}>
            Events missed {s.gaps.length === 1 ? 'once' : `${s.gaps.length >= 10 ? '10 or more' : s.gaps.length} times`}, last {ago(s.gaps[0].detectedAt)}
          </button>
          {showGaps && (
            <ul className="mt-1 flex flex-col gap-1 text-ink-2">
              {s.gaps.map((g) => <li key={g.id}><span className="text-ink-3 tnum">{fmtDateTime(g.detectedAt)}</span> · {gapText(g)}</li>)}
            </ul>
          )}
        </div>
      )}
      {s.shares.length > 0 && (
        <button className="mt-1.5 block text-[12.5px] text-ink-2 hover:underline" onClick={() => setOpen(!open)} aria-expanded={open}>
          {open ? 'Hide' : 'Show'} share folders{missing > 0 && ` (${missing} not known yet)`}
        </button>
      )}
      {open && (
        <table className="w-full text-[12.5px] mt-2">
          <tbody>
            {s.shares.map((sh) => <ShareFolderRow key={sh.id} share={sh} admin={admin} onSave={(p) => onSetPath(sh.id, p)} />)}
          </tbody>
        </table>
      )}
    </li>
  );
}

function ShareFolderRow({ share, admin, onSave }: { share: ActivitySource['shares'][number]; admin: boolean; onSave: (path: string | null) => void }) {
  const [editing, setEditing] = useState(false);
  const [value, setValue] = useState(share.localPath ?? '');
  return (
    <tr className="border-t border-line">
      <td className="py-1.5 pr-2 font-medium whitespace-nowrap align-top">{share.name}</td>
      <td className="py-1.5 min-w-0">
        {editing ? (
          <form className="flex gap-2" onSubmit={(e) => { e.preventDefault(); onSave(value.trim() || null); setEditing(false); }}>
            <input aria-label={`Folder for ${share.name}`} className={inputCls} placeholder="D:\Shares\Finance" value={value} onChange={(e) => setValue(e.target.value)} />
            <Button size="sm" type="submit" variant="primary">Save</Button>
          </form>
        ) : (
          <>
            {share.localPath ? <span className="font-mono break-all">{share.localPath}</span> : <span className="text-ink-3">Not known yet</span>}
            <span className="text-ink-3"> {share.localPathSource === 'Event' ? '(from share events)' : share.localPathSource === 'Admin' ? '(set by an admin)' : ''}</span>
            {admin && <button className="ml-2 text-ink-2 hover:underline" onClick={() => setEditing(true)}>Edit</button>}
          </>
        )}
      </td>
    </tr>
  );
}

/** Stacked bars of recorded operations per day, with a legend carrying the week's totals and a value per day on hover. */
function DayChart({ days }: { days: ActivityDay[] }) {
  const [hover, setHover] = useState<number | null>(null);
  const W = 420;
  const H = 130;
  const left = 36;
  const top = niceMax(Math.max(1, ...days.map(dayTotal)));
  const step = (W - left) / days.length;
  const bar = Math.min(34, step - 10);
  const label = (d: ActivityDay) => new Date(`${d.date}T12:00:00`).toLocaleDateString(undefined, { weekday: 'short', day: 'numeric' });
  const shown = hover === null ? null : days[hover];
  const onMove = (e: PointerEvent<SVGRectElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    const i = Math.floor((((e.clientX - box.left) / box.width) * (W - left)) / step);
    setHover(Math.max(0, Math.min(days.length - 1, i)));
  };
  return (
    <figure className="min-w-0">
      <div className="text-[12px] text-ink-3 tnum h-4">
        {shown ? `${label(shown)}: ${SERIES.map((s) => `${fmtNum(shown[s.key])} ${s.label.toLowerCase()}`).join(', ')}${shown.denied ? `, ${fmtNum(shown.denied)} denied` : ''}` : 'Last 7 days, your time zone'}
      </div>
      <svg viewBox={`0 0 ${W} ${H + 22}`} className="w-full h-auto mt-1" role="img" aria-label="Recorded file operations per day for the last 7 days">
        {[0, 0.5, 1].map((f) => (
          <g key={f}>
            <line x1={left} x2={W} y1={H - f * (H - 6)} y2={H - f * (H - 6)} stroke="var(--line)" strokeWidth={1} />
            <text x={left - 6} y={H - f * (H - 6)} dy="0.32em" textAnchor="end" fontSize={10} fill="var(--ink-3)" className="tnum">{fmtNum(top * f)}</text>
          </g>
        ))}
        {days.map((d, i) => {
          const x = left + i * step + (step - bar) / 2;
          let y = H;
          return (
            <g key={d.date} opacity={hover === null || hover === i ? 1 : 0.55}>
              {SERIES.map((s) => {
                const h = (d[s.key] / top) * (H - 6);
                if (h <= 0) return null;
                y -= h;
                // A 2px gap of surface between stacked segments.
                return <rect key={s.key} x={x} y={y} width={bar} height={Math.max(h - 2, 1)} rx={2} fill={s.color} />;
              })}
              <text x={x + bar / 2} y={H + 15} textAnchor="middle" fontSize={10} fill="var(--ink-3)">{label(d)}</text>
            </g>
          );
        })}
        <rect x={left} y={0} width={W - left} height={H} fill="transparent" onPointerMove={onMove} onPointerLeave={() => setHover(null)} />
      </svg>
      <figcaption className="flex flex-wrap gap-x-4 gap-y-1 text-[12px] text-ink-2 mt-1">
        {SERIES.map((s) => (
          <span key={s.key} className="inline-flex items-center gap-1.5">
            <i className="size-2.5 rounded-sm" style={{ background: s.color }} /> {s.label} <span className="tnum text-ink">{fmtNum(days.reduce((n, d) => n + d[s.key], 0))}</span>
          </span>
        ))}
      </figcaption>
      <table className="sr-only">
        <caption>Recorded file operations per day</caption>
        <thead><tr><th>Day</th>{SERIES.map((s) => <th key={s.key}>{s.label}</th>)}<th>Denied</th></tr></thead>
        <tbody>
          {days.map((d) => (
            <tr key={d.date}><th scope="row">{label(d)}</th>{SERIES.map((s) => <td key={s.key}>{d[s.key]}</td>)}<td>{d.denied}</td></tr>
          ))}
        </tbody>
      </table>
    </figure>
  );
}

function EventDrawer({ id, onClose }: { id: number; onClose: () => void }) {
  const detail = useData<ActivityEventDetail>(() => api.activityEvent(id), [id]);
  const d = detail.data;
  const e = d?.event;
  const where = e ? whereText(e) : null;
  return (
    <Drawer open onClose={onClose} title={e ? e.label : 'Event'} sub={e ? `${fmtDateTime(e.time)} · ${e.hostname}` : undefined} width={640}>
      {detail.error && <Callout tone="bad" title="Can't load this event">{detail.error}</Callout>}
      {d && e && (
        <div className="flex flex-col gap-4 text-[13px]">
          <Callout tone={e.certainty === 'Performed' && e.outcome === 'Success' ? 'good' : 'info'} title={e.outcome === 'Failure' ? 'Denied' : CERTAINTY[e.certainty].label}>
            {d.explanation}
          </Callout>
          <dl className="grid grid-cols-[max-content_1fr] gap-x-4 gap-y-1.5">
            <dt className="text-ink-3">Who</dt><dd className="min-w-0 break-words">{e.principalName ?? 'Unknown'}{e.principalSid && <span className="block font-mono text-[11.5px] text-ink-3">{e.principalSid}</span>}</dd>
            <dt className="text-ink-3">Where</dt><dd className="min-w-0">{where?.path && <span className="font-mono text-[12px] break-all">{where.path}</span>}{where?.note && <span className="block text-[12px] text-ink-3">{where.note}</span>}</dd>
            {e.objectPath && <><dt className="text-ink-3">Path on server</dt><dd className="font-mono text-[12px] break-all">{e.objectPath}</dd></>}
            <dt className="text-ink-3">Kind</dt><dd>{e.objectKind === 'Unknown' ? 'Not a file or folder from the last scan' : e.objectKind}</dd>
            {d.rights.length > 0 && <><dt className="text-ink-3">Rights</dt><dd className="font-mono text-[12px]">{d.rights.join(', ')}</dd></>}
            {e.clientAddress && <><dt className="text-ink-3">From</dt><dd className="font-mono text-[12px]">{e.clientAddress}</dd></>}
            {(d.processName || d.processId !== null) && <><dt className="text-ink-3">Process</dt><dd className="font-mono text-[12px] break-all">{d.processName ?? (d.processId === 4 ? 'System (file sharing)' : `id ${d.processId}`)}</dd></>}
            <dt className="text-ink-3">Event</dt><dd className="font-mono text-[12px]">{e.eventId || 'unreadable'} · record {e.recordId} · {d.channel} log{d.logGeneration > 0 ? `, generation ${d.logGeneration}` : ''}</dd>
            {d.handleId !== null && <><dt className="text-ink-3">Handle</dt><dd className="font-mono text-[12px]">0x{d.handleId.toString(16)}{d.logonId && ` · logon ${d.logonId}`}</dd></>}
            <dt className="text-ink-3">Collected</dt><dd>{fmtDateTime(d.ingestedAt)}</dd>
          </dl>
          {(d.oldSecurity || d.newSecurity) && (
            <div>
              <div className="font-medium mb-1">Permissions before and after (SDDL)</div>
              <pre className="font-mono text-[11.5px] bg-surface-2 rounded-md p-2.5 whitespace-pre-wrap break-all">{`Before: ${d.oldSecurity ?? 'not in event'}\nAfter:  ${d.newSecurity ?? 'not in event'}`}</pre>
            </div>
          )}
          {d.parseError && <Callout tone="warn" title="Why it could not be read">{d.parseError}</Callout>}
          <details>
            <summary className="cursor-pointer font-medium">Event as Windows recorded it</summary>
            <pre className="mt-2 font-mono text-[11px] bg-surface-2 rounded-md p-2.5 overflow-x-auto max-h-[360px] scroll-thin">{d.rawXml}</pre>
          </details>
        </div>
      )}
    </Drawer>
  );
}
