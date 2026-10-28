import { useMemo, useState } from 'react';
import { ArrowLeft, Camera, ChevronDown, ChevronRight, Lock, Mail, Plus } from 'lucide-react';
import { actions, getState, useApp } from '../api/store';
import { useNav } from '../App';
import { effectiveAccess, simulateRemoval } from '../domain/access';
import type { ReviewCampaign, ReviewDecision, ReviewItem } from '../domain/types';
import {
  Button,
  Callout,
  Card,
  Drawer,
  Field,
  Meter,
  PageHeader,
  Person,
  Tag,
  Unc,
  ago,
  cx,
  fmtDate,
  fmtDateTime,
  inDays,
  inputCls,
  nameOf,
  rightsLabel,
} from '../ui/kit';
import { AccessExplain } from './AccessExplain';
import { shareStats } from './common';
import { useBackend } from '../api/backend';
import LiveCampaigns from './live/LiveCampaigns';

export default function Reviews() {
  const backend = useBackend();
  return backend.mode === 'live' ? <LiveCampaigns /> : <DemoReviews />;
}

function DemoReviews() {
  const s = useApp();
  const { param, role } = useNav();
  const mine = role.role === 'Data Owner' ? s.campaigns.filter((c) => c.reviewerSid === role.sid) : s.campaigns;
  const initial = param && s.campaigns.some((c) => c.id === param) ? param : role.role === 'Data Owner' ? mine.find((c) => c.status !== 'Completed')?.id : undefined;
  const [open, setOpen] = useState<string | undefined>(initial);
  const [creating, setCreating] = useState(false);
  const campaign = s.campaigns.find((c) => c.id === open);

  if (campaign) return <CampaignView c={campaign} onBack={() => setOpen(undefined)} />;

  return (
    <>
      <PageHeader
        title="Access reviews"
        sub="Owners confirm who should keep access to their data. Each review is frozen on a snapshot, and IT acts on the decisions. Data Scope never changes AD or permissions itself."
        actions={role.role === 'Platform Admin' && <Button variant="primary" onClick={() => setCreating(true)}><Plus size={16} /> New review</Button>}
      />
      <div className="grid md:grid-cols-2 xl:grid-cols-3 gap-4">
        {mine.map((c) => (
          <CampaignCard key={c.id} c={c} onOpen={() => setOpen(c.id)} />
        ))}
      </div>
      {creating && <NewCampaign onClose={() => setCreating(false)} onCreated={(id) => { setCreating(false); setOpen(id); }} />}
    </>
  );
}

function CampaignCard({ c, onOpen }: { c: ReviewCampaign; onOpen: () => void }) {
  const s = useApp();
  const items = s.items.filter((i) => i.campaignId === c.id);
  const done = items.filter((i) => i.decision !== 'Pending').length;
  const removes = items.filter((i) => i.decision === 'Remove').length;
  return (
    <button onClick={onOpen} className="text-left bg-surface border border-line rounded-xl p-4 hover:border-ink-3 transition-colors flex flex-col gap-3">
      <div className="flex items-start justify-between gap-2">
        <div className="font-medium leading-snug">{c.name}</div>
        <StatusTag c={c} />
      </div>
      <Person sid={c.reviewerSid} size={26} sub="Reviewer" />
      <div>
        <Meter value={done / Math.max(1, items.length)} tone={c.status === 'Completed' ? 'good' : c.status === 'Overdue' ? 'warn' : 'accent'} />
        <div className="flex justify-between text-[12px] text-ink-3 mt-1.5 tnum">
          <span>{done} of {items.length} decided{removes ? ` · ${removes} removal${removes > 1 ? 's' : ''}` : ''}</span>
          <span>{c.status === 'Completed' ? `Due ${fmtDate(c.dueAt)}` : inDays(c.dueAt)}</span>
        </div>
      </div>
    </button>
  );
}

function StatusTag({ c }: { c: ReviewCampaign }) {
  if (c.status === 'Completed') return <Tag tone="good">Completed</Tag>;
  if (c.status === 'Overdue') return <Tag tone="bad">Overdue</Tag>;
  return <Tag tone="accent">Active</Tag>;
}

const DECISIONS: { id: Exclude<ReviewDecision, 'Pending'>; label: string; hint: string }[] = [
  { id: 'Keep', label: 'Keep', hint: 'Access is still needed' },
  { id: 'Remove', label: 'Remove', hint: 'Ask IT to remove it' },
  { id: 'Investigate', label: 'Investigate', hint: 'Not sure; flag for IT' },
  { id: 'Exception', label: 'Exception', hint: 'Unusual but approved, with a reason' },
];

