import { ShieldCheck } from 'lucide-react';
import { api, type Dashboard } from '../../api/client';
import { useBackend } from '../../api/backend';
import { liveRoutes, useNav, type Route } from '../../App';
import { Callout, Card, LinkArrow, Meter, PageHeader, Tag, Unc, ago, fmtBytes, fmtNum } from '../../ui/kit';
import { EXPOSURE, rangeText } from './access';
import { coverageNote, exposureNote, readPercent } from './reporting';
import { TrendChart } from './TrendChart';
import { useData } from './useData';

/** The posture at a glance, from the latest scans and directory sync. Every number links to the screen behind it. */
export default function LiveOverview() {
  const backend = useBackend();
  const { go } = useNav();
  const allowed = liveRoutes(backend);
  const dash = useData(() => api.dashboard(), [], 60000);
  const d = dash.data;
  const open = (route: Route, param?: string) => (allowed.has(route) ? () => go(route, param) : undefined);

  return (
    <>
      <PageHeader
        eyebrow={d?.domain ?? undefined}
        title="Overview"
        sub={d && (d.coverage.lastScanAt
          ? <>Last scan finished {ago(d.coverage.lastScanAt)} across {d.coverage.sharesIncluded} shares on {d.coverage.servers} file {d.coverage.servers === 1 ? 'server' : 'servers'}.</>
          : 'Nothing has been scanned yet. Add a file server under Servers & scans to begin.')}
      />
      {dash.error && <div className="mb-4"><Callout tone="bad" title="Can't reach the Data Scope server">{dash.error}</Callout></div>}
      {d && <Body d={d} open={open} />}
    </>
  );
}

