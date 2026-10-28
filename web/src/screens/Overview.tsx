import { useBackend } from '../api/backend';
import LiveOverview from './live/LiveOverview';
import { ArrowUpRight, ShieldCheck } from 'lucide-react';
import { useApp } from '../api/store';
import { useNav } from '../App';
import { CLASSIFICATIONS } from '../domain/types';
import { Card, ClassPill, LinkArrow, Meter, PageHeader, SeverityPill, Tag, ago, classColor, fmtNum, inDays, nameOf } from '../ui/kit';
import { activeUsers, ownedShareIds, shareStats } from './common';

export default function Overview() {
  const backend = useBackend();
  return backend.mode === 'live' ? <LiveOverview /> : <DemoOverview />;
}

function DemoOverview() {
  const s = useApp();
  const { go, risks, role } = useNav();
  if (role.role === 'Data Owner') return <OwnerHome />;

  const stats = s.shares.map((sh) => shareStats(s, sh.id));
  const totals = { Unknown: 0, Public: 0, Internal: 0, Confidential: 0, Restricted: 0 } as Record<(typeof CLASSIFICATIONS)[number], number>;
  stats.forEach((st) => CLASSIFICATIONS.forEach((c) => (totals[c] += st.byClass[c])));
  const totalFiles = s.files.length;
  const sensitive = totals.Restricted + totals.Confidential;
  const openRisk = risks.find((r) => r.rule === 'Sensitive data open to most of the company');
  const exposedFiles = new Set(risks.filter((r) => r.rule === 'Sensitive data open to most of the company').flatMap((r) => r.affectedFiles)).size;
  const scanned = s.files.filter((f) => f.scanStatus === 'scanned').length;
  const activeCampaigns = s.campaigns.filter((c) => c.status !== 'Completed');
  const overdue = s.campaigns.filter((c) => c.status === 'Overdue').length;
  const users = activeUsers(s);
  const lastScan = s.shares.map((x) => x.scan.lastScan).filter(Boolean).sort().pop();

  return (
    <>
      <PageHeader
        eyebrow="NORTHWIND.LOCAL"
        title="Good afternoon, Dana"
        sub={<>Last scan finished {ago(lastScan)} across {s.shares.length} shares on {s.servers.length} file servers. Here is what needs attention.</>}
      />

      <div className="grid grid-cols-2 lg:grid-cols-4 gap-3 mb-5">
        <Kpi
          label="Sensitive files"
          value={fmtNum(sensitive)}
          note={`${totals.Restricted} Restricted, ${totals.Confidential} Confidential`}
          onClick={() => go('discovery')}
        />
        <Kpi
          label="Open to most of the company"
          value={fmtNum(exposedFiles)}
          tone={exposedFiles ? 'bad' : 'good'}
          note={openRisk ? 'sensitive files reachable by 50%+ of staff' : 'none found'}
          onClick={() => go('risks')}
        />
        <Kpi label="Scan coverage" value={`${Math.round((scanned / totalFiles) * 100)}%`} note={`${fmtNum(totalFiles - scanned)} files could not be read`} onClick={() => go('setup')} />
        <Kpi
          label="Access reviews open"
          value={String(activeCampaigns.length)}
          tone={overdue ? 'warn' : undefined}
          note={overdue ? `${overdue} overdue` : 'all on schedule'}
          onClick={() => go('reviews')}
        />
      </div>

      <div className="grid lg:grid-cols-[1.35fr_1fr] gap-4 mb-4">
        <Card title="Fix these first" action={<LinkArrow onClick={() => go('risks')}>All {risks.length} risks</LinkArrow>} pad={false}>
          <ul>
            {risks.slice(0, 6).map((r) => (
              <li key={r.id} className="border-t border-line first:border-0">
                <button onClick={() => go('risks', r.id)} className="w-full text-left flex items-start gap-3 px-4 py-3 hover:bg-surface-2">
                  <div className="w-[76px] shrink-0 pt-px">
                    <SeverityPill s={r.severity} />
                  </div>
                  <div className="min-w-0 flex-1">
                    <div className="font-medium leading-snug">{r.title}</div>
                    <div className="text-[12.5px] text-ink-3 mt-0.5">
                      {r.rule}
                      {r.shareId && ` · ${s.shares.find((x) => x.id === r.shareId)?.unc}${r.scope ? '\\' + r.scope : ''}`}
                    </div>
                  </div>
                  <ArrowUpRight size={16} className="text-ink-3 shrink-0 mt-0.5" />
                </button>
              </li>
            ))}
          </ul>
        </Card>

        <Card title="What your file servers hold">
          <div className="flex h-3 rounded-full overflow-hidden gap-[2px] mb-3" role="img" aria-label="Files by classification">
            {CLASSIFICATIONS.map((c) =>
              totals[c] ? <div key={c} style={{ flexGrow: totals[c], background: classColor(c), minWidth: 4 }} title={`${c}: ${totals[c]}`} /> : null,
            )}
          </div>
          <div className="flex flex-col gap-1.5 mb-5">
            {[...CLASSIFICATIONS].reverse().map((c) => (
              <div key={c} className="flex items-center justify-between text-[13px]">
                <ClassPill c={c} />
                <span className="tnum text-ink-2">
                  {fmtNum(totals[c])} <span className="text-ink-3">({((totals[c] / totalFiles) * 100).toFixed(1)}%)</span>
                </span>
              </div>
            ))}
          </div>
          <p className="text-[12.5px] text-ink-3">
            Unknown means Data Scope could not read the file (unsupported, encrypted, corrupt or access denied). Unknown is never counted as safe.
          </p>
        </Card>
      </div>

      <div className="grid lg:grid-cols-[1.35fr_1fr] gap-4">
        <Card title="Sensitive files and exposure by share" action={<LinkArrow onClick={() => go('shares')}>Shares</LinkArrow>}>
          <ShareExposure onPick={(id) => go('shares', id)} users={users} />
        </Card>
        <div className="flex flex-col gap-4">
          <Card title="Access reviews" action={<LinkArrow onClick={() => go('reviews')}>Open</LinkArrow>}>
            <ul className="flex flex-col gap-3.5">
              {s.campaigns.map((c) => {
                const items = s.items.filter((i) => i.campaignId === c.id);
                const done = items.filter((i) => i.decision !== 'Pending').length;
                return (
                  <li key={c.id}>
                    <div className="flex items-center justify-between gap-2 mb-1.5">
                      <span className="text-[13.5px] font-medium truncate">{c.name}</span>
                      {c.status === 'Overdue' ? <Tag tone="bad">Overdue</Tag> : c.status === 'Completed' ? <Tag tone="good">Completed</Tag> : <Tag tone="accent">Due {inDays(c.dueAt)}</Tag>}
                    </div>
                    <Meter value={done / Math.max(1, items.length)} tone={c.status === 'Completed' ? 'good' : c.status === 'Overdue' ? 'warn' : 'accent'} />
                    <div className="text-[12px] text-ink-3 mt-1 tnum">
                      {done} of {items.length} decided · {nameOf(c.reviewerSid)}
                    </div>
                  </li>
                );
              })}
            </ul>
          </Card>
          <Card title="File activity alerts" action={<Tag tone="neutral">Preview</Tag>}>
            <ul className="flex flex-col gap-2.5">
              {s.alerts.filter((a) => a.state === 'Open').map((a) => (
                <li key={a.id} className="flex items-start gap-2.5">
                  <SeverityPill s={a.severity} />
                  <button onClick={() => go('activity')} className="text-left text-[13px] leading-snug hover:underline min-w-0">
                    <span className="font-medium">{a.rule}:</span> <span className="text-ink-2">{a.summary}</span>
                  </button>
                </li>
              ))}
              {!s.alerts.some((a) => a.state === 'Open') && (
                <li className="flex items-center gap-2 text-ink-3 text-[13px]">
                  <ShieldCheck size={16} /> No open alerts.
                </li>
              )}
            </ul>
          </Card>
        </div>
      </div>
    </>
  );
}

