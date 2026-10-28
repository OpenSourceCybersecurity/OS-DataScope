import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { ArrowLeft, ArrowRight, Loader2, Users } from 'lucide-react';
import {
  api,
  type ExposureMap,
  type ExposureMapFolder,
  type ExposureMapGroup,
  type ExposureMapPerson,
  type ExposureMapShare,
  type ExposureScope,
} from '../../api/client';
import { useNav } from '../../App';
import { Button, Callout, Card, Drawer, LinkArrow, PageHeader, Segmented, Tabs, Tag, Td, Th, Unc, ago, cx, filterCls, fmtNum } from '../../ui/kit';
import { CERTAINTY, TRUSTEE_KIND, rangeText } from './access';
import {
  BREADTH,
  BREADTH_ORDER,
  STATUS,
  breadthOf,
  filesText,
  folderLabel,
  layoutMap,
  peopleText,
  uncertain,
  type MapBlock,
} from './exposure';
import { Explain, PeopleList } from './LiveAccess';
import { standardParam } from './compliance';
import { useData } from './useData';

type View = 'folders' | 'groups' | 'people' | 'shares';
type Open = { kind: 'folder'; id: string } | { kind: 'group'; sid: string } | { kind: 'person'; sid: string };

/** Stripes for anything Data Scope is not certain about, drawn over the tile colour in either theme. */
const STRIPES = 'repeating-linear-gradient(135deg, transparent 0 5px, color-mix(in oklab, var(--ink) 22%, transparent) 5px 7px)';

/**
 * Sensitive Data Exposure Map (D125): where Restricted and Confidential files are, how many people can read them,
 * through which groups, and what Data Scope could not see. Computed from the latest scans and directory sync; it is not
 * a Windows access check, and anything uncertain is drawn striped and said in words.
 */
export default function LiveExposure() {
  const header = (
    <PageHeader
      title="Exposure map"
      sub="Where Restricted and Confidential files are, who can read them and through which groups, from the latest scans and directory sync. Computed from permissions, not a Windows access check."
    />
  );
  return <FileExposure header={header} />;
}

