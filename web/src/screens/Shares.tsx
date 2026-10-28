import { useMemo, useState } from 'react';
import { ChevronDown, ChevronRight, FolderLock, Pause, Play, RefreshCw } from 'lucide-react';
import { actions, useApp } from '../api/store';
import { useNav } from '../App';
import { useBackend } from '../api/backend';
import LiveShares from './live/LiveShares';
import { whoHasAccess } from '../domain/access';
import { effectiveClass } from '../domain/risk';
import { CLASSIFICATIONS, type Ace } from '../domain/types';
import {
  Button,
  Callout,
  ClassPill,
  Drawer,
  Meter,
  PageHeader,
  Person,
  SeverityPill,
  Tabs,
  Tag,
  Td,
  Th,
  Unc,
  ago,
  classColor,
  fmtBytes,
  fmtNum,
  nameOf,
  rightsLabel,
} from '../ui/kit';
import { AccessExplain } from './AccessExplain';
import { activeUsers, ownedShareIds, shareStats } from './common';

export default function Shares() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LiveShares />;
  return <DemoShares />;
}

function DemoShares() {
  const s = useApp();
  const { param, role, risks } = useNav();
  const [open, setOpen] = useState<string | undefined>(param);
  const visible = role.role === 'Data Owner' ? s.shares.filter((x) => ownedShareIds(s, role.sid).includes(x.id)) : s.shares;
  const users = activeUsers(s);

  return (
    <>
      <PageHeader
        title="Shares"
        sub="Every share Data Scope scans, what it holds, who can open it, and who is accountable for it."
      />
      <div className="bg-surface border border-line rounded-xl overflow-x-auto">
        <table className="w-full text-[13.5px] min-w-[880px]">
          <thead>
            <tr>
              <Th>Share</Th>
              <Th>Owner</Th>
              <Th>Classification mix</Th>
              <Th className="text-right">Sensitive</Th>
              <Th className="text-right">Who can open</Th>
              <Th>Risks</Th>
              <Th>Last scan</Th>
            </tr>
          </thead>
          <tbody>
            {visible.map((sh) => {
              const st = shareStats(s, sh.id);
              const owner = s.owners.find((o) => o.shareId === sh.id);
              const shareRisks = risks.filter((r) => r.shareId === sh.id);
              const worst = shareRisks[0];
              return (
                <tr key={sh.id} className="border-t border-line cursor-pointer hover:bg-surface-2" onClick={() => setOpen(sh.id)}>
                  <Td>
                    <div className="font-medium">{sh.name}</div>
                    <Unc>{sh.unc}</Unc>
                  </Td>
                  <Td>{owner?.confirmed && owner.ownerSid ? <Person sid={owner.ownerSid} size={24} sub={owner.department} /> : <Tag tone="warn">No owner</Tag>}</Td>
                  <Td>
                    <div className="flex h-2 w-40 rounded-full overflow-hidden gap-[2px]">
                      {CLASSIFICATIONS.map((c) => (st.byClass[c] ? <div key={c} title={`${c}: ${st.byClass[c]}`} style={{ flexGrow: st.byClass[c], background: classColor(c), minWidth: 3 }} /> : null))}
                    </div>
                    <div className="text-[12px] text-ink-3 mt-1 tnum">
                      {fmtNum(st.files.length)} files · {fmtBytes(st.bytes)}
                    </div>
                  </Td>
                  <Td className="text-right tnum font-medium">{st.sensitive}</Td>
                  <Td className="text-right tnum">
                    {st.peopleWithAccess}
                    <span className="text-ink-3"> / {users}</span>
                  </Td>
                  <Td>{worst ? <span className="inline-flex items-center gap-1.5"><SeverityPill s={worst.severity} /><span className="text-ink-3 text-[12px]">{shareRisks.length > 1 && `+${shareRisks.length - 1}`}</span></span> : <span className="text-ink-3">None</span>}</Td>
                  <Td>
                    <ScanCell shareId={sh.id} />
                  </Td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {open && <ShareDrawer shareId={open} onClose={() => setOpen(undefined)} />}
    </>
  );
}

function ScanCell({ shareId }: { shareId: string }) {
  const s = useApp();
  const sh = s.shares.find((x) => x.id === shareId)!;
  if (sh.scan.state === 'running' || sh.scan.state === 'paused' || sh.scan.state === 'queued')
    return (
      <div className="w-28">
        <div className="text-[12px] text-ink-2 mb-1 tnum">{sh.scan.state === 'paused' ? 'Paused' : 'Scanning'} {Math.round(sh.scan.progress * 100)}%</div>
        <Meter value={sh.scan.progress} />
      </div>
    );
  return (
    <span className="text-[13px] whitespace-nowrap">
      {ago(sh.scan.lastScan)}
      {sh.scan.state === 'partial' && <span className="ml-1.5"><Tag tone="warn">Partial</Tag></span>}
    </span>
  );
}

type Tab = 'summary' | 'access' | 'permissions' | 'files';

export function ShareDrawer({ shareId, onClose }: { shareId: string; onClose: () => void }) {
  const s = useApp();
  const { go, risks, readOnly, role } = useNav();
  const [tab, setTab] = useState<Tab>('summary');
  const sh = s.shares.find((x) => x.id === shareId)!;
  const st = shareStats(s, shareId);
  const owner = s.owners.find((o) => o.shareId === shareId);
  const job = s.jobs.find((j) => j.shareId === shareId);
  const shareRisks = risks.filter((r) => r.shareId === shareId);
  const server = s.servers.find((x) => x.id === sh.serverId)!;

  return (
    <Drawer open onClose={onClose} title={sh.name} sub={<Unc>{sh.unc}</Unc>} width={680}>
      <Tabs<Tab>
        value={tab}
        onChange={setTab}
        tabs={[
          { id: 'summary', label: 'Summary' },
          { id: 'access', label: `Who can open it (${st.peopleWithAccess})` },
          { id: 'permissions', label: 'Permissions' },
          { id: 'files', label: `Sensitive files (${st.sensitive})` },
        ]}
      />
      {tab === 'summary' && (
        <div className="flex flex-col gap-5">
          <p className="text-ink-2">{sh.description}</p>
          <dl className="grid grid-cols-2 gap-x-6 gap-y-3 text-[13px]">
            <div><dt className="text-ink-3">Server</dt><dd>{server.hostname} · {server.os}</dd></div>
            <div><dt className="text-ink-3">Files</dt><dd className="tnum">{fmtNum(st.files.length)} ({fmtBytes(st.bytes)})</dd></div>
            <div><dt className="text-ink-3">Owner</dt><dd>{owner?.confirmed && owner.ownerSid ? nameOf(owner.ownerSid) : <Tag tone="warn">No owner</Tag>}</dd></div>
            <div><dt className="text-ink-3">Review cadence</dt><dd>{owner?.cadence}</dd></div>
            <div><dt className="text-ink-3">Not scanned</dt><dd className="tnum">{st.unscanned} files (unsupported, encrypted, corrupt or denied)</dd></div>
            <div><dt className="text-ink-3">Last scan</dt><dd>{ago(sh.scan.lastScan)}</dd></div>
          </dl>
          {!readOnly && role.role !== 'Data Owner' && (
            <div className="flex flex-wrap gap-2 items-center">
              {job ? (
                <>
                  <Button onClick={() => actions.pauseScan(shareId)}>{job.state === 'running' ? <><Pause size={15} /> Pause scan</> : <><Play size={15} /> Resume scan</>}</Button>
                  <div className="w-40"><Meter value={job.done / job.total} /></div>
                  <span className="text-[12px] text-ink-3 tnum">{job.done} / {job.total}</span>
                </>
              ) : (
                <Button onClick={() => actions.startScan(shareId)}><RefreshCw size={15} /> Rescan now</Button>
              )}
              {!owner?.confirmed && <Button variant="secondary" onClick={() => go('owners')}>Assign owner</Button>}
            </div>
          )}
          <div>
            <div className="eyebrow mb-2">Risks on this share</div>
            {shareRisks.length ? (
              <ul className="flex flex-col divide-y divide-[var(--line)] border border-line rounded-lg">
                {shareRisks.map((r) => (
                  <li key={r.id}>
                    <button className="w-full text-left flex gap-3 items-start px-3 py-2.5 hover:bg-surface-2" onClick={() => go('risks', r.id)}>
                      <SeverityPill s={r.severity} />
                      <span className="text-[13px]">{r.title}</span>
                    </button>
                  </li>
                ))}
              </ul>
            ) : (
              <p className="text-ink-3 text-[13px]">No risks found in the last scan.</p>
            )}
          </div>
        </div>
      )}
      {tab === 'access' && <WhoCanOpen shareId={shareId} />}
      {tab === 'permissions' && <Permissions shareId={shareId} />}
      {tab === 'files' && (
        <ul className="flex flex-col divide-y divide-[var(--line)]">
          {st.files
            .filter((f) => ['Restricted', 'Confidential'].includes(effectiveClass(f)))
            .map((f) => (
              <li key={f.id}>
                <button className="w-full text-left py-2.5 flex items-center gap-3 hover:bg-surface-2 px-1" onClick={() => go('discovery', f.id)}>
                  <ClassPill c={effectiveClass(f)} />
                  <span className="min-w-0">
                    <span className="block truncate">{f.name}</span>
                    <span className="block text-[12px] text-ink-3 truncate font-mono">{f.path}</span>
                  </span>
                </button>
              </li>
            ))}
        </ul>
      )}
    </Drawer>
  );
}

function WhoCanOpen({ shareId }: { shareId: string }) {
  const s = useApp();
  const sh = s.shares.find((x) => x.id === shareId)!;
  const scopes = ['', ...sh.folders.filter((f) => f.inheritanceBroken).map((f) => f.path)];
  const [scope, setScope] = useState('');
  const [expanded, setExpanded] = useState<string | null>(null);
  const rows = useMemo(() => whoHasAccess(s.dir, sh, scope || undefined), [s.dir, sh, scope]);
  return (
    <div>
      {scopes.length > 1 && (
        <div className="flex flex-wrap items-center gap-2 mb-3 text-[13px]">
          <span className="text-ink-3">Folder:</span>
          {scopes.map((sc) => (
            <button key={sc} onClick={() => setScope(sc)} className={`px-2 h-7 rounded-md border text-[12.5px] ${scope === sc ? 'border-accent bg-accent-soft' : 'border-line hover:bg-surface-2'}`}>
              {sc ? <span className="inline-flex items-center gap-1"><FolderLock size={13} />{sc}</span> : 'Share root'}
            </button>
          ))}
        </div>
      )}
      {scope && <div className="mb-3"><Callout tone="info">This folder does not inherit permissions from the share root, so it has its own list.</Callout></div>}
      <ul className="border border-line rounded-lg divide-y divide-[var(--line)]">
        {rows.map((e) => (
          <li key={e.userSid}>
            <button className="w-full flex items-center gap-3 px-3 py-2 hover:bg-surface-2 text-left" onClick={() => setExpanded(expanded === e.userSid ? null : e.userSid)} aria-expanded={expanded === e.userSid}>
              {expanded === e.userSid ? <ChevronDown size={15} className="text-ink-3" /> : <ChevronRight size={15} className="text-ink-3" />}
              <span className="flex-1 min-w-0"><Person sid={e.userSid} size={24} /></span>
              {e.confidence === 'partial' && <Tag tone="warn">Partial</Tag>}
              <span className="text-[13px] font-medium w-24 text-right">{rightsLabel(e.rights)}</span>
            </button>
            {expanded === e.userSid && (
              <div className="px-4 pb-4 pt-1 bg-surface-2/50">
                <AccessExplain e={e} />
              </div>
            )}
          </li>
        ))}
      </ul>
    </div>
  );
}

function Permissions({ shareId }: { shareId: string }) {
  const s = useApp();
  const sh = s.shares.find((x) => x.id === shareId)!;
  const Table = ({ acl, title }: { acl: Ace[]; title: string }) => (
    <div className="mb-5">
      <div className="eyebrow mb-1.5">{title}</div>
      <div className="border border-line rounded-lg overflow-x-auto">
        <table className="w-full text-[13px] min-w-[480px]">
          <thead>
            <tr><Th>Trustee</Th><Th>Type</Th><Th>Rights</Th><Th>Mask</Th><Th>Members</Th></tr>
          </thead>
          <tbody>
            {acl.map((a, i) => {
              const p = s.dir.get(a.sid);
              return (
                <tr key={i} className="border-t border-line">
                  <Td>{p ? p.displayName : <span className="font-mono text-[12px]">{a.sid}</span>}{!p && <span className="ml-1.5"><Tag tone="bad">Unresolved</Tag></span>}</Td>
                  <Td>{a.type === 'deny' ? <Tag tone="bad">Deny</Tag> : 'Allow'}</Td>
                  <Td>{rightsLabel(a.rights)}{a.inherited && <span className="text-ink-3"> · inherited</span>}</Td>
                  <Td className="font-mono text-[12px] text-ink-3">{a.mask}</Td>
                  <Td className="text-ink-3">{p?.type === 'user' ? 'Person' : p ? 'Group' : '—'}</Td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
    </div>
  );
  return (
    <div>
      <Callout tone="info" title="How Windows decides">
        A person gets the more restrictive of the share permission and the NTFS permission. Deny entries win over allow entries at the same level.
      </Callout>
      <div className="h-4" />
      <Table acl={sh.shareAcl} title="Share permissions" />
      <Table acl={sh.ntfsAcl} title="NTFS permissions (share root)" />
      {sh.folders.filter((f) => f.inheritanceBroken && f.ntfsAcl).map((f) => (
        <Table key={f.path} acl={f.ntfsAcl!} title={`NTFS: ${f.path} (inheritance disabled)`} />
      ))}
    </div>
  );
}
