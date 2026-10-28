import { useState } from 'react';
import { Download, FileText, Printer } from 'lucide-react';
import { api } from '../../api/client';
import { ButtonLink, Callout, Empty, Field, PageHeader, cx, filterCls } from '../../ui/kit';
import { REPORT_PERIODS, parseScope, pickStandard, scopeOptions } from './reporting';
import { useData } from './useData';

/**
 * Reports made on the Data Scope server. Each one opens with when it was made, its scope, how fresh and complete the
 * data is, and what it cannot show. The list only holds reports the signed-in person's role allows.
 */
export default function LiveReports() {
  const reports = useData(() => api.reports(), []);
  const servers = useData(() => api.servers(), []);
  const shares = useData(() => api.shares(), []);
  const [selected, setSelected] = useState<string>();
  const [scope, setScope] = useState('');
  const [days, setDays] = useState(30);
  const [chosen, setChosen] = useState('');
  const list = reports.data ?? [];
  const report = list.find((r) => r.id === selected) ?? list[0];
  // Only people who can see a report about one standard need the list of standards.
  const needsStandards = list.some((r) => r.standard);
  const compliance = useData(() => (needsStandards ? api.complianceStandards() : Promise.resolve(undefined)), [needsStandards]);
  const standardsOn = (compliance.data?.standards ?? []).filter((s) => s.enabled);
  const options = scopeOptions(servers.data?.items ?? [], shares.data?.items ?? []);
  const scoped = parseScope(scope);
  const period = report?.period ? days : undefined;
  const standard = report?.standard ? pickStandard(standardsOn, chosen) : undefined;
  const ready = !report?.standard || standard !== undefined;

  return (
    <>
      <PageHeader
        title="Reports"
        sub="Made on your Data Scope server from the latest scans, directory sync and collected file activity. Every report starts with when it was made, what it covers, how complete the data is and what it cannot tell you."
      />
      {reports.error && <div className="mb-4"><Callout tone="bad" title="Can't reach the Data Scope server">{reports.error}</Callout></div>}
      {reports.data && list.length === 0 && (
        <Empty icon={<FileText size={20} />} title="No reports for your role">Ask a Data Scope administrator for the Auditor or Security analyst role.</Empty>
      )}
      {report && (
        <div className="grid lg:grid-cols-[300px_1fr] gap-4 items-start">
          <ul className="bg-surface border border-line rounded-xl overflow-hidden divide-y divide-[var(--line)]" aria-label="Reports">
            {list.map((r) => (
              <li key={r.id}>
                <button
                  onClick={() => setSelected(r.id)}
                  aria-current={r.id === report.id ? 'true' : undefined}
                  className={cx('w-full text-left px-4 py-3 hover:bg-surface-2', r.id === report.id && 'bg-accent-soft')}
                >
                  <span className="flex items-center gap-2 font-medium text-[13.5px]"><FileText size={15} className="text-ink-3 shrink-0" />{r.title}</span>
                </button>
              </li>
            ))}
          </ul>
          <section className="bg-surface border border-line rounded-xl min-w-0 p-4 space-y-4">
            <div>
              <h2 className="font-display text-lg font-[650]">{report.title}</h2>
              <p className="text-[13.5px] text-ink-2 mt-1 max-w-[70ch]">{report.about}</p>
            </div>
            <div className="flex flex-wrap gap-3">
              <Field label="Scope" htmlFor="report-scope">
                <select id="report-scope" className={cx(filterCls, 'min-w-[16rem]')} value={scope} onChange={(e) => setScope(e.target.value)}>
                  {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                </select>
              </Field>
              {report.standard && standardsOn.length > 0 && (
                <Field label="Standard" htmlFor="report-standard">
                  <select id="report-standard" className={filterCls} value={standard} onChange={(e) => setChosen(e.target.value)}>
                    {standardsOn.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
                  </select>
                </Field>
              )}
              {report.period && (
                <Field label="Period" htmlFor="report-period">
                  <select id="report-period" className={filterCls} value={days} onChange={(e) => setDays(Number(e.target.value))}>
                    {REPORT_PERIODS.map((p) => <option key={p.days} value={p.days}>{p.label}</option>)}
                  </select>
                </Field>
              )}
            </div>
            {report.standard && compliance.error && <Callout tone="bad" title="Can't load compliance standards">{compliance.error}</Callout>}
            {report.standard && compliance.data && !ready && (
              <Callout title="No compliance standard is turned on">
                This report is made for one standard. An administrator can turn one on in Settings, Compliance standards.
              </Callout>
            )}
            {ready && (!report.standard || compliance.data) && (
              <div className="flex flex-wrap gap-2">
                <ButtonLink variant="primary" href={api.reportUrl(report.id, 'csv', scoped, period, standard)} download><Download size={15} /> Download CSV</ButtonLink>
                <ButtonLink href={api.reportUrl(report.id, 'html', scoped, period, standard)} newTab><Printer size={15} /> Open printable page</ButtonLink>
              </div>
            )}
            <ul className="text-[12.5px] text-ink-3 space-y-1 list-disc pl-4 max-w-[75ch]">
              <li>To keep a PDF, open the printable page and print it to <b>Microsoft Print to PDF</b> or <b>Save as PDF</b>.</li>
              <li>The CSV holds up to 100,000 rows and the printable page up to 5,000. Both say when a report stopped early.</li>
              <li>Cells that start with = + - or @ are prefixed so spreadsheets don't run them as formulas.</li>
              <li>Each download is recorded in the Data Scope audit log with your name, the report and the scope.</li>
            </ul>
          </section>
        </div>
      )}
    </>
  );
}