function FileExposure({ header }: { header: React.ReactNode }) {
  const [min, setMin] = useState<'Confidential' | 'Restricted'>('Confidential');
  const [shareId, setShareId] = useState('');
  // Other screens link here with "standard:<id>" to show only the files under one compliance standard (D126).
  const { param } = useNav();
  const [standard, setStandard] = useState(() => standardParam(param));
  const scope: ExposureScope = { minSensitivity: min === 'Restricted' ? 'Restricted' : undefined, shareId: shareId || undefined, standard: standard || undefined };
  const loaded = useData(() => api.exposureMap(scope), [min, shareId, standard]);
  const allShares = useData(() => api.shares(), []);
  const compliance = useData(() => api.complianceStandards(), []);
  const turnedOn = (compliance.data?.standards ?? []).filter((x) => x.enabled);
  const standardName = turnedOn.find((x) => x.id === standard)?.name;
  const [view, setView] = useState<View>('folders');
  const [stack, setStack] = useState<Open[]>([]);
  const open = (o: Open) => setStack((s) => [...s, o]);
  const m = loaded.data;

  return (
    <>
      {header}
      <div className="flex flex-wrap items-center gap-3 mb-4">
        <Segmented
          value={min}
          onChange={setMin}
          options={[
            { id: 'Confidential', label: 'Restricted and Confidential' },
            { id: 'Restricted', label: 'Restricted only' },
          ]}
        />
        <select className={filterCls} value={shareId} onChange={(e) => setShareId(e.target.value)} aria-label="Share">
          <option value="">Every share</option>
          {(allShares.data?.items ?? []).filter((s) => s.kind === 'Disk' && (s.source ?? 'FileShare') === 'FileShare' && s.state !== 'Removed').map((s) => (
            <option key={s.id} value={s.id}>{s.uncPath}</option>
          ))}
        </select>
        {(turnedOn.length > 0 || standard) && (
          <select className={filterCls} value={standard} onChange={(e) => setStandard(e.target.value)} aria-label="Compliance standard">
            <option value="">Any standard</option>
            {turnedOn.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
          </select>
        )}
      </div>
      {loaded.error && <Callout tone="bad" title="Can't load the exposure map">{loaded.error}</Callout>}
      {!m && !loaded.error && <p className="text-[13px] text-ink-3 flex items-center gap-2"><Loader2 size={14} className="animate-spin" /> Working out who can read what…</p>}
      {m && (
        <div className="space-y-4">
          <Tiles m={m} />
          <Limits m={m} />
          {m.summary.sensitiveFolders === 0 ? (
            <Card>
              <p className="text-[13.5px] text-ink-2">
                No {min === 'Restricted' ? 'Restricted' : 'Restricted or Confidential'} files were found{standardName ? ` under ${standardName}` : ''}{shareId ? ' on this share' : ''}. That is not the same as none existing:
                check what was not read above.
              </p>
            </Card>
          ) : (
            <Card title="Where sensitive files are, and how widely they can be read">
              <MapView m={m} onOpen={(id) => open({ kind: 'folder', id })} onShare={setShareId} />
            </Card>
          )}
          <div>
            <Tabs
              value={view}
              onChange={setView}
              tabs={[
                { id: 'folders', label: `Folders (${fmtNum(m.foldersTotal)})` },
                { id: 'groups', label: `Groups (${fmtNum(m.groupsTotal)})` },
                { id: 'people', label: `People (${fmtNum(m.peopleTotal)})` },
                { id: 'shares', label: `Shares (${fmtNum(m.shares.length)})` },
              ]}
            />
            {view === 'folders' && <FoldersTable m={m} onOpen={(id) => open({ kind: 'folder', id })} onGroup={(sid) => open({ kind: 'group', sid })} />}
            {view === 'groups' && <GroupsTable m={m} onOpen={(sid) => open({ kind: 'group', sid })} />}
            {view === 'people' && <PeopleTable m={m} onOpen={(sid) => open({ kind: 'person', sid })} />}
            {view === 'shares' && <SharesTable m={m} onShare={setShareId} />}
          </div>
        </div>
      )}
      {m && (
        <DetailDrawer
          m={m}
          scope={scope}
          stack={stack}
          onBack={() => setStack((s) => s.slice(0, -1))}
          onClose={() => setStack([])}
          onOpen={open}
        />
      )}
    </>
  );
}

function Tiles({ m }: { m: ExposureMap }) {
  const s = m.summary;
  return (
    <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
      <Stat label="Folders with sensitive files" value={fmtNum(s.sensitiveFolders)} sub={filesText(s.restrictedFiles, s.confidentialFiles)} />
      <Stat
        label="Open to everyone in the domain"
        value={fmtNum(s.openFolders)}
        sub={`${fmtNum(s.openFiles)} sensitive ${s.openFiles === 1 ? 'file' : 'files'}${s.possiblyOpenFolders ? `; ${fmtNum(s.possiblyOpenFolders)} more folders possibly` : ''}`}
        tone={s.openFolders ? 'bad' : s.possiblyOpenFolders ? 'warn' : undefined}
      />
      <Stat label="People who can read sensitive files" value={fmtNum(s.people)} sub={s.peoplePossible ? `${fmtNum(s.peoplePossible)} more possibly` : 'Enabled accounts'} />
      <Stat label="Groups and entries granting it" value={fmtNum(s.groups)} sub="Each permission entry that lets people read" />
    </div>
  );
}

function Stat({ label, value, sub, tone }: { label: string; value: string; sub?: string; tone?: 'bad' | 'warn' }) {
  return (
    <div className="bg-surface border border-line rounded-xl px-4 py-3 min-w-0">
      <div className="text-[12px] text-ink-3">{label}</div>
      <div className="font-display text-[22px] font-[650] tnum mt-0.5" style={tone ? { color: tone === 'bad' ? 'var(--s-critical)' : 'var(--s-medium)' } : undefined}>{value}</div>
      {sub && <div className="text-[12px] text-ink-3">{sub}</div>}
    </div>
  );
}

/** What the map cannot see, always shown: a reader should never take a gap for a clean result. */
function Limits({ m }: { m: ExposureMap }) {
  const { go } = useNav();
  const c = m.coverage;
  const items: { text: string; link?: { label: string; go: () => void } }[] = [];
  if (c.filesNotRead)
    items.push({
      text: `${fmtNum(c.filesNotRead)} ${c.filesNotRead === 1 ? 'file' : 'files'} on scanned shares ${c.filesNotRead === 1 ? 'was' : 'were'} not read${c.filesExcluded ? ` (${fmtNum(c.filesExcluded)} excluded by an administrator)` : ''}, so any sensitive data in them is not on this map.`,
      link: { label: 'See files not read', go: () => go('discovery', 'unread') },
    });
  if (c.foldersUnknown) items.push({ text: `${fmtNum(c.foldersUnknown)} sensitive ${c.foldersUnknown === 1 ? 'folder has' : 'folders have'} permissions the scan account could not read: who can read ${c.foldersUnknown === 1 ? 'it' : 'them'} is unknown.` });
  if (c.foldersUncertain) items.push({ text: `In ${fmtNum(c.foldersUncertain)} sensitive ${c.foldersUncertain === 1 ? 'folder' : 'folders'}, some people only might be able to read: a share's permissions, a group's members or a server's local groups could not be read. These are striped.` });
  if (c.sharesPermissionsNotRead) items.push({ text: `${fmtNum(c.sharesPermissionsNotRead)} scanned ${c.sharesPermissionsNotRead === 1 ? 'share has' : 'shares have'} share permissions Data Scope could not read (it needs local administrator on the file server), so access there is a range.` });
  if (c.sharesNotFullyScanned) items.push({ text: `${fmtNum(c.sharesNotFullyScanned)} ${c.sharesNotFullyScanned === 1 ? 'share was' : 'shares were'} not fully scanned.`, link: { label: 'Open Shares', go: () => go('shares') } });
  if (c.sharesLeftOut) items.push({ text: `${fmtNum(c.sharesLeftOut)} ${c.sharesLeftOut === 1 ? 'share is' : 'shares are'} left out of scans; anything shown for ${c.sharesLeftOut === 1 ? 'it' : 'them'} is as last seen.`, link: { label: 'Scan scope', go: () => go('scan-scope') } });
  if (c.disabledAccounts) items.push({ text: `${fmtNum(c.disabledAccounts)} disabled ${c.disabledAccounts === 1 ? 'account has' : 'accounts have'} permissions to read sensitive files. ${c.disabledAccounts === 1 ? 'It is' : 'They are'} not counted as people, since disabled accounts cannot sign in.`, link: { label: 'See Risks', go: () => go('risks') } });
  if (c.truncated) items.push({ text: `There are more than ${fmtNum(c.folderLimit)} folders with sensitive files; the ones with the most Restricted files are shown.` });
  const synced = m.directory.syncedAt ? `Group membership is from the directory sync ${ago(m.directory.syncedAt)}.` : 'Active Directory has not been synced, so access through groups is missing.';
  return (
    <Callout tone={items.length || !m.directory.syncedAt ? 'warn' : 'info'} title={items.length ? 'What this map cannot see' : 'How far to trust this map'}>
      <ul className="list-disc pl-5 space-y-1">
        {items.map((i) => (
          <li key={i.text}>
            {i.text}{' '}
            {i.link && <button type="button" className="text-accent hover:underline whitespace-nowrap" onClick={i.link.go}>{i.link.label}</button>}
          </li>
        ))}
        <li>{synced} Access is computed from the permissions the scans read, the way Windows evaluates them; it is not proof anyone opened a file.</li>
      </ul>
    </Callout>
  );
}

// ---------- The map ----------

function MapView({ m, onOpen, onShare }: { m: ExposureMap; onOpen: (folderId: string) => void; onShare: (shareId: string) => void }) {
  const box = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);
  useLayoutEffect(() => {
    const el = box.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setWidth(el.clientWidth));
    ro.observe(el);
    setWidth(el.clientWidth);
    return () => ro.disconnect();
  }, []);
  const height = width < 640 ? 380 : 440;
  const blocks = width ? layoutMap(m.shares, m.folders, width, height) : [];
  const present = new Set(m.folders.map(breadthOf));

  return (
    <div>
      <p className="text-[12.5px] text-ink-2 mb-3 max-w-3xl">
        Each box is a share and each tile a folder, sized by its sensitive files and coloured by how many people can read them. Striped tiles are not certain. Select a tile for who can read it and why.
      </p>
      <div ref={box} className="relative w-full rounded-lg overflow-hidden bg-surface-2" style={{ height }} role="group" aria-label="Exposure map">
        {blocks.map((b) => <Block key={b.share.shareId} b={b} onOpen={onOpen} onShare={onShare} />)}
      </div>
      <ul className="mt-3 flex flex-wrap gap-x-4 gap-y-1.5 text-[12px] text-ink-2" aria-label="Legend">
        {BREADTH_ORDER.filter((k) => present.has(k)).map((k) => (
          <li key={k} className="inline-flex items-center gap-1.5">
            <span className="size-3 rounded-sm" style={{ background: fill(k), boxShadow: `inset 0 2px 0 ${BREADTH[k].color}`, backgroundImage: k === 'unknown' ? STRIPES : undefined }} />
            {BREADTH[k].label}
          </li>
        ))}
        <li className="inline-flex items-center gap-1.5">
          <span className="size-3 rounded-sm border border-line" style={{ backgroundImage: STRIPES }} />
          Not certain
        </li>
      </ul>
    </div>
  );
}