function CampaignView({ c, onBack }: { c: ReviewCampaign; onBack: () => void }) {
  const s = useApp();
  const { role, readOnly, go } = useNav();
  const canDecide = !readOnly && c.status !== 'Completed' && (role.sid === c.reviewerSid || role.role === 'Platform Admin');
  const items = s.items.filter((i) => i.campaignId === c.id);
  const done = items.filter((i) => i.decision !== 'Pending').length;
  const [selected, setSelected] = useState<string[]>([]);
  const [bulk, setBulk] = useState<ReviewDecision | null>(null);
  const [filter, setFilter] = useState<'all' | 'pending' | 'flagged'>('all');
  const [showEmail, setShowEmail] = useState(false);
  const shown = items.filter((i) => filter === 'all' || (filter === 'pending' ? i.decision === 'Pending' : i.flags.length > 0));
  const groups = [...new Set(shown.map((i) => `${i.shareId}|${i.scope}`))];

  return (
    <>
      <button onClick={onBack} className="inline-flex items-center gap-1 text-[13px] text-ink-2 hover:text-ink mb-3">
        <ArrowLeft size={15} /> All reviews
      </button>
      <PageHeader
        title={c.name}
        sub={<>Reviewer {nameOf(c.reviewerSid)}{c.backupReviewerSid && <>, backup {nameOf(c.backupReviewerSid)}</>}. Due {fmtDate(c.dueAt)} ({inDays(c.dueAt)}).</>}
        actions={
          <>
            <Button onClick={() => setShowEmail(true)}><Mail size={15} /> Review email</Button>
            {role.role !== 'Data Owner' && <Button onClick={() => go('reports', `evidence:${c.id}`)}>Export evidence</Button>}
          </>
        }
      />
      <div className="grid md:grid-cols-[1fr_auto] gap-4 items-center bg-surface border border-line rounded-xl px-4 py-3 mb-4">
        <div>
          <div className="flex items-center gap-2 text-[13px] mb-2">
            <StatusTag c={c} />
            <span className="text-ink-2 tnum">{done} of {items.length} decided</span>
          </div>
          <Meter value={done / Math.max(1, items.length)} tone={c.status === 'Completed' ? 'good' : 'accent'} />
        </div>
        <div className="flex items-center gap-2 text-[12.5px] text-ink-3">
          <Camera size={15} />
          Snapshot frozen {fmtDateTime(c.snapshotAt)} · revision {c.revision}
        </div>
      </div>
      {c.status === 'Overdue' && (
        <div className="mb-4">
          <Callout tone="bad" title="Overdue">
            Reminder sent to {nameOf(c.reviewerSid)} and escalated to backup reviewer {c.backupReviewerSid ? nameOf(c.backupReviewerSid) : '(none)'}.
          </Callout>
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2 mb-3">
        {(['all', 'pending', 'flagged'] as const).map((f) => (
          <button key={f} onClick={() => setFilter(f)} className={cx('h-8 px-3 rounded-full border text-[13px]', filter === f ? 'border-accent bg-accent-soft font-medium' : 'border-line bg-surface text-ink-2')}>
            {f === 'all' ? `All ${items.length}` : f === 'pending' ? `Needs decision ${items.length - done}` : `Flagged ${items.filter((i) => i.flags.length).length}`}
          </button>
        ))}
        {canDecide && selected.length > 0 && (
          <div className="ml-auto flex flex-wrap items-center gap-2 bg-ink text-bg rounded-lg px-3 py-1.5">
            <span className="text-[13px] tnum">{selected.length} selected</span>
            {bulk ? (
              <>
                <span className="text-[13px]">Mark all as {bulk}?</span>
                <Button size="sm" variant="primary" onClick={() => { actions.decide(selected, bulk); setSelected([]); setBulk(null); }}>Confirm</Button>
                <Button size="sm" variant="ghost" className="!text-bg" onClick={() => setBulk(null)}>Cancel</Button>
              </>
            ) : (
              <>
                <Button size="sm" variant="secondary" onClick={() => setBulk('Keep')}>Keep all</Button>
                <Button size="sm" variant="secondary" onClick={() => setBulk('Investigate')}>Investigate all</Button>
                <Button size="sm" variant="ghost" className="!text-bg" onClick={() => setSelected([])}>Clear</Button>
              </>
            )}
          </div>
        )}
      </div>

      <div className="flex flex-col gap-4">
        {groups.map((g) => {
          const [shareId, scope] = g.split('|');
          const share = s.shares.find((x) => x.id === shareId)!;
          const st = shareStats(s, shareId);
          const gi = shown.filter((i) => i.shareId === shareId && i.scope === scope);
          return (
            <Card
              key={g}
              pad={false}
              title={
                <span className="flex flex-wrap items-center gap-2">
                  {share.name}{scope && <span className="text-ink-2 font-normal">\ {scope}</span>}
                  <Unc><span className="text-ink-3 font-normal">{share.unc}{scope && '\\' + scope}</span></Unc>
                  {st.sensitive > 0 && <Tag tone="warn">{st.sensitive} sensitive files</Tag>}
                </span>
              }
            >
              {scope && <div className="px-4 pb-2 text-[12.5px] text-ink-3">This folder has its own permissions (inheritance disabled).</div>}
              <ul className="divide-y divide-[var(--line)] border-t border-line">
                {gi.map((it) => (
                  <ItemRow
                    key={it.id}
                    it={it}
                    canDecide={canDecide}
                    selected={selected.includes(it.id)}
                    onSelect={(v) => setSelected((x) => (v ? [...x, it.id] : x.filter((y) => y !== it.id)))}
                  />
                ))}
              </ul>
            </Card>
          );
        })}
      </div>
      {showEmail && <EmailPreview c={c} onClose={() => setShowEmail(false)} />}
    </>
  );
}

function ItemRow({ it, canDecide, selected, onSelect }: { it: ReviewItem; canDecide: boolean; selected: boolean; onSelect: (v: boolean) => void }) {
  const s = useApp();
  const [expanded, setExpanded] = useState(false);
  const [pending, setPending] = useState<ReviewDecision | null>(null);
  const [why, setWhy] = useState('');
  const p = s.dir.get(it.trusteeSid);
  const share = s.shares.find((x) => x.id === it.shareId)!;
  const needsWhy = pending === 'Remove' || pending === 'Exception';
  const impact = useMemo(() => (pending === 'Remove' ? simulateRemoval(s.dir, share, it.trusteeSid) : null), [pending, s.dir, share, it.trusteeSid]);
  const lastUse = (sid: string) => s.events.find((e) => e.principalSid === sid && e.shareId === it.shareId && e.certainty === 'confirmed');

  const submit = () => {
    if (!pending) return;
    actions.decide([it.id], pending, why.trim() || undefined);
    setPending(null);
    setWhy('');
  };

  return (
    <li className={cx('px-4 py-3', selected && 'bg-accent-soft/50')}>
      <div className="flex flex-wrap items-center gap-3">
        {canDecide && (
          <input type="checkbox" aria-label="Select" checked={selected} onChange={(e) => onSelect(e.target.checked)} className="size-4 accent-[var(--accent)]" />
        )}
        <button onClick={() => setExpanded(!expanded)} className="flex items-center gap-2 min-w-0 flex-1 text-left" aria-expanded={expanded}>
          {expanded ? <ChevronDown size={15} className="text-ink-3 shrink-0" /> : <ChevronRight size={15} className="text-ink-3 shrink-0" />}
          <span className="min-w-0">
            <Person
              sid={it.trusteeSid}
              sub={
                p?.type === 'user'
                  ? `${p.title} · person on the permission list`
                  : p
                    ? `${p.type === 'wellknown' ? 'Built-in group' : 'Group'} · ${it.memberCount} ${it.memberCount === 1 ? 'person' : 'people'}`
                    : 'Unknown account (SID did not resolve)'
              }
            />
          </span>
        </button>
        <div className="flex flex-wrap gap-1">
          {it.flags.map((f) => (
            <Tag key={f} tone={['Everyone in the company', 'Disabled account', 'Unresolved SID', 'Includes disabled accounts'].includes(f) ? 'bad' : 'warn'}>{f}</Tag>
          ))}
        </div>
        <span className="w-24 text-[13px] font-medium text-right">{rightsLabel(it.rights)}</span>
        <div className="w-full lg:w-auto">
          {canDecide ? (
            <div className="inline-flex rounded-md border border-line overflow-hidden" role="group" aria-label="Decision">
              {DECISIONS.map((d) => {
                const on = (pending ?? it.decision) === d.id;
                return (
                  <button
                    key={d.id}
                    title={d.hint}
                    onClick={() => (d.id === 'Keep' ? actions.decide([it.id], 'Keep') : setPending(d.id))}
                    className={cx(
                      'px-2.5 h-8 text-[13px] border-r border-line last:border-0',
                      on ? (d.id === 'Remove' ? 'bg-[var(--s-critical)] text-white' : d.id === 'Keep' ? 'bg-[var(--s-good)] text-white' : 'bg-ink text-bg') : 'bg-surface hover:bg-surface-2 text-ink-2',
                    )}
                  >
                    {d.label}
                  </button>
                );
              })}
            </div>
          ) : (
            <DecisionTag d={it.decision} />
          )}
        </div>
      </div>

      {it.decision !== 'Pending' && !pending && (
        <div className="text-[12px] text-ink-3 mt-1.5 ml-7">
          {canDecide && <DecisionTag d={it.decision} />} {it.decidedBy && `by ${nameOf(it.decidedBy)} ${ago(it.decidedAt)}`}
          {it.justification && <> · “{it.justification}”</>}
          {it.history.length > 1 && <> · changed {it.history.length - 1}×</>}
        </div>
      )}

      {pending && pending !== 'Keep' && (
        <div className="mt-3 ml-7 bg-surface-2 rounded-lg p-3 flex flex-col gap-3">
          {impact && (
            <div className="text-[13px]">
              <div className="font-medium mb-1">If IT removes {p?.displayName ?? 'this entry'} from {share.name}:</div>
              <ul className="text-ink-2 list-disc ml-5">
                <li>{impact.loseAccess.length} {impact.loseAccess.length === 1 ? 'person loses' : 'people lose'} access{impact.loseAccess.length ? `: ${impact.loseAccess.slice(0, 5).map(nameOf).join(', ')}${impact.loseAccess.length > 5 ? '…' : ''}` : ''}</li>
                {!!impact.keepAccess.length && (
                  <li>{impact.keepAccess.length} keep access through another group: {impact.keepAccess.slice(0, 4).map((k) => `${nameOf(k.sid)} (via ${k.via.map(nameOf).join(', ')})`).join('; ')}</li>
                )}
                {p?.type === 'group' && <li>The group itself is not changed in AD. Only this share’s permission entry is removed.</li>}
              </ul>
            </div>
          )}
          {pending === 'Investigate' && <div className="text-[13px] text-ink-2">IT will look into this entry. You can change your decision later.</div>}
          <Field label={needsWhy ? 'Reason (required)' : 'Comment (optional)'} htmlFor={`why-${it.id}`}>
            <input id={`why-${it.id}`} className={inputCls} value={why} onChange={(e) => setWhy(e.target.value)} placeholder={pending === 'Exception' ? 'Why this unusual access is approved' : 'What prompted this'} />
          </Field>
          <div className="flex gap-2">
            <Button variant={pending === 'Remove' ? 'danger' : 'primary'} size="sm" disabled={needsWhy && !why.trim()} onClick={submit}>
              {pending === 'Remove' ? 'Request removal' : `Save ${pending.toLowerCase()}`}
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setPending(null)}>Cancel</Button>
          </div>
        </div>
      )}

      {expanded && (
        <div className="mt-3 ml-7">
          {p?.type === 'user' ? (
            <AccessExplain e={effectiveAccess(getState().dir, share, it.trusteeSid, it.scope || undefined)} />
          ) : (
            <div>
              <div className="eyebrow mb-2">Members at snapshot ({it.members.length})</div>
              <div className="grid sm:grid-cols-2 gap-2">
                {it.members.map((m) => {
                  const last = lastUse(m);
                  return <Person key={m} sid={m} size={24} sub={`${s.dir.get(m)?.title ?? ''} · ${last ? `opened files ${ago(last.time)}` : 'no file activity in 7 days'}`} />;
                })}
              </div>
              {!it.members.length && <p className="text-[13px] text-ink-3">{p ? 'No members currently reach this share.' : 'Members cannot be listed because the SID did not resolve.'}</p>}
              <p className="text-[11.5px] text-ink-3 mt-2">File activity comes from the activity collector preview.</p>
            </div>
          )}
        </div>
      )}
    </li>
  );
}

function DecisionTag({ d }: { d: ReviewDecision }) {
  const tone = d === 'Keep' ? 'good' : d === 'Remove' ? 'bad' : d === 'Pending' ? 'neutral' : 'warn';
  return <Tag tone={tone}>{d === 'Pending' ? 'Needs decision' : d}</Tag>;
}

function NewCampaign({ onClose, onCreated }: { onClose: () => void; onCreated: (id: string) => void }) {
  const s = useApp();
  const owned = s.owners.filter((o) => o.confirmed && o.ownerSid);
  const [shareId, setShareId] = useState(owned[0]?.shareId ?? '');
  const [due, setDue] = useState(14);
  const owner = s.owners.find((o) => o.shareId === shareId);
  const share = s.shares.find((x) => x.id === shareId);
  const [name, setName] = useState('');
  const defaultName = share ? `${share.name} access review, Q4 2026` : '';
  return (
    <Drawer open onClose={onClose} title="New access review" sub="Freezes today’s permissions and group memberships, then emails the owner a sign-in link.">
      <form
        className="flex flex-col gap-4"
        onSubmit={(e) => {
          e.preventDefault();
          if (!owner?.ownerSid) return;
          onCreated(actions.createCampaign({ name: name.trim() || defaultName, shareIds: [shareId], reviewerSid: owner.ownerSid, backupReviewerSid: owner.backupOwnerSid, dueDays: due }));
        }}
      >
        <Field label="Share" htmlFor="cmp-share" hint="Only shares with a confirmed owner can be reviewed.">
          <select id="cmp-share" className={inputCls} value={shareId} onChange={(e) => setShareId(e.target.value)}>
            {s.shares.map((x) => {
              const o = s.owners.find((y) => y.shareId === x.id);
              return (
                <option key={x.id} value={x.id} disabled={!o?.confirmed}>
                  {x.name}{o?.confirmed && o.ownerSid ? ` (owner: ${nameOf(o.ownerSid)})` : ' (no owner yet)'}
                </option>
              );
            })}
          </select>
        </Field>
        <Field label="Name" htmlFor="cmp-name">
          <input id="cmp-name" className={inputCls} value={name} placeholder={defaultName} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Due in" htmlFor="cmp-due">
          <select id="cmp-due" className={inputCls} value={due} onChange={(e) => setDue(Number(e.target.value))}>
            <option value={7}>7 days</option>
            <option value={14}>14 days</option>
            <option value={30}>30 days</option>
          </select>
        </Field>
        {owner?.ownerSid && (
          <Callout tone="info" title="What happens next">
            {nameOf(owner.ownerSid)} gets an email with a link that requires signing in with their domain account. Nobody can approve from the email itself. Reminders go out 3 days before the due date, and overdue reviews escalate to {owner.backupOwnerSid ? nameOf(owner.backupOwnerSid) : 'IT'}.
          </Callout>
        )}
        <div>
          <Button type="submit" variant="primary" disabled={!owner?.ownerSid}>
            <Lock size={15} /> Freeze snapshot and send
          </Button>
        </div>
      </form>
    </Drawer>
  );
}

function EmailPreview({ c, onClose }: { c: ReviewCampaign; onClose: () => void }) {
  const s = useApp();
  const reviewer = s.dir.get(c.reviewerSid)!;
  const items = s.items.filter((i) => i.campaignId === c.id);
  return (
    <Drawer open onClose={onClose} title="Review email" sub={`Sent ${fmtDateTime(c.emailSentAt)} through the on-prem SMTP relay`}>
      <div className="border border-line rounded-lg overflow-hidden text-[13.5px]">
        <div className="bg-surface-2 px-4 py-2.5 text-[12.5px] text-ink-2 flex flex-col gap-0.5">
          <span><b>From:</b> Data Scope &lt;datascope@northwind.example&gt;</span>
          <span><b>To:</b> {reviewer.displayName} &lt;{reviewer.email}&gt;</span>
          <span><b>Subject:</b> Action needed: {c.name}</span>
        </div>
        <div className="px-4 py-4 flex flex-col gap-3">
          <p>Hi {reviewer.displayName.split(' ')[0]},</p>
          <p>You own {c.shareIds.map((id) => s.shares.find((x) => x.id === id)?.name).join(', ')}. Please confirm who should still have access. There are {items.length} entries to review, due {fmtDate(c.dueAt)}.</p>
          <p>
            <span className="inline-block bg-accent text-accent-ink rounded-md px-3 py-1.5 font-medium">Sign in to review</span>
          </p>
          <p className="text-ink-3 text-[12.5px]">This link opens Data Scope on your company network and asks you to sign in with your Windows account. It cannot be used to approve anything on its own and expires in 30 days.</p>
        </div>
      </div>
    </Drawer>
  );
}
