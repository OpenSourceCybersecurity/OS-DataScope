import { useState } from 'react';
import { ArrowRight, Ban, ChevronDown, ChevronRight, CircleHelp, FolderLock, RefreshCw, Search, ShieldAlert, Users } from 'lucide-react';
import {
  ANY_USER,
  api,
  type AccessRange,
  type AccessTarget,
  type AceEntry,
  type ExposureStatus,
  type PermissionSet,
  type Trustee,
  type UserAccess,
} from '../../api/client';
import { canManage, useBackend } from '../../api/backend';
import { useNav } from '../../App';
import { Button, Callout, Card, Drawer, PageHeader, Segmented, Tabs, Tag, Td, Th, Unc, ago, cx, fmtNum, filterCls, inputCls } from '../../ui/kit';
import { CERTAINTY, EXPOSURE, TRUSTEE_KIND, directoryFreshness, openText, openTone, parseAccessParam, rangeText, type Tone } from './access';
import { useData } from './useData';

const tagTone = (t: Tone) => (t === 'neutral' ? 'neutral' : t);

export default function LiveAccess() {
  const { param } = useNav();
  const link = parseAccessParam(param);
  const [tab, setTab] = useState<'open' | 'folders' | 'people'>(link.sid ? 'people' : 'open');
  const [folder, setFolder] = useState<string | undefined>(link.folderId);

  return (
    <>
      <PageHeader
        title="Who has access"
        sub="Computed from the share and NTFS permissions the last scan read and the latest Active Directory sync. Ranges and certainty show what could not be read. Nothing here changes permissions."
      />
      <DirectoryStrip />
      <Tabs
        value={tab}
        onChange={setTab}
        tabs={[
          { id: 'open', label: 'Open to everyone' },
          { id: 'folders', label: 'By folder' },
          { id: 'people', label: 'By person' },
        ]}
      />
      {tab === 'open' && <OpenAccess onOpen={setFolder} />}
      {tab === 'folders' && <ByFolder onOpen={setFolder} />}
      {tab === 'people' && <ByPerson initial={link.sid} initialKind={link.kind} onOpen={setFolder} />}
      {folder && <FolderDrawer folderId={folder} onClose={() => setFolder(undefined)} />}
    </>
  );
}

export function DirectoryStrip() {
  const backend = useBackend();
  const status = useData(() => api.directory(), [], 5000);
  const [problem, setProblem] = useState<string>();
  const fresh = directoryFreshness(status.data);
  const sync = async () => {
    setProblem(undefined);
    try {
      await api.syncDirectory();
      await status.reload();
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    }
  };
  const busy = !!status.data?.pending;
  return (
    <div className="mb-4 flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-3 rounded-lg border border-line bg-surface px-3.5 py-2 text-[13px]">
        <Users size={16} className="text-ink-3 shrink-0" />
        <span className="flex-1 min-w-[200px]">
          {status.error ? `Can't reach the Data Scope server: ${status.error}` : fresh.text}
          {status.data?.latest?.completedAt && <span className="text-ink-3"> Last sync {ago(status.data.latest.completedAt)}.</span>}
          {!!status.data?.latest?.cycles && <span className="text-ink-3"> {status.data.latest.cycles} group nesting {status.data.latest.cycles === 1 ? 'loop' : 'loops'} found.</span>}
          {!!status.data?.latest?.unresolvedMembers && (
            <span className="text-ink-3">
              {' '}
              {fmtNum(status.data.latest.unresolvedMembers)} group {status.data.latest.unresolvedMembers === 1 ? 'member' : 'members'} could not be resolved, so access through
              {status.data.latest.unresolvedMembers === 1 ? ' that group' : ' those groups'} is shown as possible.
            </span>
          )}
        </span>
        {fresh.tone !== 'good' && <Tag tone={tagTone(fresh.tone)}>{fresh.tone === 'bad' ? 'Sync failed' : 'Check'}</Tag>}
        {canManage(backend) && (
          <Button size="sm" onClick={sync} disabled={busy} title="Reads users, groups and memberships from Active Directory. Read-only.">
            <RefreshCw size={14} className={cx(busy && 'animate-spin')} /> {busy ? 'Syncing…' : 'Sync now'}
          </Button>
        )}
      </div>
      {problem && <Callout tone="bad" title="That didn't work">{problem}</Callout>}
    </div>
  );
}

// ---------------------------------------------------------------- open access