const fill = (k: keyof typeof BREADTH) => `color-mix(in oklab, ${BREADTH[k].color} 38%, var(--surface))`;

function Block({ b, onOpen, onShare }: { b: MapBlock; onOpen: (id: string) => void; onShare: (id: string) => void }) {
  const r = b.rect;
  return (
    <>
      {b.header && (
        <button
          type="button"
          onClick={() => onShare(b.share.shareId)}
          className="absolute text-left px-1.5 text-[12px] font-medium text-ink truncate hover:underline"
          style={{ left: r.x + 2, top: r.y + 3, width: Math.max(0, r.w - 4), height: 18 }}
          title={`${b.share.uncPath}: ${filesText(b.share.restricted, b.share.confidential)}. Show only this share.`}
        >
          {b.share.uncPath}
        </button>
      )}
      {b.tiles.map((t) => {
        const { x, y, w, h } = t.rect;
        if (w < 1 || h < 1) return null;
        const label = w > 74 && h > 30;
        if (t.more) {
          const g = t.more;
          const said = g.breadth
            ? `${fmtNum(g.folders)} other ${g.folders === 1 ? 'folder' : 'folders'} on ${b.share.uncPath} with ${fmtNum(g.files)} sensitive files, readable by ${BREADTH[g.breadth].label.toLowerCase()}${g.uncertain ? ', not certain' : ''}. Select to map only this share.`
            : `${fmtNum(g.folders)} more ${g.folders === 1 ? 'folder' : 'folders'} on ${b.share.uncPath} with ${fmtNum(g.files)} sensitive files, past the folders listed here. Select to map only this share.`;
          return (
            <button
              key={t.key}
              type="button"
              onClick={() => onShare(b.share.shareId)}
              aria-label={said}
              title={said}
              className="absolute rounded-[3px] text-left text-ink-2 text-[11.5px] p-1.5 overflow-hidden hover:outline hover:outline-2 hover:outline-[var(--ink)]"
              style={{
                left: x, top: y, width: w, height: h,
                background: g.breadth ? fill(g.breadth) : 'var(--surface-3)',
                backgroundImage: g.uncertain ? STRIPES : undefined,
                boxShadow: g.breadth ? `inset 0 3px 0 ${BREADTH[g.breadth].color}` : undefined,
              }}
            >
              {label && (
                <>
                  <span className="block font-medium text-ink leading-tight truncate">{fmtNum(g.folders)} other {g.folders === 1 ? 'folder' : 'folders'}</span>
                  {h > 46 && <span className="block leading-tight tnum truncate">{fmtNum(g.files)} files{g.breadth ? '' : ' · not listed'}</span>}
                </>
              )}
            </button>
          );
        }
        const f = t.folder!;
        const k = breadthOf(f);
        const unsure = uncertain(f);
        const name = folderLabel(f);
        const said = `${f.uncPath}: ${filesText(f.restricted, f.confidential)}. ${STATUS[f.status].label}. ${peopleText(f.people, f.peoplePossible)}.`;
        return (
          <button
            key={t.key}
            type="button"
            onClick={() => onOpen(f.folderId)}
            aria-label={said}
            title={said}
            className="absolute rounded-[3px] text-left text-ink p-1.5 overflow-hidden hover:outline hover:outline-2 hover:outline-[var(--ink)] focus-visible:outline focus-visible:outline-2 focus-visible:outline-[var(--accent)]"
            style={{ left: x, top: y, width: w, height: h, background: fill(k), backgroundImage: unsure ? STRIPES : undefined, boxShadow: `inset 0 3px 0 ${BREADTH[k].color}` }}
          >
            {label && (
              <>
                <span className="block text-[12px] font-medium leading-tight truncate">{name}</span>
                {h > 46 && <span className="block text-[11.5px] leading-tight text-ink-2 tnum truncate">{fmtNum(f.restricted + f.confidential)} files · {f.status === 'Open' ? 'everyone' : f.status === 'Unknown' ? 'unknown' : peopleShort(f.people, f.peoplePossible)}</span>}
              </>
            )}
          </button>
        );
      })}
    </>
  );
}

