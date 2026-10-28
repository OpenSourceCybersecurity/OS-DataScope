import { useMemo, useState } from 'react';
import { EyeOff, FileWarning, Search } from 'lucide-react';
import { actions, useApp } from '../api/store';
import { useNav } from '../App';
import { whoHasAccess } from '../domain/access';
import { CONFIDENCE_LABEL, POLICY_VERSION, RULES } from '../domain/detectors';
import { effectiveClass } from '../domain/risk';
import { CLASSIFICATIONS, type Classification, type FileItem, type FindingCategory } from '../domain/types';
import {
  Button,
  Callout,
  ClassPill,
  ConfidencePill,
  Drawer,
  Empty,
  Field,
  PageHeader,
  Person,
  Segmented,
  Tag,
  Td,
  Th,
  Unc,
  ago,
  fmtBytes,
  fmtDateTime,
  filterCls,
  inputCls,
  nameOf,
} from '../ui/kit';
import { sharePath } from './common';
import { useBackend } from '../api/backend';
import LiveDiscovery from './live/LiveDiscovery';

const CATEGORIES: FindingCategory[] = ['US SSN', 'Payment card', 'Bank account', 'Credentials', 'Health information', 'Date of birth'];
const STATUS_LABEL: Record<FileItem['scanStatus'], string> = {
  scanned: 'Scanned',
  unsupported: 'Unsupported format',
  encrypted: 'Encrypted or password protected',
  denied: 'Access denied to scanner',
  corrupt: 'Corrupt or malformed',
  pending: 'Waiting for scan',
};

export default function Discovery() {
  const backend = useBackend();
  return backend.mode === 'live' ? <LiveDiscovery /> : <DemoDiscovery />;
}

