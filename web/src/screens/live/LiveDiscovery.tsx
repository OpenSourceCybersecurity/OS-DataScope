import { useState } from 'react';
import { EyeOff, FileWarning, History, Loader2, Lock } from 'lucide-react';
import {
  api,
  type ClassifiedFile,
  type FileClassification,
  type Page,
  type Sensitivity,
} from '../../api/client';
import { CONTENT_STATE_LABEL, canReviewClassification, useBackend } from '../../api/backend';
import type { FindingCategory } from '../../domain/types';
import { useNav } from '../../App';
import { filtersFor, topConfidence, uncOf } from './classification';
import {
  Button,
  Callout,
  ClassPill,
  ConfidencePill,
  Drawer,
  Empty,
  Field,
  PageHeader,
  Segmented,
  Tag,
  Td,
  Th,
  Unc,
  ago,
  fmtBytes,
  fmtDateTime,
  fmtNum,
  filterCls,
  inputCls,
} from '../../ui/kit';
import { useData } from './useData';
import { discoveryParam } from './links';
import { standardNames } from './compliance';
import { WhoCanOpen } from './LiveAccess';

const CATEGORIES: FindingCategory[] = ['US SSN', 'US ITIN', "Driver's licence", 'Passport', 'Payment card', 'Bank account', 'Credentials', 'Health information', 'Date of birth', 'Personal details', 'Custom rule'];
const LABELS: Sensitivity[] = ['Public', 'Internal', 'Confidential', 'Restricted'];
const READ_STATES = new Set(['Scanned', 'Partial']);
const PAGE = 100;