function Kpi({ label, value, note, tone, onClick }: { label: string; value: string; note: string; tone?: 'bad' | 'warn' | 'good'; onClick: () => void }) {
  const color = tone === 'bad' ? 'var(--s-critical)' : tone === 'warn' ? 'var(--s-medium)' : tone === 'good' ? 'var(--s-good)' : 'var(--ink)';
  return (
    <button onClick={onClick} className="text-left bg-surface border border-line rounded-xl px-4 py-3.5 hover:border-ink-3 transition-colors min-w-0">
      <div className="text-[12.5px] text-ink-2 font-medium">{label}</div>
      <div className="font-display text-[30px] font-[650] leading-none mt-2 tnum" style={{ color }}>
        {value}
      </div>
      <div className="text-[12px] text-ink-3 mt-1.5">{note}</div>
    </button>
  );
}

function ShareExposure({ onPick, users }: { onPick: (id: string) => void; users: number }) {
  const s = useApp();
  const rows = s.shares.map((sh) => shareStats(s, sh.id)).sort((a, b) => b.sensitive - a.sensitive);
  const max = Math.max(...rows.map((r) => r.sensitive), 1);
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-[13px] min-w-[460px]">
        <thead>
          <tr className="text-ink-3 text-[12px]">
            <th className="text-left font-medium pb-2">Share</th>
            <th className="text-left font-medium pb-2">
              <span className="inline-flex items-center gap-3">
                Sensitive files
                <span className="inline-flex items-center gap-1 font-normal">
                  <i className="size-2 rounded-sm" style={{ background: classColor('Restricted') }} /> Restricted
                </span>
                <span className="inline-flex items-center gap-1 font-normal">
                  <i className="size-2 rounded-sm" style={{ background: classColor('Confidential') }} /> Confidential
                </span>
              </span>
            </th>
            <th className="text-right font-medium pb-2 whitespace-nowrap">Who can open</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => {
            const pct = r.peopleWithAccess / users;
            return (
              <tr key={r.share.id} className="border-t border-line cursor-pointer hover:bg-surface-2" onClick={() => onPick(r.share.id)}>
                <td className="py-2 pr-3 font-medium whitespace-nowrap">{r.share.name}</td>
                <td className="py-2 pr-3 w-full">
                  <div className="flex items-center gap-2">
                    <div className="flex-1 flex h-2.5 gap-[2px]">
                      {r.byClass.Restricted > 0 && <div className="rounded-l-sm" style={{ width: `${(r.byClass.Restricted / max) * 100}%`, background: classColor('Restricted') }} title={`${r.byClass.Restricted} Restricted`} />}
                      {r.byClass.Confidential > 0 && <div className="rounded-r-sm" style={{ width: `${(r.byClass.Confidential / max) * 100}%`, background: classColor('Confidential') }} title={`${r.byClass.Confidential} Confidential`} />}
                    </div>
                    <span className="tnum w-6 text-right text-ink-2">{r.sensitive}</span>
                  </div>
                </td>
                <td className="py-2 text-right whitespace-nowrap tnum">
                  <span style={{ color: pct >= 0.5 && r.sensitive ? 'var(--s-critical)' : 'var(--ink-2)', fontWeight: pct >= 0.5 && r.sensitive ? 600 : 400 }}>
                    {r.peopleWithAccess}
                  </span>
                  <span className="text-ink-3"> / {users}</span>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function OwnerHome() {
  const s = useApp();
  const { go, role } = useNav();
  const mine = ownedShareIds(s, role.sid);
  const campaigns = s.campaigns.filter((c) => c.reviewerSid === role.sid);
  return (
    <>
      <PageHeader eyebrow="Data owner" title={`Hi ${role.label.split(' ')[0]}`} sub="You own the shares below. Keep their access tidy by finishing your reviews." />
      <div className="grid md:grid-cols-2 gap-4">
        {campaigns.map((c) => {
          const items = s.items.filter((i) => i.campaignId === c.id);
          const pending = items.filter((i) => i.decision === 'Pending').length;
          return (
            <Card key={c.id} title={c.name} action={c.status === 'Completed' ? <Tag tone="good">Completed</Tag> : <Tag tone="accent">Due {inDays(c.dueAt)}</Tag>}>
              <p className="text-ink-2 text-[13.5px] mb-3">
                {pending ? `${pending} of ${items.length} entries still need your decision.` : 'All entries decided. Thank you.'}
              </p>
              <Meter value={(items.length - pending) / Math.max(1, items.length)} />
              <div className="mt-4">
                <LinkArrow onClick={() => go('reviews', c.id)}>{pending ? 'Continue review' : 'View decisions'}</LinkArrow>
              </div>
            </Card>
          );
        })}
        {mine.map((id) => {
          const st = shareStats(s, id);
          return (
            <Card key={id} title={st.share.name} action={<LinkArrow onClick={() => go('shares', id)}>Details</LinkArrow>}>
              <div className="font-mono text-[12.5px] text-ink-3 mb-3">{st.share.unc}</div>
              <div className="flex flex-wrap gap-x-6 gap-y-2 text-[13px]">
                <span><b className="tnum">{st.files.length}</b> files</span>
                <span><b className="tnum">{st.sensitive}</b> sensitive</span>
                <span><b className="tnum">{st.peopleWithAccess}</b> people can open it</span>
              </div>
            </Card>
          );
        })}
      </div>
    </>
  );
}
