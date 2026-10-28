import { useMemo, useState } from 'react';
import { Check, Info } from 'lucide-react';
import { actions, useApp } from '../api/store';
import { useNav } from '../App';
import { useBackend } from '../api/backend';
import LiveActivity from './live/LiveActivity';
import { NOW } from '../data/seed';
import type { AuditEvent } from '../domain/types';
import { Button, Callout, Card, PageHeader, SeverityPill, Tag, Td, Th, ago, cx, fmtDateTime, filterCls, nameOf } from '../ui/kit';

const CERTAINTY: Record<AuditEvent['certainty'], { label: string; tone: 'good' | 'warn' | 'neutral'; help: string }> = {
  confirmed: { label: 'Confirmed', tone: 'good', help: 'Object access event (4663/4660/4670): Windows recorded the operation on the file.' },
  attempted: { label: 'Attempt', tone: 'warn', help: 'Handle request (4656): someone asked to open the file. Failure means Windows refused.' },
  'access-check': { label: 'Access check only', tone: 'neutral', help: 'Share access check (5145): Windows checked share access. This does not prove the file was opened or read.' },
};

export default function ActivityScreen() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LiveActivity />;
  return <DemoActivity />;
}

function DemoActivity() {
  const s = useApp();
  const { readOnly } = useNav();
  const [share, setShare] = useState('all');
  const [who, setWho] = useState('all');
  const [op, setOp] = useState('all');
  const [showChecks, setShowChecks] = useState(false);
  const [limit, setLimit] = useState(60);

  const rows = useMemo(
    () =>
      s.events
        .filter((e) => showChecks || e.eventId !== 5145)
        .filter((e) => share === 'all' || e.shareId === share)
        .filter((e) => who === 'all' || e.principalSid === who)
        .filter((e) => op === 'all' || e.operation === op),
    [s.events, share, who, op, showChecks],
  );
  const actors = [...new Set(s.events.map((e) => e.principalSid))].sort((a, b) => nameOf(a).localeCompare(nameOf(b)));

  // Confirmed operations per day for the last 7 days.
  const days = Array.from({ length: 7 }, (_, i) => {
    const start = new Date(NOW.getTime() - (6 - i) * 864e5);
    start.setUTCHours(0, 0, 0, 0);
    const end = start.getTime() + 864e5;
    const evs = s.events.filter((e) => e.certainty === 'confirmed' && e.eventId === 4663 && +new Date(e.time) >= +start && +new Date(e.time) < end);
    return { label: start.toLocaleDateString('en-US', { weekday: 'short' }), n: evs.length, deletes: evs.filter((e) => e.operation === 'Delete').length };
  });
  const max = Math.max(...days.map((d) => d.n), 1);

  return (
    <>
      <PageHeader
        eyebrow="Preview · ships in V1.5"
        title="File activity"
        sub="Who opened, changed, deleted or re-permissioned files, from the Windows Security log on each file server. Shown here on synthetic events so the design can be reviewed now."
      />
      <div className="mb-4">
        <Callout tone="info" title="How this will work">
          A small Data Scope collector runs on each file server and reads Security events 4663, 4656, 4660, 4670, 5140 and 5145 after you turn on object access auditing for the folders you care about. Events are buffered locally, de-duplicated by record ID and never leave your network.
        </Callout>
      </div>

      <div className="grid lg:grid-cols-[1fr_1.1fr] gap-4 mb-4">
        <Card title="Alerts">
          <ul className="flex flex-col gap-3">
            {s.alerts.map((a) => (
              <li key={a.id} className={cx('flex flex-wrap items-start gap-3', a.state === 'Acknowledged' && 'opacity-60')}>
                <SeverityPill s={a.severity} />
                <div className="flex-1 min-w-0 text-[13px]">
                  <div className="font-medium">{a.rule} · {nameOf(a.principalSid)}</div>
                  <div className="text-ink-2">{a.summary}</div>
                  <div className="text-[12px] text-ink-3">{fmtDateTime(a.time)} · {a.eventCount} events</div>
                </div>
                {a.state === 'Open' && !readOnly ? (
                  <Button size="sm" onClick={() => actions.ackAlert(a.id)}><Check size={14} /> Acknowledge</Button>
                ) : (
                  <Tag>{a.state}</Tag>
                )}
              </li>
            ))}
          </ul>
        </Card>
        <Card title="Confirmed file operations per day">
          <svg viewBox="0 0 420 150" className="w-full h-auto" role="img" aria-label="Confirmed file operations per day, last 7 days">
            {[0, 0.5, 1].map((t) => (
              <g key={t}>
                <line x1="34" x2="414" y1={120 - t * 100} y2={120 - t * 100} stroke="var(--line)" strokeWidth="1" />
                <text x="28" y={124 - t * 100} textAnchor="end" fontSize="10" fill="var(--ink-3)" className="tnum">{Math.round(max * t)}</text>
              </g>
            ))}
            {days.map((d, i) => {
              const x = 44 + i * 54;
              const h = (d.n / max) * 100;
              const hd = (d.deletes / max) * 100;
              return (
                <g key={i}>
                  <title>{`${d.label}: ${d.n} operations, ${d.deletes} deletes`}</title>
                  <rect x={x} y={120 - h} width="34" height={Math.max(h - hd - (hd ? 2 : 0), 0)} rx="3" fill="var(--accent)" />
                  {hd > 0 && <rect x={x} y={120 - hd} width="34" height={hd} rx="3" fill="var(--s-high)" />}
                  <text x={x + 17} y="138" textAnchor="middle" fontSize="10.5" fill="var(--ink-3)">{d.label}</text>
                </g>
              );
            })}
          </svg>
          <div className="flex gap-4 text-[12px] text-ink-2 mt-1">
            <span className="inline-flex items-center gap-1.5"><i className="size-2.5 rounded-sm" style={{ background: 'var(--accent)' }} /> Reads and writes</span>
            <span className="inline-flex items-center gap-1.5"><i className="size-2.5 rounded-sm" style={{ background: 'var(--s-high)' }} /> Deletes</span>
          </div>
        </Card>
      </div>

      <div className="flex flex-wrap items-center gap-2 mb-3">
        <select id="act-share" aria-label="Share" className={filterCls} value={share} onChange={(e) => setShare(e.target.value)}>
          <option value="all">All shares</option>
          {s.shares.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
        </select>
        <select id="act-user" aria-label="Person" className={filterCls} value={who} onChange={(e) => setWho(e.target.value)}>
          <option value="all">Everyone</option>
          {actors.map((a) => <option key={a} value={a}>{nameOf(a)}</option>)}
        </select>
        <select id="act-op" aria-label="Operation" className={filterCls} value={op} onChange={(e) => setOp(e.target.value)}>
          <option value="all">Any operation</option>
          {['Read', 'Write', 'Delete', 'Permission change', 'Handle request', 'Share access check'].map((o) => <option key={o}>{o}</option>)}
        </select>
        <label className="inline-flex items-center gap-2 text-[13px] text-ink-2 ml-1">
          <input id="act-checks" type="checkbox" className="size-4 accent-[var(--accent)]" checked={showChecks} onChange={(e) => setShowChecks(e.target.checked)} />
          Include share access checks (5145)
        </label>
        <span className="ml-auto text-[12.5px] text-ink-3 tnum">{rows.length} events</span>
      </div>

      <div className="bg-surface border border-line rounded-xl overflow-x-auto">
        <table className="w-full text-[13px] min-w-[900px]">
          <thead>
            <tr><Th>When</Th><Th>Who</Th><Th>What</Th><Th>File or folder</Th><Th>Evidence</Th></tr>
          </thead>
          <tbody>
            {rows.slice(0, limit).map((e) => {
              const c = CERTAINTY[e.certainty];
              const sh = s.shares.find((x) => x.id === e.shareId);
              const corr = !e.path && e.correlationId ? s.events.find((x) => x.correlationId === e.correlationId && x.path) : undefined;
              return (
                <tr key={e.id} className="border-t border-line">
                  <Td className="whitespace-nowrap text-ink-2">{ago(e.time)}</Td>
                  <Td className="whitespace-nowrap">{nameOf(e.principalSid)}</Td>
                  <Td>
                    <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
                      {e.operation}
                      {e.outcome === 'failure' ? <Tag tone="bad">Denied</Tag> : <Tag tone={c.tone} title={c.help}>{c.label}</Tag>}
                    </span>
                  </Td>
                  <Td className="max-w-[340px]">
                    {e.path ? (
                      <span className="font-mono text-[12px] break-all">{sh?.name}\{e.path.replace(/\//g, '\\')}</span>
                    ) : corr ? (
                      <span className="text-[12px]"><span className="font-mono break-all">{sh?.name}\{corr.path!.replace(/\//g, '\\')}</span> <span className="text-ink-3">(path from correlated 4663)</span></span>
                    ) : (
                      <span className="text-ink-3 text-[12px]">No path in event</span>
                    )}
                  </Td>
                  <Td className="whitespace-nowrap font-mono text-[11.5px] text-ink-3" >
                    <span title={c.help}>{e.eventId}</span> · #{e.recordId} · {s.servers.find((x) => x.id === e.serverId)?.hostname}
                  </Td>
                </tr>
              );
            })}
          </tbody>
        </table>
        {rows.length > limit && (
          <div className="border-t border-line px-4 py-2.5">
            <Button size="sm" variant="ghost" onClick={() => setLimit(limit + 100)}>Show more</Button>
          </div>
        )}
      </div>
      <p className="text-[12px] text-ink-3 mt-2 flex gap-1.5"><Info size={14} className="shrink-0 mt-px" /> A 5145 share access check means Windows checked whether someone may use the share. It is not proof a file was read, so Data Scope never labels it as a read.</p>
    </>
  );
}
