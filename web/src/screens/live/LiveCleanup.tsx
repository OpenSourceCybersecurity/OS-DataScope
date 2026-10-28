import { useEffect, useState } from 'react';
import { ArchiveX, Copy, FolderClock, RefreshCw } from 'lucide-react';
import { api, type CleanupScope, type CleanupSummary, type DuplicateGroup, type Sensitivity, type StaleFile } from '../../api/client';
import { useNav } from '../../App';
import { Button, Callout, Card, ClassPill, Empty, Meter, PageHeader, Segmented, Tag, Td, Th, Unc, ago, cx, filterCls, fmtBytes, fmtDate, fmtNum } from '../../ui/kit';
import { PLACE, activityNote, filesText, parseCleanupParam, percent, sensitiveText, yearChoices, yearsLabel, type CleanupSource } from './cleanup';
import { useData } from './useData';

const liveSource: CleanupSource = {
  summary: (scope, years) => api.cleanup(scope, years),
  staleFiles: (scope, years, sort, offset) => api.staleFiles(scope, years, sort, offset),
  duplicates: (scope, offset) => api.duplicates(scope, offset),
};

export default function LiveCleanup() {
  return <CleanupView source={liveSource} />;
}

const isSensitive = (l: Sensitivity) => l === 'Restricted' || l === 'Confidential';

/**
 * Stale and duplicate data (D119): files nobody has changed in years and copies of one file in several places, so IT
 * and owners can archive or delete them under their retention policy. Data Scope only reports; it never moves or
 * deletes a file.
 */
export function CleanupView({ source, demo }: { source: CleanupSource; demo?: boolean }) {
  const { go, param } = useNav();
  const [years, setYears] = useState<number | undefined>();
  const [start] = useState(() => parseCleanupParam(param));
  const [shareId, setShareId] = useState<string>(start.shareId);
  const [sensitiveOnly, setSensitiveOnly] = useState(start.sensitive);
  const [tab, setTab] = useState<'stale' | 'duplicates'>('stale');
  const showSensitive = () => {
    setTab('stale');
    setSensitiveOnly(true);
    // After the list re-renders with the filter, bring it into view.
    setTimeout(() => document.getElementById('stale-files')?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 50);
  };
  const scope = { shareId: shareId || undefined };
  // Until someone picks a threshold, the server uses its configured one.
  const summary = useData(() => source.summary(scope, years), [shareId, years]);
  const s = summary.data;
  const y = years ?? s?.years ?? 3;

  const t = s?.totals;
  const sensitive = t ? t.staleRestricted + t.staleConfidential : 0;
  const place = PLACE;
  return (
    <>
      <PageHeader
        title="Stale & duplicate data"
        sub="Files nobody has changed in years, and copies of the same file in more than one place. Archiving or deleting them under your retention policy cuts storage and the data that could leak. Data Scope only reports; it never moves or deletes files."
        actions={<Button size="sm" variant="ghost" onClick={() => void summary.reload()}><RefreshCw size={14} /> Check again</Button>}
      />
      {summary.error && <div className="mb-4"><Callout tone="bad" title="Can't reach the Data Scope server">{summary.error}</Callout></div>}

      <div className="flex flex-wrap items-center gap-3 mb-4">
        <label className="flex items-center gap-2 text-[13px] text-ink-2">
          Not changed in
          <select className={filterCls} value={y} onChange={(e) => setYears(Number(e.target.value))}>
            {yearChoices(s?.defaultYears ?? y).map((n) => (
              <option key={n} value={n}>{yearsLabel(n)}{s && n === s.defaultYears ? ' (default)' : ''}</option>
            ))}
          </select>
        </label>
        {(s && s.shares.length > 1) || shareId ? (
          <select className={filterCls} value={shareId} onChange={(e) => setShareId(e.target.value)} aria-label={place.one}>
            <option value="">{place.all}</option>
            {[...(s?.shares ?? [])].sort((a, b) => a.uncPath.localeCompare(b.uncPath)).map((x) => (
              <option key={x.shareId} value={x.shareId}>{x.uncPath}</option>
            ))}
          </select>
        ) : null}
      </div>

      {s && t && (
        <>
          <div className="grid gap-3 mb-3 sm:grid-cols-3">
            <Figure icon={<FolderClock size={16} />} label="Stale data" value={fmtBytes(t.staleBytes)}
              note={`${filesText(t.staleFiles)}, ${percent(t.staleBytes, t.bytes)} of the space scanned`} />
            <Figure icon={<ArchiveX size={16} />} label="Sensitive and stale" value={fmtNum(sensitive)} tone={sensitive > 0 ? 'bad' : undefined}
              note={sensitive > 0 ? `${sensitiveText(t.staleRestricted, t.staleConfidential)} ${sensitive === 1 ? 'file' : 'files'} nobody needs day to day` : 'No sensitive file is stale'}
              onClick={sensitive > 0 ? showSensitive : undefined} action="Show these" />
            <Figure icon={<Copy size={16} />} label="Space taken by copies" value={fmtBytes(t.duplicateBytes)}
              note={`${fmtNum(t.duplicateCopies)} extra ${t.duplicateCopies === 1 ? 'copy' : 'copies'} of ${fmtNum(t.duplicateGroups)} ${t.duplicateGroups === 1 ? 'file' : 'files'}`} />
          </div>
          <p className="text-[12.5px] text-ink-3 mb-4 max-w-3xl">
            {`Stale means not changed or created since ${fmtDate(s.cutoff)}.`} {activityNote(s)}
            {t.inUse > 0 && ` ${fmtNum(t.inUse)} older ${t.inUse === 1 ? 'file was' : 'files were'} opened recently and ${t.inUse === 1 ? 'is' : 'are'} not counted.`}
            {t.noDate > 0 && ` ${filesText(t.noDate)} ${t.noDate === 1 ? 'has' : 'have'} no date and can't be judged.`}
            {s.lastScanAt ? ` From the scan finished ${ago(s.lastScanAt)}.` : ` No ${place.one.toLowerCase()} in scope has been scanned yet.`}
          </p>
          {sensitive > 0 && !demo && (
            <div className="mb-4">
              <Callout tone="warn" title="Old sensitive files are on the Risks list">
                Each share with stale sensitive files shows on <button type="button" className="text-accent font-medium hover:underline" onClick={() => go('risks')}>Risks</button> as
                "Sensitive files nobody has changed in years", where you can send it to IT or accept it with a reason.
              </Callout>
            </div>
          )}
          <div className="mb-3">
            <Segmented
              value={tab}
              onChange={setTab}
              options={[
                { id: 'stale', label: `Stale ${fmtNum(t.staleFiles)}` },
                { id: 'duplicates', label: `Duplicates ${fmtNum(t.duplicateGroups)}` },
              ]}
            />
          </div>
          {tab === 'stale'
            ? <StaleTab source={source} scope={scope} years={y} summary={s} onShare={setShareId} sensitiveOnly={sensitiveOnly} onSensitiveOnly={setSensitiveOnly} />
            : <DuplicatesTab source={source} scope={scope} minBytes={s.minDuplicateBytes} />}
        </>
      )}
    </>
  );
}

