import { useState } from 'react';
import { Download, Printer } from 'lucide-react';
import { useNav } from '../../App';
import { api, type Progress } from '../../api/client';
import { ButtonLink, Callout, Card, PageHeader, Segmented, fmtNum } from '../../ui/kit';
import { MEASURES, changeOf, formatValue, limitedDays, rangeOf, seriesOf, shortRange, type Measure, RANGES } from './progress';
import { fmtDay } from './reporting';
import { TrendChart } from './TrendChart';
import { useData } from './useData';

const TONE = { good: 'var(--s-good)', bad: 'var(--s-critical)', neutral: 'var(--ink-3)' } as const;

/**
 * Progress over time (D165): the posture day by day, so a team can show that risks, exposure and sensitive
 * files are going down. Data Scope keeps one row of counts a day; no names, paths or contents.
 */
export default function LiveProgress() {
  const { param } = useNav();
  const [range, setRange] = useState(rangeOf(param));
  const days = Number(range);
  const data = useData(() => api.progress(days), [days], 5 * 60_000);
  const d = data.data?.days === days ? data.data : undefined;

  return (
    <div className="space-y-4">
      <PageHeader
        title="Progress over time"
        sub="Open risks, exposed sensitive files, coverage and ownership, day by day. Data Scope keeps the counts at the end of each day, so you can show what has improved since an audit, a project or a quarter began."
        actions={<Segmented value={range} onChange={setRange} options={RANGES} />}
      />
      {data.error && <Callout tone="bad" title="Can't reach the Data Scope server">{data.error}</Callout>}
      {d && <Body d={d} />}
    </div>
  );
}

function Body({ d }: { d: Progress }) {
  const measures = MEASURES;
  const short = shortRange(d);
  const limited = limitedDays(d.points);
  const first = d.points[0];
  return (
    <>
      {short && <Callout title="Kept from the day Data Scope was updated">{short}</Callout>}
      <div className="grid sm:grid-cols-2 lg:grid-cols-4 gap-3">
        {measures.slice(0, 8).map((m) => <ChangeTile key={m.id} d={d} m={m} />)}
      </div>

      <Card title="Day by day">
        {d.points.length < 2 ? (
          <p className="text-[13px] text-ink-2">
            Progress builds up one day at a time. Today is measured; the charts appear from tomorrow, and each scan, directory sync or decision updates the day it happens on.
          </p>
        ) : (
          <div className="grid sm:grid-cols-2 lg:grid-cols-3 gap-x-6 gap-y-5">
            {measures.map((m) => {
              const series = seriesOf(d.points, m);
              return series.length > 0 && (
                <TrendChart key={m.id} title={m.title} points={series} value={(p) => p.value} format={(n) => formatValue(m, n)} top={m.percent ? 100 : undefined} />
              );
            })}
          </div>
        )}
        {d.thinned && <p className="text-[12px] text-ink-3 mt-3">A long range shows the last day of each week.</p>}
      </Card>

      <Card title="For a board pack or an auditor">
        <p className="text-[13px] text-ink-2 max-w-[75ch]">
          The progress report lists every day in this range with the change from {first ? fmtDay(first.day) : 'its first day'} to today. It holds counts only: no names, paths or file contents.
        </p>
        <div className="mt-3 flex flex-wrap gap-2">
          <ButtonLink href={api.reportUrl('progress', 'html', {}, d.days)} newTab><Printer size={15} /> Open printable page</ButtonLink>
          <ButtonLink href={api.reportUrl('progress', 'csv', {}, d.days)} download><Download size={15} /> Download CSV</ButtonLink>
        </div>
      </Card>

      <Card title="How to read this">
        <ul className="text-[13px] text-ink-2 list-disc pl-5 space-y-1 max-w-3xl">
          <li>Each day shows Data Scope's last measurement that day, after the day's last scan, directory sync or decision. Days on which the server was off have no point.</li>
          <li>
            Counts follow what the scans found. A share or library scanned for the first time raises them: that is new coverage, not new problems. A file not read is a
            gap, not clean.
          </li>
          <li>Open risks leave out risks accepted or snoozed. Risk rules can change with an update to Data Scope, so a count can move without anything on the servers changing.</li>
          {limited > 0 && (
            <li>
              On {fmtNum(limited)} {limited === 1 ? 'day' : 'days'} in this range the risk check stopped early somewhere, so those days' risk counts may be low.
            </li>
          )}
        </ul>
      </Card>
    </>
  );
}

function ChangeTile({ d, m }: { d: Progress; m: Measure }) {
  const { go } = useNav();
  const change = changeOf(d.points, m);
  const body = (
    <>
      <div className="text-[12px] text-ink-3">{m.title}</div>
      <div className="font-display text-[22px] font-[650] tnum mt-0.5">{change ? formatValue(m, change.now) : '—'}</div>
      <div className="text-[12px] tnum" style={{ color: TONE[change?.tone ?? 'neutral'] }}>{change?.text ?? m.about}</div>
    </>
  );
  return m.link ? (
    <button className="bg-surface border border-line rounded-xl px-4 py-3 text-left hover:border-ink-3" title={m.about} onClick={() => go(m.link!.route, m.link!.param)}>{body}</button>
  ) : (
    <div className="bg-surface border border-line rounded-xl px-4 py-3" title={m.about}>{body}</div>
  );
}
