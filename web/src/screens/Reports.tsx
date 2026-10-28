import { useBackend } from '../api/backend';
import LiveReports from './live/LiveReports';
import { useMemo, useState } from 'react';
import { Copy, FileText } from 'lucide-react';
import { actions, useApp, type AppState } from '../api/store';
import { useNav } from '../App';
import { membersOf } from '../domain/access';
import { POLICY_VERSION } from '../domain/detectors';
import { toCsv } from '../domain/csv';
import { effectiveClass, type Risk } from '../domain/risk';
import { Button, PageHeader, Tag, cx, fmtDateTime, nameOf, rightsLabel } from '../ui/kit';
import { shareStats } from './common';

interface Report {
  id: string;
  name: string;
  about: string;
  build: (s: AppState, risks: Risk[]) => { header: string[]; rows: (string | number)[][]; scope: string; limits: string };
}

const REPORTS: Report[] = [
  {
    id: 'sensitive',
    name: 'Sensitive data inventory',
    about: 'Every file with possible sensitive data, its classification, rule IDs and confidence.',
    build: (s) => ({
      header: ['Share', 'Path', 'Classification', 'Source', 'Categories', 'Rule IDs', 'Highest confidence', 'Modified'],
      rows: s.files
        .filter((f) => f.findings.length || f.override)
        .map((f) => [
          s.shares.find((x) => x.id === f.shareId)!.unc,
          f.path,
          effectiveClass(f),
          f.override ? `Reviewer (${nameOf(f.override.by)})` : 'Policy',
          f.findings.map((x) => `${x.category} x${x.count}`).join('; '),
          f.findings.map((x) => x.ruleId).join('; '),
          f.findings.some((x) => x.confidence === 'high') ? 'high' : f.findings.some((x) => x.confidence === 'medium') ? 'medium' : 'low',
          f.modified.slice(0, 10),
        ]),
      scope: 'All enrolled shares',
      limits: 'Possible matches only. Unscanned files are listed in the coverage report and are not included here.',
    }),
  },
  {
    id: 'shares',
    name: 'Share and owner inventory',
    about: 'Shares, owners, review cadence, sensitive file counts and exposure.',
    build: (s) => ({
      header: ['Share', 'Server', 'Owner', 'Backup owner', 'Cadence', 'Files', 'Sensitive files', 'Active accounts with access', 'Last scan'],
      rows: s.shares.map((sh) => {
        const o = s.owners.find((x) => x.shareId === sh.id);
        const st = shareStats(s, sh.id);
        return [sh.unc, s.servers.find((x) => x.id === sh.serverId)!.hostname, o?.confirmed && o.ownerSid ? nameOf(o.ownerSid) : 'UNOWNED', o?.backupOwnerSid ? nameOf(o.backupOwnerSid) : '', o?.cadence ?? '', st.files.length, st.sensitive, st.peopleWithAccess, sh.scan.lastScan?.slice(0, 10) ?? 'never'];
      }),
      scope: 'All enrolled shares',
      limits: 'Access counts are computed from share + NTFS snapshots, not Windows access checks.',
    }),
  },
  {
    id: 'matrix',
    name: 'AD group access matrix',
    about: 'Which groups appear on which share, with rights and effective member counts.',
    build: (s) => ({
      header: ['Group or account', 'Share', 'Scope', 'ACL', 'Type', 'Rights', 'Effective members'],
      rows: s.shares.flatMap((sh) =>
        [
          ...sh.shareAcl.map((a) => ({ a, where: 'Share', scope: '' })),
          ...sh.ntfsAcl.map((a) => ({ a, where: 'NTFS', scope: '' })),
          ...sh.folders.flatMap((f) => (f.ntfsAcl ?? []).map((a) => ({ a, where: 'NTFS', scope: f.path }))),
        ].map(({ a, where, scope }) => [s.dir.get(a.sid)?.displayName ?? a.sid, sh.unc, scope || '(root)', where, a.type, rightsLabel(a.rights), s.dir.get(a.sid) ? membersOf(s.dir, a.sid).length : 'unknown']),
      ),
      scope: 'All enrolled shares',
      limits: 'Unresolved SIDs have unknown membership.',
    }),
  },
  {
    id: 'risky',
    name: 'Risky permissions',
    about: 'All open risks with severity factors and recommended fixes.',
    build: (s, risks) => ({
      header: ['Severity', 'Rule', 'Finding', 'Share', 'Sensitivity', 'Exposure', 'Confidence', 'Recommended fix'],
      rows: risks.map((r) => [r.severity, r.rule, r.title, s.shares.find((x) => x.id === r.shareId)?.unc ?? '', r.factors.sensitivity, r.factors.exposure, r.factors.confidence, r.fix]),
      scope: 'All enrolled shares and the NORTHWIND directory',
      limits: 'Severity is rule-based and explained per row; no opaque scoring.',
    }),
  },
  {
    id: 'evidence',
    name: 'Access certification evidence',
    about: 'Every reviewed entry with snapshot time, reviewer, decision and justification.',
    build: (s) => ({
      header: ['Campaign', 'Snapshot (UTC)', 'Share', 'Scope', 'Account or group', 'Rights', 'Members at snapshot', 'Decision', 'Reviewer', 'Decided (UTC)', 'Justification'],
      rows: s.items.map((i) => {
        const c = s.campaigns.find((x) => x.id === i.campaignId)!;
        return [c.name, c.snapshotAt, s.shares.find((x) => x.id === i.shareId)!.unc, i.scope || '(root)', s.dir.get(i.trusteeSid)?.displayName ?? i.trusteeSid, rightsLabel(i.rights), i.memberCount, i.decision, i.decidedBy ? nameOf(i.decidedBy) : '', i.decidedAt ?? '', i.justification ?? ''];
      }),
      scope: 'All campaigns',
      limits: 'Decisions are stored append-only with full history; this export shows the latest decision per entry.',
    }),
  },
  {
    id: 'coverage',
    name: 'Scan coverage and failures',
    about: 'Files Data Scope could not read, by share and reason.',
    build: (s) => ({
      header: ['Share', 'Files', 'Scanned', 'Unsupported', 'Encrypted', 'Corrupt', 'Access denied', 'Coverage'],
      rows: s.shares.map((sh) => {
        const fs = s.files.filter((f) => f.shareId === sh.id);
        const n = (st: string) => fs.filter((f) => f.scanStatus === st).length;
        return [sh.unc, fs.length, n('scanned'), n('unsupported'), n('encrypted'), n('corrupt'), n('denied'), `${Math.round((n('scanned') / Math.max(1, fs.length)) * 100)}%`];
      }),
      scope: 'All enrolled shares',
      limits: 'Unscanned files are not clean. Text read from images and scanned PDFs by OCR may contain misread characters.',
    }),
  },
  {
    id: 'audit',
    name: 'Data Scope admin log',
    about: 'Administrative actions inside Data Scope: launches, overrides, exports.',
    build: (s) => ({
      header: ['When (UTC)', 'Who', 'Action', 'Target'],
      rows: s.appLog.map((l) => [l.at, nameOf(l.actorSid), l.action, l.target]),
      scope: 'This installation',
      limits: 'Append-only with an integrity hash chain in the production build.',
    }),
  },
];