function DemoDiscovery() {
  const s = useApp();
  const { param } = useNav();
  const [view, setView] = useState<'sensitive' | 'unscanned'>('sensitive');
  const [cat, setCat] = useState<FindingCategory | 'all'>('all');
  const [share, setShare] = useState('all');
  const [cls, setCls] = useState<Classification | 'all'>('all');
  const [q, setQ] = useState('');
  const [open, setOpen] = useState<string | undefined>(param);

  const sensitive = s.files.filter((f) => f.findings.length || f.override);
  const catCounts = Object.fromEntries(CATEGORIES.map((c) => [c, sensitive.filter((f) => f.findings.some((x) => x.category === c)).length]));
  const base = view === 'sensitive' ? sensitive : s.files.filter((f) => f.scanStatus !== 'scanned');
  const rows = useMemo(
    () =>
      base
        .filter((f) => cat === 'all' || f.findings.some((x) => x.category === cat))
        .filter((f) => share === 'all' || f.shareId === share)
        .filter((f) => cls === 'all' || effectiveClass(f) === cls)
        .filter((f) => !q || f.path.toLowerCase().includes(q.toLowerCase()))
        .sort((a, b) => CLASSIFICATIONS.indexOf(effectiveClass(b)) - CLASSIFICATIONS.indexOf(effectiveClass(a)) || b.findings.length - a.findings.length),
    [base, cat, share, cls, q],
  );

  return (
    <>
      <PageHeader
        title="Sensitive data"
        sub={<>Possible matches found by content scanning, with confidence and masked evidence. Data Scope never stores full file contents or unmasked values.</>}
        actions={<Tag tone="neutral">Policy {POLICY_VERSION}</Tag>}
      />
      <div className="flex flex-wrap gap-2 mb-4">
        <CatChip active={cat === 'all'} onClick={() => setCat('all')} label="All types" n={sensitive.length} />
        {CATEGORIES.map((c) => (
          <CatChip key={c} active={cat === c} onClick={() => setCat(c)} label={c} n={catCounts[c]} />
        ))}
      </div>
      <div className="flex flex-wrap items-center gap-2 mb-3">
        <Segmented
          value={view}
          onChange={(v) => {
            setView(v);
            setCat('all');
          }}
          options={[
            { id: 'sensitive', label: 'Findings' },
            { id: 'unscanned', label: `Could not scan (${s.files.filter((f) => f.scanStatus !== 'scanned').length})` },
          ]}
        />
        <div className="relative">
          <Search size={15} className="absolute left-2.5 top-1/2 -translate-y-1/2 text-ink-3" />
          <input id="file-filter" className={filterCls + ' pl-8 w-56'} placeholder="Filter by path" value={q} onChange={(e) => setQ(e.target.value)} />
        </div>
        <select id="share-filter" aria-label="Share" className={filterCls} value={share} onChange={(e) => setShare(e.target.value)}>
          <option value="all">All shares</option>
          {s.shares.map((x) => (
            <option key={x.id} value={x.id}>{x.name}</option>
          ))}
        </select>
        <select id="class-filter" aria-label="Classification" className={filterCls} value={cls} onChange={(e) => setCls(e.target.value as Classification | 'all')}>
          <option value="all">Any classification</option>
          {CLASSIFICATIONS.map((c) => (
            <option key={c}>{c}</option>
          ))}
        </select>
        <span className="text-[12.5px] text-ink-3 ml-auto tnum">{rows.length} files</span>
      </div>
      {view === 'unscanned' && (
        <div className="mb-3">
          <Callout tone="warn" title="Not scanned is not clean">
            These files may still hold sensitive data. They count against coverage until a supported parser, permission fix or exclusion resolves them.
          </Callout>
        </div>
      )}
      <div className="bg-surface border border-line rounded-xl overflow-x-auto">
        <table className="w-full text-[13.5px] min-w-[860px]">
          <thead>
            <tr>
              <Th>File</Th>
              <Th>Classification</Th>
              <Th>{view === 'sensitive' ? 'Found' : 'Reason'}</Th>
              <Th>Confidence</Th>
              <Th>Modified</Th>
            </tr>
          </thead>
          <tbody>
            {rows.slice(0, 200).map((f) => {
              const top = f.findings.reduce<FileItem['findings'][number] | undefined>((a, x) => (!a || (x.confidence === 'high' && a.confidence !== 'high') ? x : a), undefined);
              return (
                <tr key={f.id} className="border-t border-line cursor-pointer hover:bg-surface-2" onClick={() => setOpen(f.id)}>
                  <Td className="max-w-[420px]">
                    <div className="font-medium truncate">{f.name}</div>
                    <div className="text-[12px] text-ink-3 truncate font-mono">{s.shares.find((x) => x.id === f.shareId)?.name}\{f.path.replace(/\//g, '\\')}</div>
                  </Td>
                  <Td><ClassPill c={effectiveClass(f)} overridden={!!f.override} /></Td>
                  <Td>
                    {view === 'sensitive' ? (
                      <div className="flex flex-wrap gap-1">
                        {f.findings.map((x) => (
                          <Tag key={x.ruleId}>{x.category} <span className="text-ink-3 tnum">×{x.count}</span></Tag>
                        ))}
                        {!f.findings.length && <span className="text-ink-3 text-[12.5px]">Reviewer set</span>}
                      </div>
                    ) : (
                      <span className="text-ink-2">{STATUS_LABEL[f.scanStatus]}</span>
                    )}
                  </Td>
                  <Td>{top ? <ConfidencePill c={top.confidence} /> : <span className="text-ink-3">—</span>}</Td>
                  <Td className="whitespace-nowrap text-ink-2">{ago(f.modified)}</Td>
                </tr>
              );
            })}
          </tbody>
        </table>
        {!rows.length && <Empty icon={<EyeOff size={18} />} title="No files match these filters" />}
        {rows.length > 200 && <div className="px-4 py-2 text-[12.5px] text-ink-3 border-t border-line">Showing the first 200 of {rows.length}. Narrow the filters to see more.</div>}
      </div>
      {open && <FileDrawer fileId={open} onClose={() => setOpen(undefined)} />}
    </>
  );
}

function CatChip({ label, n, active, onClick }: { label: string; n: number; active: boolean; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      className={`h-8 px-3 rounded-full border text-[13px] inline-flex items-center gap-2 ${active ? 'border-accent bg-accent-soft text-ink font-medium' : 'border-line bg-surface text-ink-2 hover:border-ink-3'}`}
    >
      {label}
      <span className="tnum text-ink-3">{n}</span>
    </button>
  );
}

function FileDrawer({ fileId, onClose }: { fileId: string; onClose: () => void }) {
  const s = useApp();
  const { readOnly, go } = useNav();
  const f = s.files.find((x) => x.id === fileId)!;
  const share = s.shares.find((x) => x.id === f.shareId)!;
  const folder = f.path.split('/').slice(0, -1).join('/');
  const access = useMemo(() => whoHasAccess(s.dir, share, folder || undefined).filter((a) => s.dir.get(a.userSid)?.enabled), [s.dir, share, folder]);
  const [cls, setCls] = useState<Classification>(effectiveClass(f));
  const [reason, setReason] = useState('');
  const [editing, setEditing] = useState(false);

  return (
    <Drawer open onClose={onClose} title={f.name} sub={<Unc>{sharePath(s, f)}</Unc>} width={620}>
      <div className="flex flex-col gap-5">
        <div className="flex flex-wrap items-center gap-3">
          <ClassPill c={effectiveClass(f)} overridden={!!f.override} />
          {f.override && <span className="text-[12.5px] text-ink-3">Policy said {f.policyClassification}. Changed by {nameOf(f.override.by)} {ago(f.override.at)}: “{f.override.reason}”</span>}
          {!readOnly && !editing && <Button size="sm" variant="ghost" onClick={() => setEditing(true)}>Change classification</Button>}
        </div>
        {editing && (
          <form
            className="bg-surface-2 rounded-lg p-3 flex flex-col gap-3"
            onSubmit={(e) => {
              e.preventDefault();
              if (!reason.trim()) return;
              actions.overrideClassification(f.id, cls, reason.trim());
              setEditing(false);
              setReason('');
            }}
          >
            <div className="flex flex-wrap gap-1.5">
              {CLASSIFICATIONS.filter((c) => c !== 'Unknown').map((c) => (
                <button type="button" key={c} onClick={() => setCls(c)} className={`rounded-full ${cls === c ? 'ring-2 ring-[var(--accent)]' : ''}`}>
                  <ClassPill c={c} />
                </button>
              ))}
            </div>
            <Field label="Reason (required, kept in history)" htmlFor="override-reason">
              <input id="override-reason" className={inputCls} value={reason} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Part numbers, not SSNs" />
            </Field>
            <div className="flex gap-2">
              <Button type="submit" variant="primary" size="sm" disabled={!reason.trim()}>Save</Button>
              <Button size="sm" variant="ghost" onClick={() => setEditing(false)}>Cancel</Button>
              {f.override && <Button size="sm" variant="ghost" onClick={() => { actions.clearOverride(f.id); setEditing(false); }}>Revert to policy</Button>}
            </div>
          </form>
        )}

        {f.scanStatus !== 'scanned' && (
          <Callout tone="warn" title={STATUS_LABEL[f.scanStatus]}>
            Data Scope could not read this file, so its classification is Unknown. It is counted as a coverage gap, not as clean.
          </Callout>
        )}

        {!!f.findings.length && (
          <div>
            <div className="eyebrow mb-2">What we found</div>
            <ul className="flex flex-col gap-3">
              {f.findings.map((x) => {
                const rule = RULES.find((r) => r.id === x.ruleId)!;
                return (
                  <li key={x.ruleId} className="border border-line rounded-lg p-3">
                    <div className="flex flex-wrap items-center justify-between gap-2 mb-1.5">
                      <span className="font-medium">{rule.name} <span className="text-ink-3 font-normal tnum">× {x.count}</span></span>
                      <ConfidencePill c={x.confidence} />
                    </div>
                    <div className="text-[12px] text-ink-3 mb-2">
                      <span className="font-mono">{x.ruleId}</span> · {rule.description}
                    </div>
                    <ul className="flex flex-col gap-1">
                      {x.evidence.map((ev, i) => (
                        <li key={i} className="font-mono text-[12px] bg-surface-2 rounded px-2 py-1 break-all">{ev}</li>
                      ))}
                    </ul>
                    <div className="text-[11.5px] text-ink-3 mt-1.5">{CONFIDENCE_LABEL[x.confidence]}. Masked at scan time; the full value is never stored.</div>
                  </li>
                );
              })}
            </ul>
          </div>
        )}

        <div>
          <div className="eyebrow mb-2">Who can open this file</div>
          <p className="text-[13px] text-ink-2 mb-2">
            <b className="tnum">{access.length}</b> active accounts, based on {folder && share.folders.some((x) => x.inheritanceBroken && (folder === x.path || folder.startsWith(x.path + '/'))) ? 'this folder’s own permissions' : 'permissions inherited from the share'}.
          </p>
          <div className="flex flex-wrap gap-1.5">
            {access.slice(0, 14).map((a) => (
              <button key={a.userSid} onClick={() => go('access', a.userSid)} className="rounded-full hover:ring-2 ring-[var(--line)]" title={nameOf(a.userSid)}>
                <Person sid={a.userSid} size={22} sub="" />
              </button>
            ))}
            {access.length > 14 && <span className="text-[12.5px] text-ink-3 self-center">+{access.length - 14} more</span>}
          </div>
        </div>

        <dl className="grid grid-cols-2 gap-x-6 gap-y-2 text-[13px]">
          <div><dt className="text-ink-3">Size</dt><dd>{fmtBytes(f.size)}</dd></div>
          <div><dt className="text-ink-3">File owner (NTFS)</dt><dd>{nameOf(f.ownerSid)}</dd></div>
          <div><dt className="text-ink-3">Modified</dt><dd>{fmtDateTime(f.modified)}</dd></div>
          <div><dt className="text-ink-3">Created</dt><dd>{fmtDateTime(f.created)}</dd></div>
          <div><dt className="text-ink-3">Last scanned</dt><dd>{ago(share.scan.lastScan)}</dd></div>
          <div><dt className="text-ink-3">Policy version</dt><dd className="font-mono text-[12px]">{POLICY_VERSION}</dd></div>
        </dl>
        <div className="text-[12px] text-ink-3 flex gap-1.5"><FileWarning size={14} className="shrink-0 mt-0.5" /> Matches are possible sensitive data, not confirmed. Use “Change classification” to record a false positive.</div>
      </div>
    </Drawer>
  );
}
