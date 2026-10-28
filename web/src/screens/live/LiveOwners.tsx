import { useState } from 'react';
import { UserCheck } from 'lucide-react';
import { api, type FolderOwner, type OwnerAssignment, type OwnerStatus } from '../../api/client';
import { canManageReviews, useBackend } from '../../api/backend';
import { Button, Callout, Card, Drawer, Empty, PageHeader, Tabs, Tag, Td, Th, Unc, cx, fmtBytes, fmtDate, fmtNum, inputCls } from '../../ui/kit';
import { OwnerPanel } from './OwnerPanel';
import { useData } from './useData';

const STATUS: Record<OwnerStatus, { label: string; tone: 'good' | 'bad' | 'warn' | 'neutral' }> = {
  Active: { label: 'Active', tone: 'good' },
  Disabled: { label: 'Account disabled', tone: 'bad' },
  Missing: { label: 'Not in directory', tone: 'bad' },
  Unknown: { label: 'Not checked', tone: 'neutral' },
};

type Target = { folderId: string; resource: string };

/**
 * Who answers for each share. Owners are people from Active Directory, confirmed by an administrator; Data Scope only
 * suggests. Changing an owner keeps the history.
 */
export default function LiveOwners() {
  const backend = useBackend();
  const admin = canManageReviews(backend);
  const [tab, setTab] = useState<'owned' | 'unowned' | 'attention'>('owned');
  const [search, setSearch] = useState('');
  const summary = useData(() => api.ownerSummary(), []);
  const owners = useData(() => api.owners({ search }), [search]);
  const orphaned = useData(() => api.owners({ orphaned: true }), []);
  const unowned = useData(() => api.unowned(), []);
  const [target, setTarget] = useState<Target>();

  const reload = async () => {
    await Promise.all([summary.reload(), owners.reload(), orphaned.reload(), unowned.reload()]);
  };
  const s = summary.data;
  const error = summary.error ?? owners.error ?? unowned.error;

  return (
    <>
      <PageHeader title="Owners" sub="The person who answers for each share: they review who can open it. Owners are confirmed by an administrator; Data Scope only suggests." />
      {error && <div className="mb-4"><Callout tone="bad" title="Can't reach the Data Scope server">{error}</Callout></div>}
      {s && (
        <div className="grid grid-cols-2 md:grid-cols-5 gap-3 mb-4">
          <Stat label="Owned" value={s.owned} />
          <Stat label="Shares without an owner" value={s.unowned} warn={s.unowned > 0} />
          <Stat label="Owner left or disabled" value={s.orphanedOwners} warn={s.orphanedOwners > 0} />
          <Stat label="No backup owner" value={s.missingBackup} />
          <Stat label="Review due" value={s.reviewsDue} />
        </div>
      )}
      <Tabs
        value={tab}
        onChange={setTab}
        tabs={[
          { id: 'owned', label: `Owned (${s?.owned ?? '…'})` },
          { id: 'unowned', label: `Without an owner (${s?.unowned ?? '…'})` },
          { id: 'attention', label: `Needs attention (${s?.orphanedOwners ?? '…'})` },
        ]}
      />
      {tab === 'owned' && (
        <>
          <input className={cx(inputCls, 'mb-3 max-w-[360px]')} placeholder="Search owner, share, folder or department" value={search} onChange={(e) => setSearch(e.target.value)} />
          <OwnerTable rows={owners.data?.items} onOpen={(o) => setTarget({ folderId: o.folderId, resource: o.resource })} empty="No shares or folders have an owner yet. Start with the shares without one." />
        </>
      )}
      {tab === 'attention' && (
        <OwnerTable rows={orphaned.data?.items} onOpen={(o) => setTarget({ folderId: o.folderId, resource: o.resource })} empty="Every owner is an active account in the latest directory sync." />
      )}
      {tab === 'unowned' && (
        <Card pad={false}>
          {unowned.data && unowned.data.items.length === 0 ? (
            <Empty icon={<UserCheck size={20} />} title="Every scanned share has an owner" />
          ) : (
            <div className="overflow-x-auto">
              <table className="w-full text-[13.5px] min-w-[640px]">
                <thead><tr><Th>Share</Th><Th className="text-right">Files</Th><Th className="text-right">Size</Th><Th>Sensitive data</Th><Th /></tr></thead>
                <tbody>
                  {unowned.data?.items.map((u) => (
                    <tr key={u.folderId} className="border-t border-line">
                      <Td><Unc>{u.resource}</Unc></Td>
                      <Td className="text-right tnum">{fmtNum(u.fileCount)}</Td>
                      <Td className="text-right tnum">{fmtBytes(u.totalBytes)}</Td>
                      <Td>{u.mostSensitive ? <Tag tone={u.mostSensitive === 'Restricted' ? 'bad' : 'warn'}>{u.mostSensitive}</Tag> : <span className="text-ink-3">None found</span>}</Td>
                      <Td className="text-right">{admin ? <Button size="sm" onClick={() => setTarget({ folderId: u.folderId, resource: u.resource })}>Assign owner</Button> : <Button size="sm" variant="ghost" onClick={() => setTarget({ folderId: u.folderId, resource: u.resource })}>Details</Button>}</Td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </Card>
      )}
      {target && <OwnerDrawer target={target} admin={admin} onClose={() => setTarget(undefined)} onChanged={reload} />}
    </>
  );
}

function OwnerTable({ rows, onOpen, empty }: { rows?: OwnerAssignment[]; onOpen: (o: OwnerAssignment) => void; empty: string }) {
  if (rows && rows.length === 0) return <Card><Empty icon={<UserCheck size={20} />} title={empty} /></Card>;
  return (
    <Card pad={false}>
      <div className="overflow-x-auto">
        <table className="w-full text-[13.5px] min-w-[860px]">
          <thead><tr><Th>Share or folder</Th><Th>Owner</Th><Th>Backup</Th><Th>Reviewed</Th><Th>Next review</Th></tr></thead>
          <tbody>
            {rows?.map((o) => (
              <tr key={o.id} className="border-t border-line cursor-pointer hover:bg-surface-2" onClick={() => onOpen(o)}>
                <Td>
                  <Unc>{o.resource}</Unc>
                  {o.purpose && <div className="text-[12px] text-ink-3">{o.purpose}</div>}
                </Td>
                <Td>
                  <div>{o.ownerName}</div>
                  {o.ownerStatus !== 'Active' && <Tag tone={STATUS[o.ownerStatus].tone}>{STATUS[o.ownerStatus].label}</Tag>}
                  {o.department && <div className="text-[12px] text-ink-3">{o.department}</div>}
                </Td>
                <Td>{o.backupName ?? <span className="text-ink-3">None</span>}</Td>
                <Td className="whitespace-nowrap">{o.reviewFrequency}{o.lastReviewedOn && <div className="text-[12px] text-ink-3">Last {fmtDate(o.lastReviewedOn)}</div>}</Td>
                <Td className="whitespace-nowrap">{o.nextReviewDue ? fmtDate(o.nextReviewDue) : <span className="text-ink-3">Not reviewed yet</span>}</Td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}

function OwnerDrawer({ target, admin, onClose, onChanged }: { target: Target; admin: boolean; onClose: () => void; onChanged: () => Promise<void> }) {
  const [data, setData] = useState<FolderOwner>();
  return (
    <Drawer open onClose={onClose} title={data ? (data.current ? data.current.ownerName : 'No owner yet') : ''} sub={<Unc>{target.resource}</Unc>} width={560}>
      <OwnerPanel folderId={target.folderId} admin={admin} onChanged={onChanged} onLoaded={setData} />
    </Drawer>
  );
}

function Stat({ label, value, warn }: { label: string; value: number; warn?: boolean }) {
  return (
    <div className="bg-surface border border-line rounded-xl px-4 py-3">
      <div className="text-[12px] text-ink-3">{label}</div>
      <div className="font-display text-[22px] font-[650] tnum mt-0.5" style={warn ? { color: 'var(--s-medium)' } : undefined}>{fmtNum(value)}</div>
    </div>
  );
}
