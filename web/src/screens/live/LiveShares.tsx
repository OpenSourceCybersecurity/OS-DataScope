import { useEffect, useState } from 'react';
import { AlertTriangle, ArrowRight, ChevronRight, File, Folder as FolderIcon, FolderX, HardDrive } from 'lucide-react';
import { api, type Folder, type Share } from '../../api/client';
import { GAP_LABEL, canManage, canManageReviews, useBackend } from '../../api/backend';
import { Button, Callout, Card, Drawer, PageHeader, Segmented, Tabs, Tag, Td, Th, Unc, ago, fmtBytes, fmtDateTime, fmtNum } from '../../ui/kit';
import { PeopleList, PermissionTable, Summary, useOpenFolder } from './LiveAccess';
import { OwnerPanel } from './OwnerPanel';
import { useNav } from '../../App';
import { useData } from './useData';

export default function LiveShares() {
  const backend = useBackend();
  const admin = canManage(backend);
  const shares = useData(() => api.shares(), [], 5000);
  const coverage = useData(() => api.coverage('files'), [], 5000);
  const [open, setOpen] = useState<Share>();
  const [problem, setProblem] = useState<string>();
  // Other screens link here with a share's id (Risks, for example) to open it.
  const { param, go } = useNav();
  useEffect(() => {
    const linked = param && shares.data?.items.find((s) => s.id === param);
    if (linked) setOpen(linked);
  }, [param, shares.data === undefined]); // eslint-disable-line react-hooks/exhaustive-deps

  const toggle = async (share: Share, included: boolean) => {
    setProblem(undefined);
    // Leaving a share out needs a reason and shows what it hides, so it happens on Scan scope & exclusions (D124).
    if (!included) return go('scan-scope', `exclude:${share.id}`);
    try {
      await api.setIncluded(share.id, included);
      await shares.reload();
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    }
  };

  const list = (shares.data?.items ?? []).filter((s) => s.state !== 'Removed' && (s.source ?? 'FileShare') === 'FileShare');
  const c = coverage.data;
  const fully = c ? c.sharesScanned : 0;

  return (
    <>
      <PageHeader title="File shares" sub="Every share on your enrolled servers, how much of it the last scan covered, and what it could not reach." />
      {(shares.error || coverage.error) && <div className="mb-4"><Callout tone="bad" title="Can't reach the Data Scope server">{shares.error ?? coverage.error}</Callout></div>}
      {problem && <div className="mb-4"><Callout tone="bad" title="That didn't work">{problem}</Callout></div>}

      {c && c.sharesIncluded > 0 && (
        <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mb-4">
          <Stat
            label="Shares fully covered"
            value={`${fully} of ${c.sharesIncluded}`}
            tone={fully < c.sharesIncluded ? 'warn' : 'good'}
          />
          <Stat label="Folders" value={fmtNum(c.folders)} />
          <Stat label="Files" value={fmtNum(c.files)} sub={fmtBytes(c.bytes)} />
          <Stat label="Coverage gaps" value={fmtNum(Object.values(c.gapsByKind).reduce((a, b) => a + (b ?? 0), 0))} tone={Object.keys(c.gapsByKind).length ? 'warn' : 'good'} />
        </div>
      )}

      {shares.data && list.length === 0 && (
        <Card>
          <div className="text-center py-8">
            <HardDrive className="mx-auto text-ink-3" size={28} />
            <div className="font-medium mt-2">No shares found yet</div>
            <p className="text-ink-2 text-[13.5px] mt-1">Add a file server and run a scan under Servers &amp; scans. Its shares appear here.</p>
          </div>
        </Card>
      )}

      {list.length > 0 && (
        <div className="bg-surface border border-line rounded-xl overflow-x-auto">
          <table className="w-full text-[13.5px] min-w-[980px]">
            <thead>
              <tr><Th>Share</Th><Th>Owner</Th><Th>Coverage</Th><Th className="text-right">Folders</Th><Th className="text-right">Files</Th><Th className="text-right">Size</Th><Th>Last scan</Th><Th>Scanned</Th></tr>
            </thead>
            <tbody>
              {list.map((sh) => (
                <tr key={sh.id} className="border-t border-line cursor-pointer hover:bg-surface-2" onClick={() => setOpen(sh)}>
                  <Td>
                    <div className="font-medium flex items-center gap-1.5">
                      <span className="break-all">{sh.name}</span>
                    </div>
                    <Unc>{sh.uncPath}</Unc>
                  </Td>
                  <Td>{sh.ownerName ?? (sh.kind === 'Disk' ? <span className="text-ink-3">None yet</span> : <span className="text-ink-3">—</span>)}</Td>
                  <Td><ShareStateTag share={sh} /></Td>
                  <Td className="text-right tnum">{sh.state === 'NotScanned' ? '—' : fmtNum(sh.folderCount)}</Td>
                  <Td className="text-right tnum">{sh.state === 'NotScanned' ? '—' : fmtNum(sh.fileCount)}</Td>
                  <Td className="text-right tnum">{sh.state === 'NotScanned' ? '—' : fmtBytes(sh.totalBytes)}</Td>
                  <Td className="text-ink-2 whitespace-nowrap">{sh.lastScannedAt ? ago(sh.lastScannedAt) : 'Never'}</Td>
                  <td className="px-3 py-2.5 align-middle" onClick={(e) => e.stopPropagation()}>
                    {sh.kind === 'Disk' ? (
                      <label className="inline-flex items-center gap-2 text-[13px]" title={admin ? undefined : 'Only platform admins can change what is scanned'}>
                        <input type="checkbox" className="size-4 accent-[var(--accent)]" checked={sh.included} disabled={!admin} onChange={(e) => toggle(sh, e.target.checked)} aria-label={`Scan ${sh.name}`} />
                        {sh.included ? 'Yes' : 'No'}
                      </label>
                    ) : null}
                    {sh.kind === 'Disk' && !sh.included && sh.exclusionReason && (
                      <div className="text-[12px] text-ink-3 max-w-[220px] break-words" title={sh.excludedByName ? `Left out by ${sh.excludedByName}` : undefined}>{sh.exclusionReason}</div>
                    )}
                    {sh.kind !== 'Disk' && (
                      <span className="text-[12.5px] text-ink-3" title="Administrative and system shares are never scanned. Scan the share that holds the data instead.">Never (admin share)</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <ShareDrawer share={open} onClose={() => setOpen(undefined)} onChanged={shares.reload} />
    </>
  );
}

export function Stat({ label, value, sub, tone }: { label: string; value: string; sub?: string; tone?: 'warn' | 'good' }) {
  return (
    <div className="bg-surface border border-line rounded-xl px-4 py-3">
      <div className="text-[12px] text-ink-3">{label}</div>
      <div className="font-display text-[22px] font-[650] tnum mt-0.5" style={tone === 'warn' ? { color: 'var(--s-medium)' } : undefined}>{value}</div>
      {sub && <div className="text-[12px] text-ink-3 tnum">{sub}</div>}
    </div>
  );
}

export function ShareStateTag({ share }: { share: Share }) {
  switch (share.state) {
    case 'Scanned': return <Tag tone="good">Complete</Tag>;
    case 'Partial': return <Tag tone="warn" title="Some folders could not be listed. Open the share to see which.">Partial · {fmtNum(share.gapCount)} {share.gapCount === 1 ? 'gap' : 'gaps'}</Tag>;
    case 'Unavailable': return <Tag tone="bad">Could not open</Tag>;
    case 'NotScanned': return <Tag>{share.included ? 'Not scanned yet' : 'Not scanned'}</Tag>;
    default: return <Tag>{share.state}</Tag>;
  }
}

/** A share: its overview, its folders and files, and what the last scan could not reach. */
export function ShareDrawer({ share, onClose, onChanged }: { share?: Share; onClose: () => void; onChanged: () => Promise<unknown> }) {
  const [tab, setTab] = useState<'about' | 'browse' | 'gaps'>('about');
  return (
    <Drawer open={!!share} onClose={() => (onClose(), setTab('about'))} title={share?.name ?? ''} sub={share && <span className="font-mono text-[12.5px]">{share.uncPath}</span>} width={760}>
      {share && (
        <>
          {share.state === 'NotScanned' && <div className="mb-3"><Callout tone="info" title="Not scanned yet">Run a scan of {share.hostname} to list this share, see who has access and assign its owner.</Callout></div>}
          {share.state !== 'NotScanned' && (
            <>
              <Tabs
                value={tab}
                onChange={setTab}
                tabs={[
                  { id: 'about', label: 'Overview' },
                  { id: 'browse', label: 'Folders and files' },
                  { id: 'gaps', label: `Not covered (${fmtNum(share.gapCount)})` },
                ]}
              />
              {tab === 'about' && <ShareOverview share={share} onChanged={onChanged} />}
              {tab === 'browse' && <Browser share={share} />}
              {tab === 'gaps' && <Gaps share={share} />}
            </>
          )}
        </>
      )}
    </Drawer>
  );
}

/** One share at a glance: what it is, who owns it and who can open it. */
function ShareOverview({ share, onChanged }: { share: Share; onChanged: () => Promise<unknown> }) {
  const backend = useBackend();
  const access = useData(() => api.shareAccess(share.id), [share.id]);
  const a = access.data;
  const root = a?.rootFolderId ?? undefined;
  const folder = useData(() => (root ? api.folderAccess(root) : Promise.resolve(undefined)), [root]);
  const [who, setWho] = useState<'people' | 'entries'>('people');
  const openFolder = useOpenFolder();
  const f = folder.data;

  return (
    <div className="flex flex-col gap-5">
      {access.error && <Callout tone="bad">{access.error}</Callout>}
      <section>
        <dl className="grid grid-cols-[120px_1fr] gap-x-3 gap-y-1.5 text-[13.5px]">
          <dt className="text-ink-3">Server</dt><dd>{share.hostname}</dd>
          <dt className="text-ink-3">Path</dt><dd className="font-mono text-[12.5px] break-all">{share.uncPath}</dd>
          {share.remark && <><dt className="text-ink-3">Description</dt><dd>{share.remark}</dd></>}
          <dt className="text-ink-3">Coverage</dt><dd><ShareStateTag share={share} /></dd>
          <dt className="text-ink-3">Contents</dt>
          <dd className="tnum">{fmtNum(share.folderCount)} folders, {fmtNum(share.fileCount)} files, {fmtBytes(share.totalBytes)}</dd>
          <dt className="text-ink-3">Last scan</dt><dd>{share.lastScannedAt ? `${fmtDateTime(share.lastScannedAt)} (${ago(share.lastScannedAt)})` : 'Never'}</dd>
        </dl>
      </section>

      {a && f && (
        <div className="grid sm:grid-cols-3 gap-2">
          {a.openAccess && <Summary label="Any domain user, files here" range={a.openAccess} />}
          <Summary label="People who can open files" value={`${fmtNum(f.usersCertain)}${f.usersPossible ? ` + ${fmtNum(f.usersPossible)} possibly` : ''}`} />
          <Summary label="Places with their own permissions" value={fmtNum(a.boundaries)} />
        </div>
      )}

      <section>
        <h3 className="eyebrow mb-2">Owner</h3>
        {root ? (
          <OwnerPanel folderId={root} admin={canManageReviews(backend)} onChanged={async () => void (await onChanged())} />
        ) : (
          <p className="text-[13.5px] text-ink-3">{access.data ? 'The last scan could not list this share’s top folder, so it can’t have an owner yet.' : 'Loading…'}</p>
        )}
      </section>

      {root && (
        <section className="flex flex-col gap-2">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h3 className="eyebrow">Who has access</h3>
            <Button size="sm" variant="ghost" onClick={() => openFolder(root)}>Open in Who has access <ArrowRight size={13} /></Button>
          </div>
          <div>
            <Segmented value={who} onChange={setWho} options={[{ id: 'people', label: 'People' }, { id: 'entries', label: 'Groups and permission entries' }]} />
          </div>
          {who === 'people' && <PeopleList folderId={root} target="Files" />}
          {who === 'entries' && a && f && (
            <div className="flex flex-col gap-3">
              <div>
                <div className="text-[12.5px] text-ink-3 mb-1">Share permissions</div>
                <PermissionTable set={a.share} share />
              </div>
              <div>
                <div className="text-[12.5px] text-ink-3 mb-1">NTFS permissions on the top folder</div>
                <PermissionTable set={f.folder} />
              </div>
            </div>
          )}
          <p className="text-[12px] text-ink-3">Computed from the permissions the last scan read and the latest directory sync. Not evidence that anyone opened a file.</p>
        </section>
      )}
    </div>
  );
}

function Browser({ share }: { share: Share }) {
  const [trail, setTrail] = useState<Folder[]>([]);
  const current = trail.at(-1);
  const folders = useData(() => api.folders(share.id, current?.id), [share.id, current?.id]);
  const files = useData(() => (current ? api.files(current.id) : Promise.resolve(undefined)), [current?.id]);

  return (
    <div>
      <nav className="flex flex-wrap items-center gap-1 text-[13px] mb-3" aria-label="Folder path">
        <button className="text-accent hover:underline" onClick={() => setTrail([])}>{share.name}</button>
        {trail.map((f, i) => (
          <span key={f.id} className="inline-flex items-center gap-1">
            <ChevronRight size={13} className="text-ink-3" />
            <button className="text-accent hover:underline break-all" onClick={() => setTrail(trail.slice(0, i + 1))}>{f.name}</button>
          </span>
        ))}
      </nav>
      {current && !current.listed && (
        <div className="mb-3"><Callout tone="warn" title="This folder was not listed">The last scan found this folder but could not read inside it. See the “Not covered” tab for why.</Callout></div>
      )}
      {folders.error && <Callout tone="bad">{folders.error}</Callout>}
      <ul className="divide-y divide-line border border-line rounded-lg">
        {(folders.data?.items ?? []).map((f) => (
          <li key={f.id}>
            <button className="w-full flex items-center gap-2.5 px-3 py-2 text-left hover:bg-surface-2 text-[13.5px]" onClick={() => setTrail([...trail, f])}>
              {f.listed ? <FolderIcon size={16} className="text-ink-3 shrink-0" /> : <FolderX size={16} className="text-[var(--s-medium)] shrink-0" />}
              <span className="flex-1 min-w-0 break-all">{f.name}</span>
              {!f.listed && <Tag tone="warn">Not listed</Tag>}
              <ChevronRight size={14} className="text-ink-3" />
            </button>
          </li>
        ))}
        {(files.data?.items ?? []).map((f) => (
          <li key={f.id} className="flex items-center gap-2.5 px-3 py-2 text-[13.5px]">
            <File size={16} className="text-ink-3 shrink-0" />
            <span className="flex-1 min-w-0 break-all">{f.name}</span>
            <span className="text-ink-3 text-[12.5px] tnum whitespace-nowrap">{fmtBytes(f.size)}</span>
            <span className="text-ink-3 text-[12.5px] whitespace-nowrap hidden sm:inline">{f.modifiedAt ? fmtDateTime(f.modifiedAt) : ''}</span>
          </li>
        ))}
        {folders.data && !folders.data.items.length && !(files.data?.items.length) && <li className="px-3 py-6 text-center text-ink-3 text-[13px]">This folder is empty.</li>}
      </ul>
      {((folders.data?.total ?? 0) > 200 || (files.data?.total ?? 0) > 200) && (
        <p className="text-[12px] text-ink-3 mt-2">Showing the first 200 folders and 200 files.</p>
      )}
    </div>
  );
}

function Gaps({ share }: { share: Share }) {
  const gaps = useData(() => api.shareGaps(share.id), [share.id]);
  if (gaps.error) return <Callout tone="bad">{gaps.error}</Callout>;
  const items = gaps.data?.items ?? [];
  if (gaps.data && !items.length) return <Callout tone="good" title="Nothing missed">The last scan listed every folder in this share.</Callout>;
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-[13px] min-w-[520px]">
        <thead><tr><Th>Where</Th><Th>What happened</Th></tr></thead>
        <tbody>
          {items.map((g) => {
            const label = GAP_LABEL[g.kind] ?? { title: g.kind, fix: '' };
            return (
              <tr key={g.id} className="border-t border-line align-top">
                <Td className="font-mono text-[12px] break-all max-w-[240px]">{g.path ? `${share.name}\\${g.path}` : share.name}</Td>
                <Td>
                  <div className="font-medium flex items-center gap-1.5"><AlertTriangle size={13} className="text-[var(--s-medium)]" /> {label.title}</div>
                  <div className="text-ink-2">{g.message}</div>
                  {label.fix && <div className="text-ink-3 text-[12.5px] mt-0.5">{label.fix}</div>}
                </Td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