const peopleShort = (people: number, possible: number) => (people === 0 && possible ? `up to ${fmtNum(possible)}` : `${fmtNum(people)}${possible ? '+' : ''}`) + ' people';

// ---------- Lists ----------

function StatusTag({ f }: { f: Pick<ExposureMapFolder, 'status'> }) {
  return <Tag tone={STATUS[f.status].tone === 'good' ? 'neutral' : STATUS[f.status].tone}>{STATUS[f.status].label}</Tag>;
}

/** A bar beside a number, so the list reads like the map. */
function Bar({ value, max, color }: { value: number; max: number; color: string }) {
  return (
    <span className="inline-block h-1.5 w-20 rounded-full bg-surface-3 align-middle overflow-hidden" aria-hidden>
      <span className="block h-full rounded-full" style={{ width: `${max ? Math.max(4, (100 * value) / max) : 0}%`, background: color }} />
    </span>
  );
}

function FoldersTable({ m, onOpen, onGroup }: { m: ExposureMap; onOpen: (id: string) => void; onGroup: (sid: string) => void }) {
  const max = Math.max(1, ...m.folders.map((f) => f.people + f.peoplePossible));
  return (
    <div className="mt-3">
      <p className="text-[12.5px] text-ink-3 mb-2">Most exposed first: open to everyone, then by how many people can read.{m.foldersTotal > m.folders.length && ` Showing ${fmtNum(m.folders.length)} of ${fmtNum(m.foldersTotal)}.`}</p>
      <div className="bg-surface border border-line rounded-xl overflow-x-auto">
        <table className="w-full text-[13px] min-w-[860px]">
          <thead><tr><Th>Folder</Th><Th>Sensitive files</Th><Th>Who can read</Th><Th>Granted through</Th></tr></thead>
          <tbody>
            {m.folders.map((f) => (
              <tr key={f.folderId} className="border-t border-line align-top hover:bg-surface-2 cursor-pointer" onClick={() => onOpen(f.folderId)}>
                <Td className="max-w-[300px]"><Unc>{f.uncPath}</Unc></Td>
                <Td className="whitespace-nowrap tnum">{filesText(f.restricted, f.confidential)}</Td>
                <Td>
                  <div className="flex flex-wrap items-center gap-2"><StatusTag f={f} /></div>
                  <div className="mt-1 flex items-center gap-2 text-ink-2 tnum">
                    <Bar value={f.people + f.peoplePossible} max={max} color={BREADTH[breadthOf(f)].color} />
                    {f.status === 'Unknown' ? 'Permissions not read' : peopleText(f.people, f.peoplePossible)}
                  </div>
                </Td>
                <Td>
                  <div className="flex flex-wrap gap-1.5" onClick={(e) => e.stopPropagation()}>
                    {f.grants.slice(0, 3).map((g) => (
                      <button key={g.trustee.sid} type="button" onClick={() => onGroup(g.trustee.sid)} className="rounded px-1.5 py-0.5 bg-surface-2 hover:bg-surface-3 text-[12.5px]">
                        {g.trustee.name} <span className="text-ink-3 tnum">{fmtNum(g.people + g.peoplePossible)}</span>
                      </button>
                    ))}
                    {f.grants.length > 3 && <span className="text-[12px] text-ink-3 self-center">+{f.grants.length - 3} more</span>}
                    {!f.grants.length && <span className="text-ink-3">{f.status === 'Unknown' ? 'Unknown' : '—'}</span>}
                  </div>
                </Td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function GroupsTable({ m, onOpen }: { m: ExposureMap; onOpen: (sid: string) => void }) {
  const max = Math.max(1, ...m.groups.map((g) => g.people + g.peoplePossible));
  return (
    <div className="mt-3">
      <p className="text-[12.5px] text-ink-3 mb-2">
        The groups and accounts named in permission entries that let people read sensitive files, the ones reaching the most people first. A person reached by several groups counts under each.
        {m.groupsTotal > m.groups.length && ` Showing ${fmtNum(m.groups.length)} of ${fmtNum(m.groupsTotal)}.`}
      </p>
      <div className="bg-surface border border-line rounded-xl overflow-x-auto">
        <table className="w-full text-[13px] min-w-[820px]">
          <thead><tr><Th>Group or account</Th><Th>People it lets read</Th><Th className="text-right">Folders</Th><Th>Sensitive files</Th><Th>Most it grants</Th></tr></thead>
          <tbody>
            {m.groups.map((g) => (
              <tr key={g.trustee.sid} className="border-t border-line align-top hover:bg-surface-2 cursor-pointer" onClick={() => onOpen(g.trustee.sid)}>
                <Td>
                  <div className="font-medium flex flex-wrap items-center gap-1.5">{g.trustee.name}{g.trustee.broad && <Tag tone="bad">Everyone-style</Tag>}{g.trustee.administrative && <Tag>Administrators</Tag>}</div>
                  <div className="text-[12px] text-ink-3">{TRUSTEE_KIND[g.trustee.kind]}{g.membersCertain != null && ` · ${fmtNum(g.membersCertain)} members${g.membersPossible ? ` + ${fmtNum(g.membersPossible)} possibly` : ''}`}</div>
                </Td>
                <Td>
                  <div className="flex items-center gap-2 tnum text-ink-2">
                    <Bar value={g.people + g.peoplePossible} max={max} color="var(--series-1)" />
                    {peopleText(g.people, g.peoplePossible)}
                  </div>
                </Td>
                <Td className="text-right tnum">{fmtNum(g.folders)}</Td>
                <Td className="tnum whitespace-nowrap">{filesText(g.restricted, g.confidential)}</Td>
                <Td>{g.highestRights}</Td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function PeopleTable({ m, onOpen }: { m: ExposureMap; onOpen: (sid: string) => void }) {
  const max = Math.max(1, ...m.people.map((p) => p.restricted + p.confidential));
  return (
    <div className="mt-3">
      <p className="text-[12.5px] text-ink-3 mb-2">
        Enabled accounts by the Restricted, then Confidential, files they can read. Files they only might read are counted apart.
        {m.peopleTotal > m.people.length && ` Showing ${fmtNum(m.people.length)} of ${fmtNum(m.peopleTotal)}.`}
      </p>
      <div className="bg-surface border border-line rounded-xl overflow-x-auto">
        <table className="w-full text-[13px] min-w-[820px]">
          <thead><tr><Th>Person</Th><Th>Can read</Th><Th className="text-right">Possibly</Th><Th className="text-right">Folders</Th><Th>Mostly through</Th></tr></thead>
          <tbody>
            {m.people.map((p) => (
              <tr key={p.sid} className="border-t border-line align-top hover:bg-surface-2 cursor-pointer" onClick={() => onOpen(p.sid)}>
                <Td className="font-medium">{p.name}</Td>
                <Td>
                  <div className="flex items-center gap-2 tnum">
                    <Bar value={p.restricted + p.confidential} max={max} color="var(--c-restricted)" />
                    {filesText(p.restricted, p.confidential)}
                  </div>
                </Td>
                <Td className="text-right tnum text-ink-2">{p.possibleFiles ? fmtNum(p.possibleFiles) : '—'}</Td>
                <Td className="text-right tnum">{fmtNum(p.folders)}</Td>
                <Td className="text-ink-2">{p.via.join(', ') || '—'}</Td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function SharesTable({ m, onShare }: { m: ExposureMap; onShare: (id: string) => void }) {
  const { go } = useNav();
  return (
    <div className="mt-3 bg-surface border border-line rounded-xl overflow-x-auto">
      <table className="w-full text-[13px] min-w-[900px]">
        <thead><tr><Th>Share</Th><Th>Sensitive files</Th><Th>Folders open to everyone</Th><Th>People</Th><Th>What limits this</Th><Th /></tr></thead>
        <tbody>
          {m.shares.map((s) => (
            <tr key={s.shareId} className="border-t border-line align-top">
              <Td><div className="font-medium">{s.name}</div><Unc>{s.uncPath}</Unc></Td>
              <Td className="tnum">{filesText(s.restricted, s.confidential)}<div className="text-[12px] text-ink-3">{fmtNum(s.sensitiveFolders)} {s.sensitiveFolders === 1 ? 'folder' : 'folders'}</div></Td>
              <Td className="tnum">
                {s.openFolders ? <Tag tone="bad">{fmtNum(s.openFolders)}</Tag> : '0'}
                {s.possiblyOpenFolders > 0 && <span className="ml-1.5"><Tag tone="warn">{fmtNum(s.possiblyOpenFolders)} possibly</Tag></span>}
              </Td>
              <Td className="tnum">{s.sensitiveFolders ? peopleText(s.people, s.peoplePossible) : '—'}</Td>
              <Td className="text-[12.5px] text-ink-2"><ShareLimits s={s} /></Td>
              <Td className="whitespace-nowrap text-right">
                {s.sensitiveFolders > 0 && <Button size="sm" variant="ghost" onClick={() => onShare(s.shareId)}>Map it</Button>}
                <Button size="sm" variant="ghost" onClick={() => go('shares', s.shareId)}>Open</Button>
              </Td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function ShareLimits({ s }: { s: ExposureMapShare }) {
  const parts = [
    !s.included && 'Left out of scans; as last seen',
    s.included && s.state === 'Partial' && 'Partly scanned',
    s.included && s.state === 'Unavailable' && 'Could not be opened',
    s.included && s.state === 'NotScanned' && 'Not scanned yet',
    s.sharePermissions !== 'Read' && 'Share permissions not read',
    s.unknownFolders > 0 && `${fmtNum(s.unknownFolders)} folders' permissions not read`,
    s.filesNotRead > 0 && `${fmtNum(s.filesNotRead)} files not read`,
  ].filter(Boolean) as string[];
  return parts.length ? <>{parts.join('. ')}.</> : <span className="text-ink-3">Nothing</span>;
}

// ---------- Drill-downs ----------

function DetailDrawer({ m, scope, stack, onBack, onClose, onOpen }: {
  m: ExposureMap;
  scope: ExposureScope;
  stack: Open[];
  onBack: () => void;
  onClose: () => void;
  onOpen: (o: Open) => void;
}) {
  const top = stack.at(-1);
  const folder = top?.kind === 'folder' ? m.folders.find((f) => f.folderId === top.id) : undefined;
  const group = top?.kind === 'group' ? m.groups.find((g) => g.trustee.sid === top.sid) : undefined;
  const person = top?.kind === 'person' ? m.people.find((p) => p.sid === top.sid) : undefined;
  const title = !top ? '' : top.kind === 'folder' ? (folder ? folderLabel(folder) : 'Folder') : top.kind === 'group' ? group?.trustee.name ?? 'Group' : person?.name ?? 'Person';
  const sub = top?.kind === 'folder' ? <span className="font-mono text-[12.5px] break-all">{folder?.uncPath}</span> : top?.kind === 'group' ? 'What this group or account lets people read' : top ? 'What this person can read, and why' : undefined;
  return (
    <Drawer open={!!top} onClose={onClose} title={title} sub={sub} width={720}>
      {stack.length > 1 && (
        <button type="button" onClick={onBack} className="mb-3 inline-flex items-center gap-1 text-[13px] text-accent hover:underline">
          <ArrowLeft size={14} /> Back
        </button>
      )}
      {top?.kind === 'folder' && <FolderDetail key={top.id} id={top.id} folder={folder} onOpen={onOpen} />}
      {top?.kind === 'group' && <GroupDetail key={top.sid} sid={top.sid} scope={scope} onOpen={onOpen} />}
      {top?.kind === 'person' && <PersonDetail key={top.sid} sid={top.sid} scope={scope} onOpen={onOpen} />}
    </Drawer>
  );
}

function FolderDetail({ id, folder: f, onOpen }: { id: string; folder?: ExposureMapFolder; onOpen: (o: Open) => void }) {
  const { go } = useNav();
  const [who, setWho] = useState(false);
  const links = (
    <div className="flex flex-wrap gap-2">
      {f && <Button size="sm" onClick={() => go('discovery', `share:${f.shareId}`)}>Sensitive files on this share <ArrowRight size={13} /></Button>}
      <Button size="sm" onClick={() => go('access', `folder:${id}`)}>Who has access <ArrowRight size={13} /></Button>
      {f && <Button size="sm" onClick={() => go('risks', `share:${f.shareId}`)}>Risks on this share <ArrowRight size={13} /></Button>}
      {f && <Button size="sm" variant="ghost" onClick={() => go('shares', f.shareId)}>Open the share</Button>}
    </div>
  );
  if (!f) return <div className="space-y-3 text-[13px]"><p className="text-ink-2">This folder is outside the busiest folders listed here. Open it in Who has access for the full picture.</p>{links}</div>;
  return (
    <div className="flex flex-col gap-4 text-[13px]">
      <div className="grid sm:grid-cols-3 gap-2">
        <Box label="Sensitive files here" value={filesText(f.restricted, f.confidential)} />
        <Box label="Anyone in the domain" value={f.status === 'Unknown' ? 'Unknown' : rangeText(f.anyDomainUser)} tone={STATUS[f.status].tone} />
        <Box label="People who can read" value={f.status === 'Unknown' ? 'Unknown' : peopleText(f.people, f.peoplePossible)} />
      </div>
      {f.anyDomainUser.notes.length > 0 && (
        <Callout tone={f.anyDomainUser.certainty === 'Likely' ? 'info' : 'warn'} title={f.anyDomainUser.certainty === 'Likely' ? 'Worth knowing' : 'Why this is not certain'}>
          <ul className="list-disc pl-5 space-y-0.5">{f.anyDomainUser.notes.map((n) => <li key={n.code + n.text}>{n.text}</li>)}</ul>
        </Callout>
      )}
      <section>
        <h3 className="eyebrow mb-2">How access is granted</h3>
        {f.status === 'Unknown' ? (
          <p className="text-ink-2">The scan account could not read this folder's permissions, so Data Scope cannot say who can read it. Give the scan account read permissions on it and scan again.</p>
        ) : f.grants.length === 0 ? (
          <p className="text-ink-2">No permission entry lets an enabled account in the directory read files here.</p>
        ) : (
          <ul className="divide-y divide-line border-y border-line">
            {f.grants.map((g) => (
              <li key={g.trustee.sid}>
                <button type="button" onClick={() => onOpen({ kind: 'group', sid: g.trustee.sid })} className="w-full flex flex-wrap items-center gap-2 py-2 text-left hover:bg-surface-2 px-1">
                  <Users size={15} className="text-ink-3" />
                  <span className="font-medium">{g.trustee.name}</span>
                  {g.trustee.broad && <Tag tone="bad">Everyone-style</Tag>}
                  <span className="text-ink-3">{TRUSTEE_KIND[g.trustee.kind]}, {g.rights}</span>
                  <span className="ml-auto tnum text-ink-2">{peopleText(g.people, g.peoplePossible)}</span>
                </button>
              </li>
            ))}
          </ul>
        )}
        {f.status !== 'Unknown' && <p className="mt-1.5 text-[12px] text-ink-3">Every entry that lets people read is listed, even when another entry already covers them; removing one alone may not stop access. The share's own permissions can narrow this further.</p>}
      </section>
      {f.status !== 'Unknown' && (
        <section>
          <button type="button" className="eyebrow text-accent hover:underline" onClick={() => setWho((x) => !x)}>{who ? 'Hide' : 'Show'} everyone who can read, and why</button>
          {who && <div className="mt-2"><PeopleList folderId={id} target="Files" /></div>}
        </section>
      )}
      {links}
    </div>
  );
}

function Box({ label, value, tone }: { label: string; value: string; tone?: 'bad' | 'warn' | 'neutral' | 'good' }) {
  return (
    <div className="rounded-lg bg-surface-2 px-3 py-2 min-w-0">
      <div className="text-[11.5px] text-ink-3">{label}</div>
      <div className="font-semibold" style={{ color: tone === 'bad' ? 'var(--s-critical)' : tone === 'warn' ? 'var(--s-medium)' : undefined }}>{value}</div>
    </div>
  );
}

function GroupDetail({ sid, scope, onOpen }: { sid: string; scope: ExposureScope; onOpen: (o: Open) => void }) {
  const d = useData(() => api.groupExposure(sid, scope), [sid, scope.shareId, scope.minSensitivity]);
  const { go } = useNav();
  if (d.error) return <Callout tone="bad">{d.error}</Callout>;
  const g = d.data;
  if (!g) return <p className="text-[13px] text-ink-3">Loading…</p>;
  const t: ExposureMapGroup | null = g.totals;
  return (
    <div className="flex flex-col gap-4 text-[13px]">
      <div className="grid sm:grid-cols-3 gap-2">
        <Box label="Kind" value={`${TRUSTEE_KIND[g.trustee.kind]}${g.trustee.broad ? ', reaches almost everyone' : ''}`} tone={g.trustee.broad ? 'bad' : undefined} />
        <Box label="People it lets read sensitive files" value={t ? peopleText(t.people, t.peoplePossible) : 'No one'} />
        <Box label="Members in the directory" value={t?.membersCertain != null ? `${fmtNum(t.membersCertain)}${t.membersPossible ? ` + ${fmtNum(t.membersPossible)} possibly` : ''}` : '—'} />
      </div>
      {g.trustee.administrative && <Callout>Administrative access like this is expected on most folders. It still counts, because these accounts can read the files.</Callout>}
      <section>
        <h3 className="eyebrow mb-2">Sensitive folders it opens ({fmtNum(g.foldersTotal)})</h3>
        {g.folders.length === 0 ? (
          <p className="text-ink-2">None: no entry naming it lets anyone read sensitive files{scope.shareId ? ' on this share' : ''}.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-[13px] min-w-[560px]">
              <thead><tr><Th>Folder</Th><Th>Sensitive files</Th><Th>Grants</Th><Th>People through it</Th></tr></thead>
              <tbody>
                {g.folders.map((f) => (
                  <tr key={f.folderId} className="border-t border-line align-top hover:bg-surface-2 cursor-pointer" onClick={() => onOpen({ kind: 'folder', id: f.folderId })}>
                    <Td className="max-w-[260px]"><Unc>{f.uncPath}</Unc><div className="mt-0.5"><StatusTag f={f} /></div></Td>
                    <Td className="tnum whitespace-nowrap">{filesText(f.restricted, f.confidential)}</Td>
                    <Td>{f.rights}</Td>
                    <Td className="tnum">{peopleText(f.people, f.peoplePossible)}</Td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
      {g.trustee.kind === 'User' && <LinkArrow onClick={() => go('access', `user:${sid}`)}>Everything this account can open, in Who has access</LinkArrow>}
    </div>
  );
}

function PersonDetail({ sid, scope, onOpen }: { sid: string; scope: ExposureScope; onOpen: (o: Open) => void }) {
  const d = useData(() => api.personExposure(sid, scope), [sid, scope.shareId, scope.minSensitivity]);
  const { go } = useNav();
  const [why, setWhy] = useState<string>();
  useEffect(() => setWhy(undefined), [sid]);
  if (d.error) return <Callout tone="bad">{d.error}</Callout>;
  const p = d.data;
  if (!p) return <p className="text-[13px] text-ink-3">Loading…</p>;
  const t: ExposureMapPerson | null = p.totals;
  return (
    <div className="flex flex-col gap-4 text-[13px]">
      <div className="grid sm:grid-cols-3 gap-2">
        <Box label="Can read" value={t ? filesText(t.restricted, t.confidential) : 'None'} />
        <Box label="Might also read" value={t?.possibleFiles ? `${fmtNum(t.possibleFiles)} files` : 'None'} />
        <Box label="Sensitive folders" value={fmtNum(p.foldersTotal)} />
      </div>
      {!p.account.enabled && <Callout tone="warn">This account is disabled, so it cannot sign in. Its permissions are not counted here; see Risks for accounts like this.</Callout>}
      <section>
        <h3 className="eyebrow mb-2">Where and why</h3>
        {p.folders.length === 0 ? (
          <p className="text-ink-2">This account cannot read any sensitive files on the map.</p>
        ) : (
          <ul className="divide-y divide-line border-y border-line">
            {p.folders.map((f) => (
              <li key={f.folderId} className="py-2">
                <div className="flex flex-wrap items-center gap-2">
                  <button type="button" className="text-left hover:underline min-w-0" onClick={() => onOpen({ kind: 'folder', id: f.folderId })}><Unc>{f.uncPath}</Unc></button>
                  <span className="tnum text-ink-2">{filesText(f.restricted, f.confidential)}</span>
                  {f.certain ? <Tag>{f.rights}</Tag> : <Tag tone="warn" title={CERTAINTY.Partial.help}>Possibly {f.upTo.toLowerCase()}</Tag>}
                </div>
                <div className="mt-1 flex flex-wrap items-center gap-1.5 text-[12.5px] text-ink-2">
                  Through
                  {f.via.length ? f.via.map((v) => (
                    <button key={v.trustee.sid} type="button" onClick={() => onOpen({ kind: 'group', sid: v.trustee.sid })} className={cx('rounded px-1.5 py-0.5 hover:bg-surface-3', v.certain ? 'bg-surface-2' : 'bg-[var(--s-medium-soft)]')} title={v.certain ? undefined : 'Membership could not be confirmed'}>
                      {v.trustee.sid === sid ? 'a direct entry' : v.trustee.name}{!v.certain && ' (possibly)'}
                    </button>
                  )) : <span>an entry Data Scope could not tie to a group</span>}
                  <button type="button" className="ml-1 text-accent hover:underline" onClick={() => setWhy(why === f.folderId ? undefined : f.folderId)}>{why === f.folderId ? 'Hide why' : 'Why?'}</button>
                </div>
                {why === f.folderId && <div className="mt-2 rounded-lg border border-line p-3"><Explain folderId={f.folderId} sid={sid} target="Files" /></div>}
              </li>
            ))}
          </ul>
        )}
      </section>
      <LinkArrow onClick={() => go('access', `user:${sid}`)}>Everything this person can open, in Who has access</LinkArrow>
    </div>
  );
}