function Body({ d, open }: { d: Dashboard; open: (route: Route, param?: string) => (() => void) | undefined }) {
  const read = readPercent(d);
  const sensitive = d.sensitive.restricted + d.sensitive.confidential;
  const maxAge = Math.max(1, ...d.age.map((a) => a.files));
  return (
    <>
      <div className="grid grid-cols-2 lg:grid-cols-4 gap-3 mb-5">
        <Kpi label="Sensitive files" value={fmtNum(sensitive)} note={`${fmtNum(d.sensitive.restricted)} Restricted, ${fmtNum(d.sensitive.confidential)} Confidential`} onClick={open('discovery')} />
        <Kpi
          label="Sensitive files open to everyone"
          value={fmtNum(d.exposure.sensitiveFilesOpen)}
          tone={d.exposure.open ? 'bad' : d.exposure.possiblyOpen ? 'warn' : 'good'}
          note={exposureNote(d)}
          onClick={open('access')}
        />
        <Kpi
          label="Files read"
          value={read === null ? '—' : `${read}%`}
          tone={d.coverage.filesNotRead || d.coverage.gaps ? 'warn' : undefined}
          note={`${coverageNote(d)}. ${plural(d.coverage.filesNotRead, 'file')} not read, ${plural(d.coverage.gaps, 'gap')}.`}
          onClick={open('shares')}
        />
        <Kpi
          label="Access reviews waiting"
          value={fmtNum(d.reviews.reviewsOpen)}
          tone={d.reviews.reviewsOverdue ? 'warn' : undefined}
          note={d.reviews.openCampaigns === 0 ? 'No campaign running' : d.reviews.reviewsOverdue ? `${d.reviews.reviewsOverdue} overdue` : `In ${d.reviews.openCampaigns} running ${d.reviews.openCampaigns === 1 ? 'campaign' : 'campaigns'}, none overdue`}
          onClick={open('reviews')}
        />
      </div>

      <div className="grid lg:grid-cols-[1.35fr_1fr] gap-4 mb-4">
        <Card title="Fix these first" action={open('access') && <LinkArrow onClick={open('access')!}>Who has access</LinkArrow>} pad={false}>
          {d.exposure.top.length === 0 ? (
            <div className="px-4 py-6 flex items-center gap-3 text-ink-2 text-[13.5px]">
              <ShieldCheck size={18} style={{ color: 'var(--s-good)' }} />
              No folder with sensitive files is open, or possibly open, to everyone in the domain.
            </div>
          ) : (
            <ul className="divide-y divide-[var(--line)]">
              {d.exposure.top.map((e) => {
                const onClick = open('access', `folder:${e.folderId}`);
                return (
                  <li key={e.folderId}>
                    <button className="w-full text-left px-4 py-3 hover:bg-surface-2 disabled:hover:bg-transparent" onClick={onClick} disabled={!onClick}>
                      <div className="flex flex-wrap items-center gap-2">
                        <Tag tone={EXPOSURE[e.status].tone === 'bad' ? 'bad' : 'warn'}>{EXPOSURE[e.status].label}</Tag>
                        <Unc>{e.uncPath}</Unc>
                      </div>
                      <div className="text-[12.5px] text-ink-3 mt-1">
                        {[e.restrictedFiles && `${fmtNum(e.restrictedFiles)} Restricted`, e.confidentialFiles && `${fmtNum(e.confidentialFiles)} Confidential`].filter(Boolean).join(', ')}
                        {' · '}any domain user: {rangeText(e.openAccess).toLowerCase()}
                      </div>
                    </button>
                  </li>
                );
              })}
            </ul>
          )}
        </Card>

        <Card title="Ownership">
          <dl className="grid grid-cols-[1fr_auto] gap-y-2 text-[13.5px]">
            <Line label="Shares with an owner" value={d.owners.owned} onClick={open('owners')} />
            <Line label="Shares with no owner" value={d.owners.unowned} tone={d.owners.unowned ? 'warn' : undefined} onClick={open('owners')} />
            <Line label="Owners disabled or gone from AD" value={d.owners.orphanedOwners} tone={d.owners.orphanedOwners ? 'bad' : undefined} onClick={open('owners')} />
          </dl>
          {open('reports') && <div className="mt-4"><LinkArrow onClick={open('reports')!}>Reports for auditors</LinkArrow></div>}
        </Card>
      </div>

      <Card title="Over time" className="mb-4">
        {d.trend.length < 2 ? (
          <p className="text-[13px] text-ink-2">
            Trends appear once scans have finished on two different days. Each finished scan keeps its totals, so the chart builds up from here.
          </p>
        ) : (
          <>
            <div className="grid sm:grid-cols-3 gap-x-6 gap-y-5">
              <TrendChart title="Files" points={d.trend} value={(p) => p.files} />
              <TrendChart title="Sensitive files" points={d.trend} value={(p) => p.restricted + p.confidential} />
              <TrendChart title="Files not read" points={d.trend} value={(p) => p.filesNotRead} />
            </div>
            <p className="text-[12px] text-ink-3 mt-3">
              Each point adds up every included share's latest scan as of that day. A share's first scan makes the totals jump; that is new coverage, not new files.
            </p>
          </>
        )}
        {open('progress') && (
          <div className="mt-3">
            <LinkArrow onClick={open('progress')!}>Progress on risks, exposure and labels</LinkArrow>
          </div>
        )}
      </Card>

      <Card title="Files by last change">
        <ul className="space-y-2.5">
          {d.age.map((a) => (
            <li key={a.label} className="grid grid-cols-[minmax(8rem,12rem)_1fr_auto] items-center gap-3 text-[13px]">
              <span className="text-ink-2">{a.label}</span>
              <Meter value={a.files / maxAge} label={a.label} />
              <span className="tnum text-right text-ink-2 whitespace-nowrap">{fmtNum(a.files)} files · {fmtBytes(a.bytes)}</span>
            </li>
          ))}
        </ul>
        <p className="text-[12px] text-ink-3 mt-3">
          From each file's last-write time as Windows reports it. Last-access time is often switched off or inaccurate, so Data Scope doesn't use it. Old files are candidates to review, not to delete automatically.
        </p>
      </Card>
      <p className="text-[12px] text-ink-3 mt-4">
        Classification policy {d.sensitive.policyVersion}.{' '}
        {d.directorySyncedAt ? `Active Directory synced ${ago(d.directorySyncedAt)}.` : 'Active Directory has not been synced, so access through groups is not counted yet.'}
        {d.exposure.truncated && ' Only the 20,000 folders with the most sensitive files were checked for exposure.'}
      </p>
    </>
  );
}

const plural = (n: number, word: string) => `${fmtNum(n)} ${word}${n === 1 ? '' : 's'}`;

function Kpi({ label, value, note, tone, onClick }: { label: string; value: string; note: string; tone?: 'bad' | 'warn' | 'good'; onClick?: () => void }) {
  const color = tone === 'bad' ? 'var(--s-critical)' : tone === 'warn' ? 'var(--s-medium)' : tone === 'good' ? 'var(--s-good)' : 'var(--ink)';
  return (
    <button onClick={onClick} disabled={!onClick} className="text-left bg-surface border border-line rounded-xl px-4 py-3.5 enabled:hover:border-ink-3 transition-colors min-w-0">
      <div className="text-[12.5px] text-ink-2 font-medium">{label}</div>
      <div className="font-display text-[30px] font-[650] leading-none mt-2 tnum" style={{ color }}>{value}</div>
      <div className="text-[12px] text-ink-3 mt-1.5">{note}</div>
    </button>
  );
}

function Line({ label, value, tone, onClick }: { label: string; value: number; tone?: 'bad' | 'warn'; onClick?: () => void }) {
  const color = tone === 'bad' ? 'var(--s-critical)' : tone === 'warn' ? 'var(--s-medium)' : undefined;
  return (
    <>
      <dt>{onClick ? <button className="text-left hover:underline" onClick={onClick}>{label}</button> : label}</dt>
      <dd className="tnum text-right font-medium" style={{ color }}>{fmtNum(value)}</dd>
    </>
  );
}