export default function LiveDiscovery() {
  // Other screens link here with "share:<id>" or "unread[:<id>]" (the Exposure map, for one).
  const { param } = useNav();
  const linked = discoveryParam(param);
  const [view, setView] = useState<'sensitive' | 'unread'>(linked.view);
  const [category, setCategory] = useState<FindingCategory>();
  const [shareId, setShareId] = useState(linked.shareId);
  const [label, setLabel] = useState<Sensitivity | ''>('');
  const [minConfidence, setMinConfidence] = useState<'' | 'Medium' | 'High'>('');
  const [standard, setStandard] = useState(linked.standard);
  // The Custom detection page links here with "rule:<id>" for one rule's files (D133).
  const [rule, setRule] = useState(linked.rule);
  // The search box links here with "file:<id>" to open one file.
  const [open, setOpen] = useState<string | undefined>(linked.fileId);
  const [more, setMore] = useState<ClassifiedFile[]>([]);
  const [loadingMore, setLoadingMore] = useState(false);

  const filters = filtersFor({ view, shareId, category, label, minConfidence, standard, rule });
  const key = JSON.stringify(filters);
  const summary = useData(() => api.classificationSummary(), [], 10000);
  // Compliance standards (D126): only turned-on ones can narrow the list, and each row says which it falls under.
  const compliance = useData(() => api.complianceStandards(), []);
  const turnedOn = (compliance.data?.standards ?? []).filter((x) => x.enabled);
  const shares = useData(() => api.shares(), []);
  const files = useData<Page<ClassifiedFile>>(() => {
    setMore([]);
    return api.classifiedFiles(filters, 0, PAGE);
  }, [key]);

  const s = summary.data;
  const unread = s ? Object.entries(s.byContentState).filter(([k]) => !READ_STATES.has(k)).reduce((a, [, n]) => a + n, 0) : 0;
  const read = s ? (s.byContentState.Scanned ?? 0) + (s.byContentState.Partial ?? 0) : 0;
  const rows = [...(files.data?.items ?? []), ...more];
  const total = files.data?.total ?? 0;

  const loadMore = async () => {
    setLoadingMore(true);
    try {
      const page = await api.classifiedFiles(filters, rows.length, PAGE);
      setMore((m) => [...m, ...page.items]);
    } finally {
      setLoadingMore(false);
    }
  };

  const pick = (next: Partial<{ view: typeof view; category: FindingCategory | undefined; label: Sensitivity | '' }>) => {
    if (next.view !== undefined) setView(next.view);
    if ('category' in next) setCategory(next.category);
    if (next.label !== undefined) setLabel(next.label);
  };

  return (
    <>
      <PageHeader
        title="Sensitive data"
        sub={<>Possible matches found by reading file contents, with confidence and masked evidence. Data Scope never stores file contents or unmasked values.</>}
        actions={s && <Tag tone="neutral">Policy {s.policyVersion}</Tag>}
      />
      {(summary.error || files.error) && (
        <div className="mb-4"><Callout tone="bad" title="Can't reach the Data Scope server">{summary.error ?? files.error}</Callout></div>
      )}

      {s && read === 0 && (
        <div className="mb-4">
          <Callout tone="info" title="No file contents read yet">
            Run a scan under <b>Servers &amp; scans</b>. After it lists the folders, it reads supported files (text, CSV, Word, Excel, PowerPoint and PDF) and the results appear here.
          </Callout>
        </div>
      )}

      {s && read > 0 && (
        <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mb-4">
          <Tile label="Restricted" value={s.bySensitivity.Restricted ?? 0} color="var(--c-restricted)" onClick={() => pick({ view: 'sensitive', label: 'Restricted', category: undefined })} />
          <Tile label="Confidential" value={s.bySensitivity.Confidential ?? 0} color="var(--c-confidential)" onClick={() => pick({ view: 'sensitive', label: 'Confidential', category: undefined })} />
          <Tile label="Files read" value={read} sub={`of ${fmtNum(s.files)} files`} />
          <Tile label="Not read" value={unread} sub="Unknown, not clean" warn={unread > 0} onClick={() => pick({ view: 'unread' })} />
        </div>
      )}

      {view === 'sensitive' && (
        <div className="flex flex-wrap gap-2 mb-4" role="group" aria-label="Type of data">
          <Chip active={!category} onClick={() => setCategory(undefined)} label="All types" />
          {CATEGORIES.filter((c) => c !== 'Custom rule' || category === c || (s?.byCategory.find((x) => x.category === c)?.files ?? 0) > 0).map((c) => (
            <Chip key={c} active={category === c} onClick={() => setCategory(category === c ? undefined : c)} label={c} n={s?.byCategory.find((x) => x.category === c)?.files ?? 0} />
          ))}
          {rule && (
            <Chip active onClick={() => setRule('')} label={`Only rule ${rule}  ✕`} />
          )}
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2 mb-3">
        <Segmented
          value={view}
          onChange={(v) => pick({ view: v, category: undefined, label: '' })}
          options={[
            { id: 'sensitive', label: 'Sensitive files' },
            { id: 'unread', label: `Not read (${fmtNum(unread)})` },
          ]}
        />
        <select aria-label="Share" className={filterCls} value={shareId} onChange={(e) => setShareId(e.target.value)}>
          <option value="">All shares</option>
          {(shares.data?.items ?? []).filter((x) => x.kind === 'Disk').map((x) => (
            <option key={x.id} value={x.id}>{x.hostname} · {x.name}</option>
          ))}
        </select>
        {view === 'sensitive' && (
          <>
            <select aria-label="Label" className={filterCls} value={label} onChange={(e) => setLabel(e.target.value as Sensitivity | '')}>
              <option value="">Confidential or above</option>
              {[...LABELS].reverse().map((c) => <option key={c} value={c}>{c} only</option>)}
            </select>
            <select aria-label="Confidence" className={filterCls} value={minConfidence} onChange={(e) => setMinConfidence(e.target.value as '' | 'Medium' | 'High')}>
              <option value="">Any confidence</option>
              <option value="Medium">Medium or high</option>
              <option value="High">High only</option>
            </select>
            {(turnedOn.length > 0 || standard) && (
              <select aria-label="Compliance standard" className={filterCls} value={standard} onChange={(e) => setStandard(e.target.value)}>
                <option value="">Any standard</option>
                {turnedOn.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
              </select>
            )}
          </>
        )}
        <span className="text-[12.5px] text-ink-3 ml-auto tnum">{fmtNum(total)} {total === 1 ? 'file' : 'files'}</span>
      </div>

      {view === 'unread' && (
        <div className="mb-3">
          <Callout tone="warn" title="Not read is not clean">
            These files may still hold sensitive data. Each row says why it wasn't read. Access-denied, offline and timed-out files are tried again on the next scan.
          </Callout>
        </div>
      )}

      <div className="bg-surface border border-line rounded-xl overflow-x-auto">
        <table className="w-full text-[13.5px] min-w-[820px]">
          <thead>
            <tr>
              <Th>File</Th>
              <Th>Label</Th>
              <Th>{view === 'sensitive' ? 'Possible matches' : 'Why not read'}</Th>
              {view === 'sensitive' && <Th>Confidence</Th>}
              <Th>Modified</Th>
            </tr>
          </thead>
          <tbody>
            {rows.map((f) => {
              const top = topConfidence(f);
              return (
                <tr key={f.id} className="border-t border-line cursor-pointer hover:bg-surface-2" onClick={() => setOpen(f.id)}>
                  <Td className="max-w-[440px]">
                    <div className="font-medium break-all">{f.name}</div>
                    <div className="text-[12px] text-ink-3 font-mono break-all">\\{f.hostname}\{f.shareName}\{f.path}</div>
                  </Td>
                  <Td><ClassPill c={f.sensitivity} overridden={!!f.overrideSensitivity} /></Td>
                  <Td>
                    {view === 'sensitive' ? (
                      <div className="flex flex-wrap gap-1">
                        {f.categories.map((c) => (
                          <Tag key={c.category}>{c.category} <span className="text-ink-3 tnum">×{fmtNum(c.matches)}</span></Tag>
                        ))}
                        {!f.categories.length && <span className="text-ink-3 text-[12.5px]">Set by a reviewer</span>}
                        {standardNames(f.standards ?? [], turnedOn).map((n) => <Tag key={n} tone="accent">{n}</Tag>)}
                      </div>
                    ) : (
                      <div>
                        <div className="text-ink-2">{CONTENT_STATE_LABEL[f.contentState]}</div>
                        {f.contentNote && <div className="text-[12px] text-ink-3">{f.contentNote}</div>}
                      </div>
                    )}
                  </Td>
                  {view === 'sensitive' && <Td>{top ? <ConfidencePill c={top} /> : <span className="text-ink-3">—</span>}</Td>}
                  <Td className="whitespace-nowrap text-ink-2">{f.modifiedAt ? ago(f.modifiedAt) : '—'}</Td>
                </tr>
              );
            })}
          </tbody>
        </table>
        {files.data && !rows.length && (
          <Empty icon={<EyeOff size={18} />} title={view === 'sensitive' ? 'No sensitive files match these filters' : 'Every file found has been read'}>
            {view === 'sensitive' && read > 0 && 'Nothing was flagged here. Files that could not be read are under “Not read”.'}
          </Empty>
        )}
        {rows.length < total && (
          <div className="px-4 py-2.5 border-t border-line flex items-center gap-3">
            <Button size="sm" onClick={loadMore} disabled={loadingMore}>
              {loadingMore && <Loader2 size={14} className="animate-spin" />} Show more
            </Button>
            <span className="text-[12.5px] text-ink-3 tnum">Showing {fmtNum(rows.length)} of {fmtNum(total)}</span>
          </div>
        )}
      </div>

      {open && (
        <FileDrawer
          fileId={open}
          onClose={() => setOpen(undefined)}
          onChanged={() => {
            void files.reload();
            void summary.reload();
          }}
        />
      )}
    </>
  );
}

function Tile({ label, value, sub, color, warn, onClick }: { label: string; value: number; sub?: string; color?: string; warn?: boolean; onClick?: () => void }) {
  const body = (
    <>
      <div className="text-[12px] text-ink-3 flex items-center gap-1.5">
        {color && <span className="size-2 rounded-full" style={{ background: color }} />}
        {label}
      </div>
      <div className="font-display text-[22px] font-[650] tnum mt-0.5" style={warn ? { color: 'var(--s-medium)' } : undefined}>{fmtNum(value)}</div>
      {sub && <div className="text-[12px] text-ink-3">{sub}</div>}
    </>
  );
  return onClick ? (
    <button className="bg-surface border border-line rounded-xl px-4 py-3 text-left hover:border-ink-3" onClick={onClick}>{body}</button>
  ) : (
    <div className="bg-surface border border-line rounded-xl px-4 py-3">{body}</div>
  );
}

function Chip({ label, n, active, onClick }: { label: string; n?: number; active: boolean; onClick: () => void }) {
  return (
    <button
      onClick={onClick}
      aria-pressed={active}
      className={`h-8 px-3 rounded-full border text-[13px] inline-flex items-center gap-2 ${active ? 'border-accent bg-accent-soft text-ink font-medium' : 'border-line bg-surface text-ink-2 hover:border-ink-3'}`}
    >
      {label}
      {n !== undefined && <span className="tnum text-ink-3">{fmtNum(n)}</span>}
    </button>
  );
}

function FileDrawer({ fileId, onClose, onChanged }: { fileId: string; onClose: () => void; onChanged: () => void }) {
  const backend = useBackend();
  const reviewer = canReviewClassification(backend);
  const detail = useData(() => api.fileClassification(fileId), [fileId]);
  const standards = useData(() => api.complianceStandards(), []);
  const [data, setData] = useState<FileClassification>();
  const d = data ?? detail.data;
  const f = d?.file;

  return (
    <Drawer open onClose={onClose} title={f?.name ?? 'Loading…'} sub={f && <Unc>{uncOf(f)}</Unc>} width={640}>
      {detail.error && <Callout tone="bad">{detail.error}</Callout>}
      {d && f && (
        <div className="flex flex-col gap-5">
          <div className="flex flex-wrap items-center gap-3">
            <ClassPill c={f.sensitivity} overridden={!!f.overrideSensitivity} />
            {f.overrideSensitivity && <span className="text-[12.5px] text-ink-3">The policy says {f.policySensitivity}.</span>}
            {(f.standards ?? []).length > 0 && (
              <span className="flex flex-wrap items-center gap-1.5 text-[12.5px] text-ink-3">
                Falls under {standardNames(f.standards, standards.data?.standards).map((n) => <Tag key={n} tone="accent">{n}</Tag>)}
              </span>
            )}
          </div>

          {d.changedSinceReview && (
            <Callout tone="warn" title="Changed since it was reviewed">
              The file was read again after the last review and its findings may differ. Check the matches below and confirm or remove the reviewer's label.
            </Callout>
          )}

          {!READ_STATES.has(f.contentState) && (
            <Callout tone="warn" title={CONTENT_STATE_LABEL[f.contentState]}>
              {f.contentNote ?? 'Data Scope could not read this file.'} Its label is Unknown until it can be read. It counts as a coverage gap, not as clean.
            </Callout>
          )}
          {f.contentState === 'Partial' && (
            <Callout tone="info" title="Partly read">{f.contentNote} Matches below come from the part that was read.</Callout>
          )}

          {reviewer && <OverrideForm file={f} onSaved={(next) => { setData(next); onChanged(); }} />}

          {d.findings.length > 0 && (
            <div>
              <div className="eyebrow mb-2">Possible matches</div>
              {d.evidenceHidden && (
                <div className="mb-2 text-[12.5px] text-ink-2 flex gap-1.5"><Lock size={14} className="shrink-0 mt-0.5" /> Your role shows what was found and how often, not the masked snippets.</div>
              )}
              <ul className="flex flex-col gap-3">
                {d.findings.map((x) => (
                  <li key={x.ruleId} className="border border-line rounded-lg p-3">
                    <div className="flex flex-wrap items-center justify-between gap-2 mb-1.5">
                      <span className="font-medium">{x.ruleName} <span className="text-ink-3 font-normal tnum">× {fmtNum(x.matches)}</span></span>
                      <ConfidencePill c={x.confidence} />
                    </div>
                    <div className="text-[12px] text-ink-3 mb-2"><span className="font-mono">{x.ruleId}</span> · {x.category} · policy {x.policyVersion}</div>
                    {x.evidence && (
                      <ul className="flex flex-col gap-1">
                        {x.evidence.map((ev, i) => (
                          <li key={i} className="font-mono text-[12px] bg-surface-2 rounded px-2 py-1 break-all">{ev}</li>
                        ))}
                      </ul>
                    )}
                  </li>
                ))}
              </ul>
              <div className="text-[12px] text-ink-3 mt-2 flex gap-1.5">
                <FileWarning size={14} className="shrink-0 mt-0.5" /> These are possible matches, not confirmed sensitive data. Values were masked when the file was read; the full values are never stored.
              </div>
            </div>
          )}
          {READ_STATES.has(f.contentState) && !d.findings.length && (
            <p className="text-[13px] text-ink-2">No possible matches in the text that was read.</p>
          )}

          {d.overrides.length > 0 && (
            <div>
              <div className="eyebrow mb-2 flex items-center gap-1.5"><History size={13} /> Review history</div>
              <ul className="flex flex-col gap-2 text-[13px]">
                {d.overrides.map((o, i) => (
                  <li key={i} className="border-l-2 border-line pl-3">
                    <div>
                      <b>{o.reviewerName}</b> {o.sensitivity ? <>set it to <b>{o.sensitivity}</b></> : <>removed the reviewer's label</>}{' '}
                      <span className="text-ink-3">{ago(o.at)}</span>
                    </div>
                    <div className="text-ink-2">“{o.reason}”</div>
                  </li>
                ))}
              </ul>
            </div>
          )}

          <WhoCanOpen fileId={f.id} />

          <dl className="grid grid-cols-2 gap-x-6 gap-y-2 text-[13px]">
            <div><dt className="text-ink-3">Size</dt><dd>{fmtBytes(f.size)}</dd></div>
            <div><dt className="text-ink-3">Modified</dt><dd>{f.modifiedAt ? fmtDateTime(f.modifiedAt) : '—'}</dd></div>
            <div><dt className="text-ink-3">Contents read</dt><dd>{f.classifiedAt ? fmtDateTime(f.classifiedAt) : 'Never'}</dd></div>
            <div><dt className="text-ink-3">Policy version</dt><dd className="font-mono text-[12px]">{f.classifiedPolicy ?? '—'}</dd></div>
          </dl>
        </div>
      )}
    </Drawer>
  );
}

function OverrideForm({ file, onSaved }: { file: ClassifiedFile; onSaved: (d: FileClassification) => void }) {
  const [editing, setEditing] = useState(false);
  const [choice, setChoice] = useState<Sensitivity>(file.sensitivity === 'Unknown' ? 'Internal' : file.sensitivity);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string>();

  const save = async (sensitivity: Sensitivity | null) => {
    setBusy(true);
    setProblem(undefined);
    try {
      onSaved(await api.overrideClassification(file.id, sensitivity, reason.trim()));
      setEditing(false);
      setReason('');
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  if (!editing) {
    return (
      <div>
        <Button size="sm" variant="ghost" onClick={() => setEditing(true)}>Change label</Button>
      </div>
    );
  }
  return (
    <form
      className="bg-surface-2 rounded-lg p-3 flex flex-col gap-3"
      onSubmit={(e) => {
        e.preventDefault();
        void save(choice);
      }}
    >
      <div className="flex flex-wrap gap-1.5" role="radiogroup" aria-label="New label">
        {LABELS.map((c) => (
          <button type="button" role="radio" aria-checked={choice === c} key={c} onClick={() => setChoice(c)} className={`rounded-full ${choice === c ? 'ring-2 ring-[var(--accent)]' : ''}`}>
            <ClassPill c={c} />
          </button>
        ))}
      </div>
      <Field label="Reason (required, kept in the audit log)" htmlFor="override-reason">
        <input id="override-reason" className={inputCls} value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} placeholder="e.g. Part numbers, not SSNs" />
      </Field>
      {problem && <Callout tone="bad">{problem}</Callout>}
      <div className="flex flex-wrap gap-2">
        <Button type="submit" variant="primary" size="sm" disabled={busy || reason.trim().length < 3}>Save label</Button>
        <Button size="sm" variant="ghost" onClick={() => setEditing(false)} disabled={busy}>Cancel</Button>
        {file.overrideSensitivity && (
          <Button size="sm" variant="ghost" disabled={busy || reason.trim().length < 3} onClick={() => void save(null)}>Go back to the policy label</Button>
        )}
      </div>
    </form>
  );
}