function Figure({ icon, label, value, note, tone, onClick, action }: {
  icon: React.ReactNode;
  label: string;
  value: string;
  note: string;
  tone?: 'bad';
  /** Makes the whole card a button that opens what it counts. */
  onClick?: () => void;
  action?: string;
}) {
  const body = (
    <Card className={cx('h-full', onClick && 'transition-colors group-hover:border-accent')}>
      <div className="flex items-center gap-2 text-[12.5px] text-ink-2">{icon}{label}</div>
      <div className="text-[26px] font-semibold tnum mt-1 leading-tight" style={tone === 'bad' ? { color: 'var(--s-critical)' } : undefined}>{value}</div>
      <div className="text-[12.5px] text-ink-3 mt-1">{note}</div>
      {onClick && action && <div className="text-[12.5px] font-medium text-accent mt-1.5 group-hover:underline">{action}</div>}
    </Card>
  );
  return onClick ? (
    <button type="button" className="group text-left rounded-xl focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--accent)]" onClick={onClick}>
      {body}
    </button>
  ) : body;
}

function StaleTab({ source, scope: at, years, summary, onShare, sensitiveOnly, onSensitiveOnly }: {
  source: CleanupSource;
  scope: CleanupScope;
  years: number;
  summary: CleanupSummary;
  onShare: (id: string) => void;
  sensitiveOnly: boolean;
  onSensitiveOnly: (on: boolean) => void;
}) {
  const [folder, setFolder] = useState<{ id: string; path: string }>();
  const [sort, setSort] = useState<'size' | 'oldest'>('size');
  const [extra, setExtra] = useState<StaleFile[]>([]);
  const shareId = at.shareId ?? '';
  const scope = { ...at, folderId: folder?.id, sensitive: sensitiveOnly || undefined };
  const files = useData(() => (setExtra([]), source.staleFiles(scope, years, sort, 0)), [shareId, years, sort, folder?.id, sensitiveOnly]);
  useEffect(() => setFolder(undefined), [shareId, years]);
  const place = PLACE;
  const shown = [...(files.data?.items ?? []), ...extra];
  const more = async () => {
    const page = await source.staleFiles(scope, years, sort, shown.length);
    setExtra((x) => [...x, ...page.items]);
  };

  if (summary.totals.staleFiles === 0)
    return (
      <Card>
        <Empty icon={<FolderClock size={20} />} title={`Nothing unchanged for ${yearsLabel(years)}`}>
          {summary.totals.files === 0
            ? 'Scan a file server first; stale data is worked out from what the scan finds.'
            : 'Try a shorter time, or check again after the next scan.'}
        </Empty>
      </Card>
    );

  return (
    <div className="flex flex-col gap-4">
      {!shareId && summary.shares.length > 1 && (
        <Card title={place.by} pad={false}>
          <div className="overflow-x-auto">
            <table className="w-full text-[13.5px] min-w-[620px]">
              <thead><tr><Th>{place.one}</Th><Th className="text-right">Stale files</Th><Th className="text-right">Stale size</Th><Th>Share of its space</Th><Th>Sensitive</Th></tr></thead>
              <tbody>
                {summary.shares.filter((x) => x.staleFiles > 0).map((x) => (
                  <tr key={x.shareId} className="border-t border-line cursor-pointer hover:bg-surface-2" onClick={() => onShare(x.shareId)}>
                    <Td><Unc>{x.uncPath}</Unc></Td>
                    <Td className="text-right tnum">{fmtNum(x.staleFiles)}</Td>
                    <Td className="text-right tnum whitespace-nowrap">{fmtBytes(x.staleBytes)}</Td>
                    <Td className="min-w-[140px]">
                      <Meter value={x.bytes ? x.staleBytes / x.bytes : 0} tone="warn" label={`Stale share of ${x.uncPath}`} />
                      <div className="text-[12px] text-ink-3 tnum mt-1">{percent(x.staleBytes, x.bytes)}</div>
                    </Td>
                    <Td>{x.staleRestricted + x.staleConfidential > 0 ? <Tag tone="bad">{sensitiveText(x.staleRestricted, x.staleConfidential)}</Tag> : <span className="text-ink-3">None</span>}</Td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      )}

      {summary.folders.length > 0 && (
        <Card title="Folders with the most stale data">
          <ul className="divide-y divide-line -my-2">
            {summary.folders.slice(0, 10).map((f) => (
              <li key={f.folderId}>
                <button
                  type="button"
                  onClick={() => setFolder(folder?.id === f.folderId ? undefined : { id: f.folderId, path: f.uncPath })}
                  className={cx('w-full text-left py-2 flex flex-wrap items-baseline gap-x-3 gap-y-0.5 hover:bg-surface-2 rounded', folder?.id === f.folderId && 'bg-surface-2')}
                  aria-pressed={folder?.id === f.folderId}
                >
                  <span className="text-[12.5px] break-all min-w-0 flex-1 font-mono">{f.uncPath}</span>
                  <span className="text-[12.5px] text-ink-2 tnum whitespace-nowrap">{filesText(f.staleFiles)} · {fmtBytes(f.staleBytes)}</span>
                  {f.restricted + f.confidential > 0 && <Tag tone="bad">{sensitiveText(f.restricted, f.confidential)}</Tag>}
                </button>
              </li>
            ))}
          </ul>
        </Card>
      )}

      <Card
        id="stale-files"
        title={`${sensitiveOnly ? 'Stale sensitive files' : 'Stale files'}${folder ? ` in ${folder.path}` : ''}`}
        action={
          <div className="flex flex-wrap items-center justify-end gap-2">
            {folder && <Button size="sm" variant="ghost" onClick={() => setFolder(undefined)}>All folders</Button>}
            <Segmented value={sensitiveOnly ? 'sensitive' : 'all'} onChange={(v) => onSensitiveOnly(v === 'sensitive')} options={[{ id: 'all', label: 'All' }, { id: 'sensitive', label: 'Sensitive' }]} />
            <Segmented value={sort} onChange={setSort} options={[{ id: 'size', label: 'Largest' }, { id: 'oldest', label: 'Oldest' }]} />
          </div>
        }
        pad={false}
      >
        {files.error && <div className="p-4"><Callout tone="bad">{files.error}</Callout></div>}
        <div className="overflow-x-auto">
          <table className="w-full text-[13.5px] min-w-[640px]">
            <thead><tr><Th>File</Th><Th className="text-right">Size</Th><Th>Last changed</Th><Th>Label</Th></tr></thead>
            <tbody>
              {shown.map((f) => (
                <tr key={f.fileId} className="border-t border-line align-top">
                  <Td>
                    <div className="font-medium break-all">{f.name}</div>
                    <div className="text-[12px] text-ink-3 break-all font-mono">{f.path}</div>
                  </Td>
                  <Td className="text-right tnum whitespace-nowrap">{fmtBytes(f.size)}</Td>
                  <Td className="whitespace-nowrap">{f.lastChanged ? fmtDate(f.lastChanged) : 'Unknown'}</Td>
                  <Td>{isSensitive(f.label) ? <ClassPill c={f.label} /> : <span className="text-ink-2">{f.label}</span>}</Td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {files.data && shown.length < files.data.total && (
          <div className="p-3 border-t border-line flex items-center gap-3">
            <Button size="sm" onClick={() => void more()}>Show more</Button>
            <span className="text-[12.5px] text-ink-3">{fmtNum(shown.length)} of {fmtNum(files.data.total)}</span>
          </div>
        )}
      </Card>
    </div>
  );
}

function DuplicatesTab({ source, scope, minBytes }: { source: CleanupSource; scope: CleanupScope; minBytes: number }) {
  const [extra, setExtra] = useState<DuplicateGroup[]>([]);
  const list = useData(() => (setExtra([]), source.duplicates(scope, 0)), [scope.shareId]);
  const shown = [...(list.data?.items ?? []), ...extra];
  const more = async () => {
    const page = await source.duplicates(scope, shown.length);
    setExtra((x) => [...x, ...page.items]);
  };

  if (list.error) return <Callout tone="bad">{list.error}</Callout>;
  if (list.data && list.data.total === 0)
    return (
      <Card>
        <Empty icon={<Copy size={20} />} title="No copies found">
          No two files here share a name and size. Files under {fmtBytes(minBytes)} and hidden or system files are not counted.
        </Empty>
      </Card>
    );

  return (
    <div className="flex flex-col gap-3">
      <p className="text-[12.5px] text-ink-3 max-w-3xl">
        Copies are files with the same name and size. Their contents are not compared, so check before deleting, and renamed copies are not found.
        Files under {fmtBytes(minBytes)} and hidden or system files are left out.
      </p>
      <ul className="bg-surface border border-line rounded-xl divide-y divide-[var(--line)] overflow-hidden">
        {shown.map((g) => (
          <li key={`${g.name}|${g.size}`} className="px-4 py-3">
            <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1">
              <span className="font-medium break-all">{g.name}</span>
              <span className="text-[12.5px] text-ink-2 tnum">{fmtNum(g.copies)} copies of {fmtBytes(g.size)} · {fmtBytes(g.wasted)} extra</span>
              {g.sameModified && <Tag title="Every copy also has the same last-modified time">Same modified time</Tag>}
              {g.shares > 1 && <Tag tone="accent">{fmtNum(g.shares)} shares</Tag>}
              {isSensitive(g.label) && <ClassPill c={g.label} />}
            </div>
            <ul className="mt-1.5 flex flex-col gap-0.5">
              {g.locations.map((l) => (
                <li key={l.fileId} className="text-[12px] text-ink-2 break-all font-mono">{l.path}</li>
              ))}
              {g.copies > g.locations.length && <li className="text-[12px] text-ink-3">and {fmtNum(g.copies - g.locations.length)} more</li>}
            </ul>
          </li>
        ))}
      </ul>
      {list.data && shown.length < list.data.total && (
        <div className="flex items-center gap-3">
          <Button size="sm" onClick={() => void more()}>Show more</Button>
          <span className="text-[12.5px] text-ink-3">{fmtNum(shown.length)} of {fmtNum(list.data.total)}</span>
        </div>
      )}
    </div>
  );
}
