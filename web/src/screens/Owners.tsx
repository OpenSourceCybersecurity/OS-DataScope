import { useState } from 'react';
import { Sparkles } from 'lucide-react';
import { actions, useApp } from '../api/store';
import { useNav } from '../App';
import type { OwnerAssignment } from '../domain/types';
import { Button, Callout, Drawer, Field, PageHeader, Person, Tag, Td, Th, Unc, inputCls, nameOf } from '../ui/kit';
import { shareStats } from './common';
import { useBackend } from '../api/backend';
import LiveOwners from './live/LiveOwners';

export default function Owners() {
  const backend = useBackend();
  return backend.mode === 'live' ? <LiveOwners /> : <DemoOwners />;
}

function DemoOwners() {
  const s = useApp();
  const { readOnly } = useNav();
  const [edit, setEdit] = useState<string | null>(null);
  const unowned = s.owners.filter((o) => !o.confirmed).length;
  return (
    <>
      <PageHeader title="Owners" sub="Each share needs one accountable person who decides who gets access. Data Scope suggests owners but never assigns one without your confirmation." />
      {unowned > 0 && (
        <div className="mb-4">
          <Callout tone="warn" title={`${unowned} share${unowned > 1 ? 's have' : ' has'} no confirmed owner`}>
            Shares without an owner cannot be included in access reviews.
          </Callout>
        </div>
      )}
      <div className="bg-surface border border-line rounded-xl overflow-x-auto">
        <table className="w-full text-[13.5px] min-w-[820px]">
          <thead>
            <tr><Th>Share</Th><Th>Owner</Th><Th>Backup</Th><Th>Department</Th><Th>Review cadence</Th><Th>Sensitive files</Th><Th /></tr>
          </thead>
          <tbody>
            {s.owners.map((o) => {
              const sh = s.shares.find((x) => x.id === o.shareId)!;
              const st = shareStats(s, o.shareId);
              return (
                <tr key={o.shareId} className="border-t border-line">
                  <Td><div className="font-medium">{sh.name}</div><Unc><span className="text-ink-3">{sh.unc}</span></Unc></Td>
                  <Td>
                    {o.confirmed && o.ownerSid ? (
                      <Person sid={o.ownerSid} size={24} />
                    ) : o.suggestedOwnerSid ? (
                      <span className="inline-flex flex-col gap-1">
                        <span className="inline-flex items-center gap-1.5 text-ink-2"><Sparkles size={14} /> Suggested: {nameOf(o.suggestedOwnerSid)}</span>
                        <span className="text-[12px] text-ink-3 max-w-[38ch]">{o.suggestionReason}</span>
                      </span>
                    ) : (
                      <Tag tone="warn">No owner</Tag>
                    )}
                  </Td>
                  <Td>{o.backupOwnerSid ? nameOf(o.backupOwnerSid) : <span className="text-ink-3">—</span>}</Td>
                  <Td>{o.department ?? <span className="text-ink-3">—</span>}</Td>
                  <Td>{o.cadence}</Td>
                  <Td className="tnum">{st.sensitive}</Td>
                  <Td className="text-right whitespace-nowrap">
                    {!readOnly && (o.confirmed ? (
                      <Button size="sm" variant="ghost" onClick={() => setEdit(o.shareId)}>Edit</Button>
                    ) : o.suggestedOwnerSid ? (
                      <span className="inline-flex gap-1.5">
                        <Button size="sm" variant="primary" onClick={() => actions.setOwner(o.shareId, { ownerSid: o.suggestedOwnerSid, department: s.dir.get(o.suggestedOwnerSid!)?.department })}>Confirm</Button>
                        <Button size="sm" variant="ghost" onClick={() => setEdit(o.shareId)}>Pick someone else</Button>
                      </span>
                    ) : (
                      <Button size="sm" variant="primary" onClick={() => setEdit(o.shareId)}>Assign</Button>
                    ))}
                  </Td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      {edit && <OwnerDrawer o={s.owners.find((x) => x.shareId === edit)!} onClose={() => setEdit(null)} />}
    </>
  );
}

function OwnerDrawer({ o, onClose }: { o: OwnerAssignment; onClose: () => void }) {
  const s = useApp();
  const sh = s.shares.find((x) => x.id === o.shareId)!;
  const users = [...s.dir.values()].filter((p) => p.type === 'user' && p.enabled && !p.sam.startsWith('svc_')).sort((a, b) => a.displayName.localeCompare(b.displayName));
  const [owner, setOwner] = useState(o.ownerSid ?? o.suggestedOwnerSid ?? users[0].sid);
  const [backup, setBackup] = useState(o.backupOwnerSid ?? '');
  const [cadence, setCadence] = useState(o.cadence);
  const [purpose, setPurpose] = useState(o.purpose ?? '');
  return (
    <Drawer open onClose={onClose} title={`Owner for ${sh.name}`} sub={<Unc>{sh.unc}</Unc>}>
      <form
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          actions.setOwner(o.shareId, { ownerSid: owner, backupOwnerSid: backup || undefined, cadence, purpose, department: s.dir.get(owner)?.department });
          onClose();
        }}
      >
        <Field label="Owner" htmlFor="own-owner" hint="Usually the department manager who knows who needs the data.">
          <select id="own-owner" className={inputCls} value={owner} onChange={(e) => setOwner(e.target.value)}>
            {users.map((u) => <option key={u.sid} value={u.sid}>{u.displayName} · {u.title}</option>)}
          </select>
        </Field>
        <Field label="Backup owner" htmlFor="own-backup" hint="Receives escalations when a review is overdue.">
          <select id="own-backup" className={inputCls} value={backup} onChange={(e) => setBackup(e.target.value)}>
            <option value="">None</option>
            {users.filter((u) => u.sid !== owner).map((u) => <option key={u.sid} value={u.sid}>{u.displayName}</option>)}
          </select>
        </Field>
        <Field label="Purpose" htmlFor="own-purpose">
          <input id="own-purpose" className={inputCls} value={purpose} onChange={(e) => setPurpose(e.target.value)} placeholder="What this share is for" />
        </Field>
        <Field label="Review cadence" htmlFor="own-cadence">
          <select id="own-cadence" className={inputCls} value={cadence} onChange={(e) => setCadence(e.target.value as OwnerAssignment['cadence'])}>
            <option>Quarterly</option>
            <option>Semiannual</option>
            <option>Annual</option>
          </select>
        </Field>
        <div><Button type="submit" variant="primary">Confirm owner</Button></div>
      </form>
    </Drawer>
  );
}