function OpenAccess({ onOpen }: { onOpen: (folderId: string) => void }) {
  const [status, setStatus] = useState<ExposureStatus | ''>('');
  const exposure = useData(() => api.exposure({ status: status || undefined }), [status]);
  const e = exposure.data;
  return (
    <div className="flex flex-col gap-4">
      {exposure.error && <Callout tone="bad" title="Can't load open access">{exposure.error}</Callout>}
      {e && (
        <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
          <Tile label="Folders open to everyone" value={e.open} tone={e.open ? 'bad' : 'good'} onClick={() => setStatus('Open')} />
          <Tile label="Sensitive files in them" value={e.sensitiveFilesOpen} tone={e.sensitiveFilesOpen ? 'bad' : 'good'} />
          <Tile label="Possibly open" value={e.possiblyOpen} tone={e.possiblyOpen ? 'warn' : 'good'} onClick={() => setStatus('PossiblyOpen')} />
          <Tile label="Permissions unknown" value={e.unknown} tone={e.unknown ? 'warn' : 'good'} onClick={() => setStatus('Unknown')} />
        </div>
      )}
      <div className="flex flex-wrap items-center gap-3">
        <Segmented
          value={status}
          onChange={setStatus}
          options={[
            { id: '', label: 'All' },
            { id: 'Open', label: 'Open' },
            { id: 'PossiblyOpen', label: 'Possibly open' },
            { id: 'Unknown', label: 'Unknown' },
          ]}
        />
        <span className="text-[12.5px] text-ink-3">
          Folders holding Confidential or Restricted files, checked against an ordinary domain user with no special groups.
        </span>
      </div>
      {e?.truncated && <Callout tone="warn">Only the 20,000 folders with the most sensitive files were checked. Narrow by share to see the rest.</Callout>}
      {e && e.folders.items.length === 0 && (
        <Card>
          <div className="text-center py-8">
            <ShieldAlert className="mx-auto text-ink-3" size={28} />
            <div className="font-medium mt-2">{e.foldersChecked === 0 ? 'No sensitive files found yet' : 'Nothing open to everyone'}</div>
            <p className="text-ink-2 text-[13.5px] mt-1">
              {e.foldersChecked === 0
                ? 'Once a scan finds Confidential or Restricted files, Data Scope checks who can reach them here.'
                : `Checked ${fmtNum(e.foldersChecked)} folders with sensitive files.`}
            </p>
          </div>
        </Card>
      )}
      {e && e.folders.items.length > 0 && (
        <div className="bg-surface border border-line rounded-xl overflow-x-auto">
          <table className="w-full text-[13.5px] min-w-[760px]">
            <thead>
              <tr><Th>Folder</Th><Th>Status</Th><Th>Any domain user</Th><Th className="text-right">Restricted</Th><Th className="text-right">Confidential</Th></tr>
            </thead>
            <tbody>
              {e.folders.items.map((i) => (
                <tr key={i.folderId} className="border-t border-line cursor-pointer hover:bg-surface-2" onClick={() => onOpen(i.folderId)}>
                  <Td><Unc>{i.uncPath}</Unc></Td>
                  <Td><span title={EXPOSURE[i.status].help}><Tag tone={tagTone(EXPOSURE[i.status].tone)}>{EXPOSURE[i.status].label}</Tag></span></Td>
                  <Td>{rangeText(i.openAccess)}</Td>
                  <Td className="text-right tnum">{fmtNum(i.restrictedFiles)}</Td>
                  <Td className="text-right tnum">{fmtNum(i.confidentialFiles)}</Td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {e && e.folders.total > e.folders.items.length && <p className="text-[12px] text-ink-3">Showing the first {e.folders.items.length} of {fmtNum(e.folders.total)} folders.</p>}
    </div>
  );
}

function Tile({ label, value, tone, onClick }: { label: string; value: number; tone: Tone; onClick?: () => void }) {
  const color = tone === 'bad' ? 'var(--s-critical)' : tone === 'warn' ? 'var(--s-medium)' : undefined;
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={!onClick}
      className="text-left bg-surface border border-line rounded-xl px-4 py-3 enabled:hover:bg-surface-2 disabled:cursor-default"
    >
      <div className="text-[12px] text-ink-3">{label}</div>
      <div className="font-display text-[22px] font-[650] tnum mt-0.5" style={{ color }}>{fmtNum(value)}</div>
    </button>
  );
}

// ---------------------------------------------------------------- by folder

function ByFolder({ onOpen }: { onOpen: (folderId: string) => void }) {
  const shares = useData(() => api.shares(), []);
  const list = (shares.data?.items ?? []).filter((s) => s.kind === 'Disk' && (s.source ?? 'FileShare') === 'FileShare' && s.state !== 'Removed' && s.state !== 'NotScanned');
  const [shareId, setShareId] = useState('');
  const current = shareId || list[0]?.id || '';
  if (shares.error) return <Callout tone="bad">{shares.error}</Callout>;
  if (shares.data && !list.length) return <Callout tone="info" title="No scanned shares yet">Run a scan under Servers &amp; scans. Each share’s permissions appear here.</Callout>;
  return (
    <div className="flex flex-col gap-4">
      <label className="flex items-center gap-2 text-[13px]">
        <span className="text-ink-3">Share</span>
        <select className={filterCls} value={current} onChange={(e) => setShareId(e.target.value)} aria-label="Share">
          {list.map((s) => (
            <option key={s.id} value={s.id}>{s.uncPath}</option>
          ))}
        </select>
      </label>
      {current && <ShareAccessView shareId={current} onOpen={onOpen} />}
    </div>
  );
}

function ShareAccessView({ shareId, onOpen }: { shareId: string; onOpen: (folderId: string) => void }) {
  const share = useData(() => api.shareAccess(shareId), [shareId]);
  const boundaries = useData(() => api.boundaries(shareId), [shareId]);
  const s = share.data;
  if (share.error) return <Callout tone="bad">{share.error}</Callout>;
  const notRead = s ? (s.folderPermissionStates.AccessDenied ?? 0) + (s.folderPermissionStates.Unreadable ?? 0) : 0;
  return (
    <>
      {s && (
        <Card title="Share permissions" action={s.openAccess && <Tag tone={tagTone(openTone(s.openAccess))}>{openText(s.openAccess)}</Tag>}>
          <p className="text-[13px] text-ink-2 mb-3">
            Windows applies these first; a user gets the lesser of the share permission and the NTFS permission.
          </p>
          <PermissionTable set={s.share} share />
          {notRead > 0 && (
            <div className="mt-3"><Callout tone="warn">The scan account could not read the permissions of {fmtNum(notRead)} {notRead === 1 ? 'folder' : 'folders'} in this share. Their access is unknown.</Callout></div>
          )}
        </Card>
      )}
      <Card title="Where permissions are set" pad={false}>
        <p className="px-4 pb-3 text-[13px] text-ink-2">
          The share root and every folder with its own entries or with inheritance turned off. Folders below them inherit the same access.
        </p>
        {boundaries.error && <div className="px-4 pb-4"><Callout tone="bad">{boundaries.error}</Callout></div>}
        <ul className="divide-y divide-[var(--line)] border-t border-line">
          {(boundaries.data?.items ?? []).map((b) => (
            <li key={b.folderId}>
              <button className="w-full text-left flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5 hover:bg-surface-2" onClick={() => onOpen(b.folderId)}>
                <FolderLock size={16} className="text-ink-3 shrink-0" />
                <span className="flex-1 min-w-[180px] font-mono text-[12.5px] break-all">{b.path ? `${s?.name ?? ''}\\${b.path}` : `${s?.name ?? ''} (share root)`}</span>
                {b.state !== 'Read' && <Tag tone="warn">{b.state === 'AccessDenied' ? 'Permissions not readable' : 'Permissions unknown'}</Tag>}
                {b.protected && b.path !== '' && <Tag title="This folder does not inherit permissions from its parent.">Inheritance off</Tag>}
                <Tag tone={tagTone(openTone(b.openAccess))}>{openText(b.openAccess)}</Tag>
                <ChevronRight size={14} className="text-ink-3" />
              </button>
            </li>
          ))}
        </ul>
        {boundaries.data && boundaries.data.total > boundaries.data.items.length && (
          <p className="px-4 py-2 text-[12px] text-ink-3">Showing {boundaries.data.items.length} of {fmtNum(boundaries.data.total)}.</p>
        )}
      </Card>
    </>
  );
}

// ---------------------------------------------------------------- by person

function ByPerson({ initial, initialKind = 'User', onOpen }: { initial?: string; initialKind?: 'User' | 'Group'; onOpen: (folderId: string) => void }) {
  const [q, setQ] = useState('');
  const [term, setTerm] = useState('');
  const [sel, setSel] = useState<string | undefined>(initial);
  const [kind, setKind] = useState<'User' | 'Group'>(initialKind);
  const people = useData(() => api.principals(term, kind, 0, 50), [term, kind]);
  const list = (people.data?.items ?? []).filter((p) => p.kind === kind);
  return (
    <div className="grid lg:grid-cols-[300px_1fr] gap-4 items-start">
      <Card pad={false} className="lg:sticky lg:top-20">
        <form
          className="p-3 border-b border-line relative"
          onSubmit={(e) => {
            e.preventDefault();
            setTerm(q.trim());
          }}
        >
          <Search size={15} className="absolute left-5.5 top-1/2 -translate-y-1/2 text-ink-3" />
          <input id="principal-search" className={cx(inputCls, 'pl-8')} placeholder={kind === 'User' ? 'Search people' : 'Search groups'} value={q} onChange={(e) => setQ(e.target.value)} onBlur={() => setTerm(q.trim())} />
        </form>
        <div className="px-3 py-2 border-b border-line">
          <Segmented value={kind} onChange={setKind} options={[{ id: 'User', label: 'People' }, { id: 'Group', label: 'Groups' }]} />
        </div>
        <button
          className={cx('w-full text-left px-3 py-2 text-[13px] border-b border-line hover:bg-surface-2', sel === ANY_USER && 'bg-accent-soft')}
          onClick={() => setSel(ANY_USER)}
        >
          <span className="font-medium">Any domain user</span>
          <span className="block text-[12px] text-ink-3">What every account gets without special groups</span>
        </button>
        {people.error && <div className="p-3"><Callout tone="bad">{people.error}</Callout></div>}
        <ul className="max-h-[60vh] overflow-y-auto scroll-thin py-1">
          {list.map((p) => (
            <li key={p.sid}>
              <button onClick={() => setSel(p.sid)} className={cx('w-full text-left px-3 py-1.5 hover:bg-surface-2', sel === p.sid && 'bg-accent-soft')}>
                <span className="block text-[13.5px]">{p.displayName ?? p.name}{!p.enabled && <span className="text-ink-3"> (disabled)</span>}</span>
                <span className="block text-[12px] text-ink-3">{p.kind === 'Group' ? (p.isSecurityGroup ? 'Security group' : 'Distribution group') : [p.title, p.department].filter(Boolean).join(' · ') || p.name}</span>
              </button>
            </li>
          ))}
          {people.data && !list.length && <li className="px-3 py-4 text-[13px] text-ink-3">{term ? `No ${kind === 'User' ? 'people' : 'groups'} match “${term}”.` : people.data.total ? `No ${kind === 'User' ? 'people' : 'groups'} found.` : 'No directory data yet.'}</li>}
        </ul>
        {people.data && people.data.total > list.length && <p className="px-3 py-2 text-[12px] text-ink-3 border-t border-line">Showing the first {list.length}. Search to narrow.</p>}
      </Card>
      <div className="min-w-0">
        {!sel && <Callout tone="info">Pick a user to see every place they can open files, or a group to see who is in it.</Callout>}
        {sel && <PersonView key={sel} sid={sel} onOpen={onOpen} onPick={setSel} />}
      </div>
    </div>
  );
}

function PersonView({ sid, onOpen, onPick }: { sid: string; onOpen: (folderId: string) => void; onPick: (sid: string) => void }) {
  const detail = useData(() => (sid === ANY_USER ? Promise.resolve(undefined) : api.principal(sid)), [sid]);
  const isGroup = detail.data?.principal.kind === 'Group';
  // Wait to know whether it's a group: reach is for user accounts, and asking for a group's shows an error.
  const known = sid === ANY_USER || detail.data !== undefined;
  const reach = useData(() => (!known || isGroup ? Promise.resolve(undefined) : api.reach(sid)), [sid, known, isGroup]);
  const d = detail.data;
  return (
    <div className="flex flex-col gap-4">
      {detail.error && <Callout tone="bad">{detail.error}</Callout>}
      <Card>
        <div className="flex flex-wrap items-start justify-between gap-4">
          <div>
            <div className="font-display text-[18px] font-[650]">{sid === ANY_USER ? 'Any domain user' : (d?.principal.displayName ?? d?.principal.name ?? '…')}</div>
            <div className="text-[12.5px] text-ink-3">
              {sid === ANY_USER ? 'An ordinary enabled account: Everyone, Authenticated Users, Domain Users and local groups that contain them.' : [d?.principal.userPrincipalName ?? d?.principal.name, d?.principal.title, d?.principal.department].filter(Boolean).join(' · ')}
            </div>
          </div>
          <div className="flex flex-wrap gap-1.5">
            {d && !d.principal.enabled && <Tag tone="warn">Disabled</Tag>}
            {d?.inCycle && <Tag tone="warn">In a group nesting loop</Tag>}
            {d?.membersIncomplete && <Tag tone="warn">Some members not resolvable</Tag>}
            {d?.principal.kind === 'Group' && !d.principal.isSecurityGroup && <Tag>Distribution group (grants no access)</Tag>}
          </div>
        </div>
        {d?.principal.kind === 'User' && (d.manager || d.lastLogon) && (
          <div className="text-[12.5px] text-ink-3 mt-2 flex flex-wrap gap-x-3">
            {d.manager && (
              <span>
                Reports to <button className="text-accent hover:underline" onClick={() => onPick(d.manager!.sid)}>{d.manager.name}</button>
              </span>
            )}
            {d.lastLogon && <span>Last sign-in {ago(d.lastLogon)}</span>}
          </div>
        )}
      </Card>

      {!isGroup && reach.data && (
        <Card title={`Can open files in ${fmtNum(reach.data.places.total)} ${reach.data.places.total === 1 ? 'place' : 'places'}`} pad={false}>
          <p className="px-4 pb-3 text-[13px] text-ink-2">
            Share roots and folders where permissions are set. Subfolders that inherit are not listed separately; in all, {fmtNum(reach.data.foldersWithAccess)} folders
            {reach.data.foldersCertain < reach.data.foldersWithAccess && <> ({fmtNum(reach.data.foldersWithAccess - reach.data.foldersCertain)} only possibly)</>}.
          </p>
          <ul className="divide-y divide-[var(--line)] border-t border-line">
            {reach.data.places.items.map((p) => (
              <li key={p.folderId}>
                <button className="w-full text-left flex flex-wrap items-center gap-3 px-4 py-2.5 hover:bg-surface-2" onClick={() => onOpen(p.folderId)}>
                  <span className="flex-1 min-w-[200px]"><Unc>{p.uncPath}</Unc></span>
                  {p.certainty !== 'Likely' && <span title={CERTAINTY[p.certainty].help}><Tag tone="warn">{CERTAINTY[p.certainty].label}</Tag></span>}
                  <span className="w-40 text-right font-medium text-[13px]">{rangeText(p)}</span>
                </button>
              </li>
            ))}
            {!reach.data.places.items.length && <li className="px-4 py-6 text-ink-3 text-[13px]">No access to any scanned folder.</li>}
          </ul>
        </Card>
      )}
      {reach.error && <Callout tone="bad">{reach.error}</Callout>}

      {d && d.principal.kind === 'User' && (
        <Card title="Group memberships">
          {d.memberOf.length ? (
            <div className="flex flex-wrap gap-1.5">
              {d.memberOf.map((g) => (
                <button key={g.sid} onClick={() => onPick(g.sid)} className="rounded px-2 py-0.5 text-[12.5px] bg-surface-2 hover:bg-surface-3">{g.name}</button>
              ))}
            </div>
          ) : (
            <p className="text-[13px] text-ink-3">Only the built-in groups every account has.</p>
          )}
          {!!d.maybeMemberOf.length && (
            <p className="text-[12.5px] text-ink-3 mt-3">Possibly also in {d.maybeMemberOf.map((g) => g.name).join(', ')}: these groups have members Data Scope could not resolve.</p>
          )}
        </Card>
      )}

      {d && d.principal.kind === 'Group' && (
        <Card title={`Members (${fmtNum(d.usersCertain ?? 0)} users${d.usersPossible ? `, ${fmtNum(d.usersPossible)} possibly` : ''})`}>
          {!!d.directGroups.length && (
            <p className="text-[13px] mb-3"><span className="text-ink-3">Member of </span>{d.directGroups.map((g) => g.name).join(', ')}</p>
          )}
          <div className="grid sm:grid-cols-2 gap-1">
            {d.members.map((m) => (
              <button key={m.sid} onClick={() => (m.kind === 'User' || m.kind === 'Group' ? onPick(m.sid) : undefined)} className="text-left rounded-md hover:bg-surface-2 px-2 py-1">
                <span className="block text-[13px]">{m.name}</span>
                <span className="block text-[11.5px] text-ink-3">{TRUSTEE_KIND[m.kind]}{m.enabled === false ? ' · disabled' : ''}</span>
              </button>
            ))}
          </div>
          {d.directMemberCount > d.members.length && <p className="text-[12px] text-ink-3 mt-2">Showing {d.members.length} of {fmtNum(d.directMemberCount)} direct members.</p>}
        </Card>
      )}
    </div>
  );
}

// ---------------------------------------------------------------- folder drawer

export function FolderDrawer({ folderId, onClose }: { folderId: string; onClose: () => void }) {
  const access = useData(() => api.folderAccess(folderId), [folderId]);
  const [tab, setTab] = useState<'people' | 'permissions'>('people');
  const [target, setTarget] = useState<AccessTarget>('Files');
  const a = access.data;
  return (
    <Drawer open onClose={onClose} title={a ? a.path.split('\\').at(-1) || a.shareName : 'Loading…'} sub={a && <Unc>{a.uncPath}</Unc>} width={720}>
      {access.error && <Callout tone="bad">{access.error}</Callout>}
      {a && (
        <div className="flex flex-col gap-4">
          <div className="grid sm:grid-cols-3 gap-2">
            <Summary label="Any domain user, files here" range={a.openFiles} />
            <Summary label="People who can open files" value={`${fmtNum(a.usersCertain)}${a.usersPossible ? ` + ${fmtNum(a.usersPossible)} possibly` : ''}`} />
            <Summary label="Folder permissions" value={a.folder.state === 'Read' ? (a.folder.protected ? 'Set here, not inherited' : 'Inherited and own entries') : a.folder.state === 'AccessDenied' ? 'Scan account may not read' : 'Not read'} />
          </div>
          {a.directory.localGroups !== 'Read' && a.directory.localGroups !== 'NotApplicable' && (
            <Callout tone="warn">
              Data Scope could not read {a.hostname}’s local groups (such as BUILTIN\Users), so access through them is shown as possible. Give the scan account local
              administrator rights on the file server, or allow it under “Restrict clients allowed to make remote calls to SAM”.
            </Callout>
          )}
          {!a.directory.snapshotId && <Callout tone="warn">Active Directory has not been synced, so only built-in groups are considered.</Callout>}
          <Tabs value={tab} onChange={setTab} tabs={[{ id: 'people', label: 'People' }, { id: 'permissions', label: 'Permission entries' }]} />
          {tab === 'people' && (
            <>
              <Segmented value={target} onChange={setTarget} options={[{ id: 'Files', label: 'Files in this folder' }, { id: 'Folder', label: 'The folder itself' }]} />
              <PeopleList folderId={folderId} target={target} />
            </>
          )}
          {tab === 'permissions' && (
            <div className="flex flex-col gap-4">
              <div>
                <div className="eyebrow mb-1.5">Share ({a.shareName})</div>
                <PermissionTable set={a.share} share />
              </div>
              <div>
                <div className="eyebrow mb-1.5">NTFS</div>
                {a.folder.owner && <p className="text-[12.5px] text-ink-3 mb-1.5">Owner: {a.folder.owner.name}. An owner can always change the permissions.</p>}
                <PermissionTable set={a.folder} />
              </div>
            </div>
          )}
          <p className="text-[12px] text-ink-3">
            Read {a.folder.readAt ? ago(a.folder.readAt) : 'never'}
            {a.directory.syncedAt && <>, with the directory synced {ago(a.directory.syncedAt)}</>}. Computed, not confirmed by a Windows access check, and not evidence that anyone opened a file.
          </p>
        </div>
      )}
    </Drawer>
  );
}

export function Summary({ label, range, value }: { label: string; range?: AccessRange; value?: string }) {
  const tone = range ? openTone(range) : 'neutral';
  return (
    <div className="rounded-lg bg-surface-2 px-3 py-2">
      <div className="text-[11.5px] text-ink-3">{label}</div>
      <div className="font-semibold text-[13.5px]" style={{ color: tone === 'bad' ? 'var(--s-critical)' : tone === 'warn' ? 'var(--s-medium)' : undefined }}>
        {range ? rangeText(range) : value}
      </div>
    </div>
  );
}

export function PeopleList({ folderId, target }: { folderId: string; target: AccessTarget }) {
  const [q, setQ] = useState('');
  const [term, setTerm] = useState('');
  const [open, setOpen] = useState<string>();
  const users = useData(() => api.folderUsers(folderId, { target, search: term }), [folderId, target, term]);
  return (
    <div className="flex flex-col gap-2">
      <form
        onSubmit={(e) => {
          e.preventDefault();
          setTerm(q.trim());
        }}
      >
        <input className={inputCls} placeholder="Find a person" value={q} onChange={(e) => setQ(e.target.value)} onBlur={() => setTerm(q.trim())} aria-label="Find a person" />
      </form>
      {users.error && <Callout tone="bad">{users.error}</Callout>}
      <ul className="divide-y divide-[var(--line)] border border-line rounded-lg">
        <UserRow user={{ sid: ANY_USER, name: 'Any domain user', enabled: true, rights: 'No access', upTo: 'No access', certainty: 'Likely', via: null }} anyUser folderId={folderId} target={target} open={open === ANY_USER} onToggle={() => setOpen(open === ANY_USER ? undefined : ANY_USER)} />
        {(users.data?.items ?? []).map((u) => (
          <UserRow key={u.sid} user={u} folderId={folderId} target={target} open={open === u.sid} onToggle={() => setOpen(open === u.sid ? undefined : u.sid)} />
        ))}
        {users.data && !users.data.items.length && <li className="px-3 py-4 text-[13px] text-ink-3">{term ? `Nobody matching “${term}” has access.` : 'No user accounts have access.'}</li>}
      </ul>
      {users.data && users.data.total > users.data.items.length && <p className="text-[12px] text-ink-3">Showing {users.data.items.length} of {fmtNum(users.data.total)}. Search to find someone.</p>}
    </div>
  );
}

function UserRow({ user, folderId, target, open, onToggle, anyUser }: { user: UserAccess; folderId: string; target: AccessTarget; open: boolean; onToggle: () => void; anyUser?: boolean }) {
  return (
    <li>
      <button className="w-full text-left flex flex-wrap items-center gap-x-3 gap-y-1 px-3 py-2 hover:bg-surface-2" onClick={onToggle} aria-expanded={open}>
        {open ? <ChevronDown size={14} className="text-ink-3" /> : <ChevronRight size={14} className="text-ink-3" />}
        <span className="flex-1 min-w-[160px]">
          <span className={cx('block text-[13.5px]', anyUser && 'font-medium')}>{user.name}{!user.enabled && <span className="text-ink-3"> (disabled)</span>}</span>
          {user.via && <span className="block text-[12px] text-ink-3">via {user.via}</span>}
          {anyUser && <span className="block text-[12px] text-ink-3">Open to see what every account gets here</span>}
        </span>
        {!anyUser && user.certainty !== 'Likely' && <span title={CERTAINTY[user.certainty].help}><Tag tone="warn">{CERTAINTY[user.certainty].label}</Tag></span>}
        {!anyUser && <span className="w-36 text-right text-[13px] font-medium">{rangeText(user)}</span>}
      </button>
      {open && (
        <div className="px-4 pb-4 pt-1">
          <Explain folderId={folderId} sid={user.sid} target={target} />
        </div>
      )}
    </li>
  );
}

export function Explain({ folderId, sid, target }: { folderId: string; sid: string; target: AccessTarget }) {
  const ex = useData(() => api.explain(folderId, sid, target), [folderId, sid, target]);
  if (ex.error) return <Callout tone="bad">{ex.error}</Callout>;
  const e = ex.data;
  if (!e) return <p className="text-[13px] text-ink-3">Working it out…</p>;
  const ntfs = e.reasons.filter((r) => r.source === 'NTFS');
  const share = e.reasons.filter((r) => r.source === 'Share');
  return (
    <div className="flex flex-col gap-3 text-[13px]">
      <div className="grid grid-cols-3 gap-2">
        <Cell label="Share permission" value={e.shareRights ?? 'Not readable'} />
        <Cell label="NTFS permission" value={e.folderRights ?? 'Not readable'} />
        <Cell label="Effective" value={rangeText(e.user)} strong />
      </div>
      {!!ntfs.length && <Reasons title="NTFS entries that decided it" reasons={ntfs} />}
      {!!share.length && <Reasons title="Share entries that decided it" reasons={share} />}
      {!e.reasons.length && <p className="text-ink-2">No entry gives this account any access here.</p>}
      {e.notes.map((n) => (
        <div key={n.code + n.text} className="flex gap-2 text-ink-2">
          <CircleHelp size={15} className="shrink-0 mt-0.5 text-ink-3" />
          {n.text}
        </div>
      ))}
      <div className="text-[12px] text-ink-3">
        <span title={CERTAINTY[e.user.certainty].help}><Tag tone={e.user.certainty === 'Likely' ? 'neutral' : 'warn'}>{CERTAINTY[e.user.certainty].label}</Tag></span>{' '}
        {CERTAINTY[e.user.certainty].help}
      </div>
    </div>
  );
}

function Cell({ label, value, strong }: { label: string; value: string; strong?: boolean }) {
  return (
    <div className={cx('rounded-lg px-3 py-2', strong ? 'bg-accent-soft' : 'bg-surface-2')}>
      <div className="text-[11.5px] text-ink-3">{label}</div>
      <div className="font-semibold">{value}</div>
    </div>
  );
}

function Reasons({ title, reasons }: { title: string; reasons: { effect: string; trustee: Trustee | null; match: string; chain: string[]; rights: string; inherited: boolean; index: number }[] }) {
  return (
    <div>
      <div className="eyebrow mb-1.5">{title}</div>
      <ul className="flex flex-col gap-1.5">
        {reasons.map((r) => {
          const deny = r.effect.includes('eny');
          const chain = r.chain.length ? r.chain : r.match === 'Maybe' ? ['This account'] : [r.trustee?.name ?? 'Unknown'];
          return (
            <li key={`${r.index}-${r.effect}`} className="flex flex-wrap items-center gap-1.5">
              {chain.map((name, j) => (
                <span key={name + j} className="inline-flex items-center gap-1.5">
                  {j > 0 && <ArrowRight size={13} className="text-ink-3" aria-label="member of" />}
                  <span className={cx('rounded px-1.5 py-0.5 text-[12.5px]', j === 0 ? 'bg-surface-2' : 'bg-surface-3 font-medium')}>{name}</span>
                </span>
              ))}
              {r.match === 'Maybe' && (
                <span className="inline-flex items-center gap-1.5">
                  <ArrowRight size={13} className="text-ink-3" />
                  <span className="rounded px-1.5 py-0.5 text-[12.5px] bg-surface-3 font-medium">{r.trustee?.name}</span>
                  <Tag tone="warn" title="Membership could not be confirmed">possibly a member</Tag>
                </span>
              )}
              <span className="inline-flex items-center gap-1 ml-1 text-ink-2">
                {deny && <Ban size={13} style={{ color: 'var(--s-critical)' }} />}
                {r.effect} {r.rights}
                {r.inherited && <span className="text-ink-3">(inherited)</span>}
              </span>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

export function PermissionTable({ set, share }: { set: PermissionSet; share?: boolean }) {
  if (set.state !== 'Read') {
    return (
      <Callout tone="warn" title={set.state === 'AccessDenied' ? 'Not readable by the scan account' : 'Not read'}>
        {share
          ? 'Windows only shows share permissions to administrators of the file server. Access is shown as an upper limit from NTFS alone.'
          : 'Data Scope could not read these permissions, so it cannot say who has access here.'}
      </Callout>
    );
  }
  if (set.noList) return <Callout tone="bad">There is no permission list at all, which Windows treats as full access for everyone.</Callout>;
  return (
    <div className="overflow-x-auto border border-line rounded-lg">
      <table className="w-full text-[13px] min-w-[560px]">
        <thead>
          <tr><Th>Who</Th><Th>Access</Th>{!share && <Th>Applies to</Th>}<Th className="text-right">Users</Th></tr>
        </thead>
        <tbody>
          {set.entries.map((e) => <EntryRow key={e.index} e={e} share={share} />)}
          {!set.entries.length && <tr><Td className="text-ink-3">No entries: nobody has access.</Td></tr>}
        </tbody>
      </table>
      {set.canonical === false && <p className="px-3 py-2 text-[12px] text-ink-3 border-t border-line">The entries are in an unusual order. Windows applies them top to bottom, and so does Data Scope.</p>}
    </div>
  );
}

function EntryRow({ e, share }: { e: AceEntry; share?: boolean }) {
  const t = e.trustee;
  const deny = e.effect.includes('eny');
  return (
    <tr className="border-t border-line align-top">
      <Td>
        <div className="font-medium flex flex-wrap items-center gap-1.5">
          {t?.name ?? 'Nobody'}
          {t?.broad && <Tag tone="warn" title="Reaches almost every account">Everyone-like</Tag>}
          {t?.kind === 'Deleted' && <Tag tone="warn" title="This account no longer exists; the entry can be removed">Orphaned</Tag>}
        </div>
        <div className="text-[11.5px] text-ink-3">{t ? TRUSTEE_KIND[t.kind] : ''}{e.inherited ? ' · inherited' : ''}</div>
      </Td>
      <Td>
        <span className="inline-flex items-center gap-1">
          {deny && <Ban size={13} style={{ color: 'var(--s-critical)' }} />}
          {e.effect === 'Allow' ? '' : `${e.effect} `}{e.rights}
        </span>
      </Td>
      {!share && <Td className="text-ink-2">{e.appliesTo}</Td>}
      <Td className="text-right tnum text-ink-2">
        {e.membersCertain === null ? '—' : t?.broad ? 'All' : `${fmtNum(e.membersCertain)}${e.membersPossible ? ` (+${fmtNum(e.membersPossible)}?)` : ''}`}
      </Td>
    </tr>
  );
}

export function useOpenFolder() {
  const { go } = useNav();
  return (folderId: string) => go('access', `folder:${folderId}`);
}


/** Compact "who can open this file" for the Sensitive data drawer. */
export function WhoCanOpen({ fileId }: { fileId: string }) {
  const access = useData(() => api.fileAccess(fileId), [fileId]);
  const open = useOpenFolder();
  const a = access.data;
  if (access.error) return <Callout tone="bad">{access.error}</Callout>;
  if (!a) return null;
  const tone = openTone(a.openAccess);
  return (
    <div>
      <div className="eyebrow mb-2">Who can open this</div>
      <div className="flex flex-wrap items-center gap-2 mb-2">
        <Tag tone={tagTone(tone)}>{openText(a.openAccess)}</Tag>
        <span className="text-[13px] text-ink-2">
          {fmtNum(a.usersCertain)} {a.usersCertain === 1 ? 'person' : 'people'}
          {a.usersPossible > 0 && <>, plus {fmtNum(a.usersPossible)} possibly</>}
        </span>
      </div>
      {a.users.length > 0 && (
        <ul className="flex flex-wrap gap-1.5 mb-2">
          {a.users.slice(0, 12).map((u) => (
            <li key={u.sid} className="rounded px-2 py-0.5 text-[12.5px] bg-surface-2" title={u.via ? `via ${u.via}` : undefined}>
              {u.name} <span className="text-ink-3">{rangeText(u)}</span>
            </li>
          ))}
          {a.users.length > 12 && <li className="text-[12.5px] text-ink-3 px-1">and more</li>}
        </ul>
      )}
      <button className="text-accent hover:underline text-[13px]" onClick={() => open(a.folderId)}>See why, and every entry →</button>
      <p className="text-[12px] text-ink-3 mt-1">For files that inherit the folder’s permissions. Computed, not confirmed by Windows, and not a record of anyone opening it.</p>
    </div>
  );
}