export default function Reports() {
  const backend = useBackend();
  return backend.mode === 'live' ? <LiveReports /> : <DemoReports />;
}

function DemoReports() {
  const s = useApp();
  const { param, risks, role } = useNav();
  const [sel, setSel] = useState(param?.startsWith('evidence') ? 'evidence' : 'sensitive');
  const [copied, setCopied] = useState(false);
  const report = REPORTS.find((r) => r.id === sel)!;
  const generated = useMemo(() => new Date().toISOString(), [sel]); // eslint-disable-line react-hooks/exhaustive-deps
  const data = useMemo(() => report.build(s, risks), [report, s, risks]);
  const lastScan = s.shares.map((x) => x.scan.lastScan).filter(Boolean).sort()[0];
  const preamble = [
    `Data Scope report: ${report.name}`,
    `Generated ${generated} by ${nameOf(role.sid)}`,
    `Scope: ${data.scope}`,
    `Policy version: ${POLICY_VERSION}`,
    `Oldest scan in scope: ${lastScan}`,
    `Known limitations: ${data.limits}`,
  ];
  const csv = () => toCsv(data.header, data.rows, preamble);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(csv());
      setCopied(true);
      setTimeout(() => setCopied(false), 1800);
    } catch {
      actions.toast('Copy was blocked by the browser. Select the preview text instead.', 'warn');
    }
    actions.logExport(report.name);
  };

  return (
    <>
      <PageHeader title="Reports" sub="Audit-ready exports. Every report states when it was generated, its scope, the policy version, how fresh the data is, and what it cannot tell you." />
      <div className="grid lg:grid-cols-[280px_1fr] gap-4 items-start">
        <ul className="bg-surface border border-line rounded-xl overflow-hidden divide-y divide-[var(--line)]">
          {REPORTS.map((r) => (
            <li key={r.id}>
              <button onClick={() => setSel(r.id)} className={cx('w-full text-left px-4 py-3 hover:bg-surface-2', sel === r.id && 'bg-accent-soft')}>
                <span className="flex items-center gap-2 font-medium text-[13.5px]"><FileText size={15} className="text-ink-3" />{r.name}</span>
                <span className="block text-[12px] text-ink-3 mt-0.5">{r.about}</span>
              </button>
            </li>
          ))}
        </ul>
        <section className="bg-surface border border-line rounded-xl min-w-0">
          <header className="flex flex-wrap items-start justify-between gap-3 px-4 py-3 border-b border-line">
            <div className="min-w-0">
              <h2 className="font-display text-lg font-[650]">{report.name}</h2>
              <div className="text-[12.5px] text-ink-3 mt-0.5">
                {data.rows.length} rows · generated {fmtDateTime(generated)} · policy {POLICY_VERSION}
              </div>
            </div>
            <div className="flex gap-2">
              <Button variant="primary" onClick={copy}><Copy size={15} /> {copied ? 'Copied' : 'Copy CSV'}</Button>
              <Button disabled title="PDF export arrives with milestone M5">PDF</Button>
            </div>
          </header>
          <div className="px-4 py-3 text-[12.5px] text-ink-2 flex flex-col gap-1 border-b border-line">
            <span><b>Scope:</b> {data.scope}</span>
            <span><b>Limitations:</b> {data.limits}</span>
            <span className="text-ink-3">Cells that start with = + - or @ are prefixed so spreadsheets do not run them as formulas.</span>
          </div>
          <div className="overflow-x-auto">
            <table className="text-[12.5px] w-full">
              <thead>
                <tr>{data.header.map((h) => <th key={h} className="text-left font-medium text-ink-3 px-3 py-2 whitespace-nowrap">{h}</th>)}</tr>
              </thead>
              <tbody>
                {data.rows.slice(0, 40).map((r, i) => (
                  <tr key={i} className="border-t border-line">
                    {r.map((c, j) => <td key={j} className="px-3 py-1.5 whitespace-nowrap max-w-[320px] truncate" title={String(c)}>{String(c)}</td>)}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {data.rows.length > 40 && <div className="px-4 py-2 text-[12px] text-ink-3 border-t border-line">Preview shows 40 of {data.rows.length} rows. The CSV has all of them.</div>}
          {sel === 'evidence' && <div className="px-4 py-2 border-t border-line"><Tag tone="neutral">Includes {s.campaigns.length} campaigns</Tag></div>}
        </section>
      </div>
    </>
  );
}
