import { useCallback, useState } from 'react';
import { ArrowLeft, ClipboardCheck, Download, Lock, Plus, ShieldCheck } from 'lucide-react';
import { api, type Campaign, type CampaignVerification, type ReviewFrequency } from '../../api/client';
import { canManageReviews, useBackend } from '../../api/backend';
import { Button, Callout, Card, Drawer, Empty, Field, Meter, PageHeader, Tag, Td, Th, Unc, cx, fmtDate, fmtDateTime, inputCls } from '../../ui/kit';
import { ReviewView } from './ReviewView';
import { searchDirectoryUsers } from './PersonPicker';
import { REVIEW_STATE, dueText, progress, today } from './reviews';
import { useData } from './useData';
import { useNav } from '../../App';

/**
 * Access review campaigns for administrators, analysts and auditors. Starting a campaign freezes, for every owned
 * share or folder in scope, who could reach it at that moment; owners then review in My reviews.
 */
export default function LiveCampaigns() {
  const backend = useBackend();
  const { go } = useNav();
  const admin = canManageReviews(backend);
  const campaigns = useData(() => api.campaigns(), []);
  const [creating, setCreating] = useState(false);
  const [openId, setOpenId] = useState<string>();

  if (openId) return <CampaignView id={openId} admin={admin} onBack={() => (setOpenId(undefined), void campaigns.reload())} />;

  return (
    <>
      <PageHeader
        title="Access reviews"
        sub="Each campaign asks owners to confirm who should still reach their data. What they review is frozen when the campaign starts, so later changes never alter the evidence."
        actions={admin && <Button variant="primary" onClick={() => setCreating(true)}><Plus size={15} /> Start a campaign</Button>}
      />
      {campaigns.error && <div className="mb-4"><Callout tone="bad" title="Can't reach the Data Scope server">{campaigns.error}</Callout></div>}
      {campaigns.data && campaigns.data.length === 0 && (
        <Card>
          <Empty icon={<ClipboardCheck size={20} />} title="No campaigns yet">
            Assign owners on the Owners screen, then start a campaign. Owners get an email and review in Data Scope.
          </Empty>
        </Card>
      )}
      {campaigns.data && campaigns.data.length > 0 && (
        <Card pad={false}>
          <div className="overflow-x-auto">
            <table className="w-full text-[13.5px] min-w-[760px]">
              <thead>
                <tr><Th>Campaign</Th><Th>Due</Th><Th>Reviews submitted</Th><Th className="text-right">Decisions</Th><Th>Status</Th></tr>
              </thead>
              <tbody>
                {campaigns.data.map((c) => (
                  <tr key={c.id} className="border-t border-line cursor-pointer hover:bg-surface-2" onClick={() => setOpenId(c.id)}>
                    <Td>
                      <div className="font-medium">{c.name}</div>
                      <div className="text-[12px] text-ink-3">Started {fmtDate(c.createdAt)} by {c.createdByName}</div>
                    </Td>
                    <Td className="whitespace-nowrap">{c.state === 'Open' ? <Tag tone={c.overdue ? 'bad' : 'neutral'}>{dueText(c.dueOn, today())}</Tag> : fmtDate(c.dueOn)}</Td>
                    <Td className="min-w-[160px]">
                      <Meter value={c.reviews ? c.submitted / c.reviews : 0} tone={c.submitted === c.reviews ? 'good' : 'accent'} label="Reviews submitted" />
                      <div className="text-[12px] text-ink-3 tnum mt-1">{c.submitted} of {c.reviews}</div>
                    </Td>
                    <Td className="text-right tnum">{c.decided} / {c.decisionsRequired}</Td>
                    <Td><Tag tone={c.state === 'Open' ? 'accent' : 'neutral'}>{c.state}</Tag></Td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </Card>
      )}
      <p className="mt-4 text-[13px] text-ink-2">
        Service accounts and IT groups that owners shouldn't be asked about are set in{' '}
        <button type="button" className="text-accent font-medium hover:underline" onClick={() => go('exclusions')}>Review exclusions</button>.
      </p>
      {creating && <CreateCampaign onClose={() => setCreating(false)} onCreated={(c) => (setCreating(false), setOpenId(c.id))} />}
    </>
  );
}

function CreateCampaign({ onClose, onCreated }: { onClose: () => void; onCreated: (c: Campaign) => void }) {
  const owners = useData(() => api.owners(), []);
  const [name, setName] = useState('');
  const [description, setDescription] = useState('');
  const [dueOn, setDueOn] = useState(() => {
    const d = new Date();
    d.setUTCDate(d.getUTCDate() + 21);
    return d.toISOString().slice(0, 10);
  });
  const [frequency, setFrequency] = useState<'' | ReviewFrequency>('');
  const [shareIds, setShareIds] = useState<Set<string>>(new Set());
  const [problem, setProblem] = useState<string>();
  const [busy, setBusy] = useState(false);
  const shares = [...new Map((owners.data?.items ?? []).map((o) => [o.shareId, `\\\\${o.hostname}\\${o.shareName}`])).entries()].sort((a, b) => a[1].localeCompare(b[1]));
  const inScope = (owners.data?.items ?? []).filter((o) => (!frequency || o.reviewFrequency === frequency) && (shareIds.size === 0 || shareIds.has(o.shareId))).length;

  const submit = async () => {
    setBusy(true);
    setProblem(undefined);
    try {
      onCreated(await api.createCampaign({ name: name.trim(), description: description.trim() || undefined, dueOn, frequency: frequency || undefined, shareIds: [...shareIds] }));
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Drawer open onClose={onClose} title="Start an access review" sub="Owners get an email and review in Data Scope. Nothing changes in Windows." width={560}>
      <form className="space-y-4" onSubmit={(e) => (e.preventDefault(), void submit())}>
        <Field label="Name" htmlFor="c-name">
          <input id="c-name" className={inputCls} value={name} onChange={(e) => setName(e.target.value)} placeholder="Q4 2026 access review" maxLength={200} />
        </Field>
        <Field label="Note for owners (optional)" htmlFor="c-desc">
          <textarea id="c-desc" className={cx(inputCls, 'h-20 py-2')} value={description} onChange={(e) => setDescription(e.target.value)} maxLength={2000} />
        </Field>
        <Field label="Due on" htmlFor="c-due" hint="Reminders go out a week before and the day before. Three days after, overdue reviews also go to the backup owner.">
          <input id="c-due" type="date" className={inputCls} value={dueOn} min={today()} onChange={(e) => setDueOn(e.target.value)} />
        </Field>
        <Field label="Include" htmlFor="c-freq">
          <select id="c-freq" className={inputCls} value={frequency} onChange={(e) => setFrequency(e.target.value as '' | ReviewFrequency)}>
            <option value="">Every owned share and folder</option>
            <option value="Quarterly">Only those reviewed quarterly</option>
            <option value="Semiannual">Only those reviewed twice a year</option>
            <option value="Annual">Only those reviewed yearly</option>
          </select>
        </Field>
        {shares.length > 0 && (
          <fieldset>
            <legend className="text-[13px] font-medium mb-1">Shares (leave all unticked for every share)</legend>
            <div className="max-h-48 overflow-y-auto scroll-thin border border-line rounded-lg divide-y divide-line">
              {shares.map(([id, unc]) => (
                <label key={id} className="flex items-center gap-2 px-3 py-1.5 text-[13px]">
                  <input
                    type="checkbox"
                    className="size-4 accent-[var(--accent)]"
                    checked={shareIds.has(id)}
                    onChange={(e) =>
                      setShareIds((s) => {
                        const next = new Set(s);
                        if (e.target.checked) next.add(id);
                        else next.delete(id);
                        return next;
                      })
                    }
                  />
                  <Unc>{unc}</Unc>
                </label>
              ))}
            </div>
          </fieldset>
        )}
        <Callout tone="info">
          {inScope} owned {inScope === 1 ? 'resource' : 'resources'} in scope. Only shares and folders with an owner are reviewed, and only where the last scan could read the permissions; the rest are listed as skipped with the reason.
        </Callout>
        {problem && <Callout tone="bad" title="Can't start the campaign">{problem}</Callout>}
        <div className="flex gap-2">
          <Button type="submit" variant="primary" disabled={busy || inScope === 0}>Start campaign</Button>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
        </div>
      </form>
    </Drawer>
  );
}

function CampaignView({ id, admin, onBack }: { id: string; admin: boolean; onBack: () => void }) {
  const detail = useData(() => api.campaign(id), [id]);
  const [reviewId, setReviewId] = useState<string>();
  const [verification, setVerification] = useState<CampaignVerification>();
  const [closing, setClosing] = useState(false);
  const [note, setNote] = useState('');
  const [problem, setProblem] = useState<string>();

  if (reviewId) return <AdminReview id={reviewId} admin={admin} onBack={() => (setReviewId(undefined), void detail.reload())} />;
  if (detail.error) return <Callout tone="bad" title="Can't open this campaign">{detail.error}</Callout>;
  if (!detail.data) return <p className="text-ink-3">Loading…</p>;
  const c = detail.data.campaign;

  const act = async (work: () => Promise<unknown>) => {
    setProblem(undefined);
    try {
      await work();
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    }
  };

  return (
    <>
      <button className="inline-flex items-center gap-1 text-[13px] text-ink-2 hover:text-ink mb-3" onClick={onBack}>
        <ArrowLeft size={15} /> All campaigns
      </button>
      <PageHeader
        eyebrow={c.state === 'Open' ? dueText(c.dueOn, today()) : `Closed ${fmtDate(c.closedAt ?? undefined)} by ${c.closedByName}`}
        title={c.name}
        sub={c.description ?? `Frozen on ${fmtDateTime(c.createdAt)}${c.directorySyncedAt ? ` from the Active Directory sync of ${fmtDateTime(c.directorySyncedAt)}` : ''}.`}
        actions={
          <>
            <Button onClick={() => act(async () => setVerification(await api.verifyCampaign(id)))}><ShieldCheck size={15} /> Check evidence seal</Button>
            <a className="inline-flex items-center gap-1.5 h-9 px-3.5 rounded-md text-sm font-medium bg-surface border border-line hover:bg-surface-2" href={api.evidenceUrl(id, 'csv')}>
              <Download size={15} /> Evidence (CSV)
            </a>
            <a className="inline-flex items-center gap-1.5 h-9 px-3.5 rounded-md text-sm font-medium bg-surface border border-line hover:bg-surface-2" href={api.evidenceUrl(id, 'json')}>
              <Download size={15} /> Full evidence (JSON)
            </a>
            {admin && c.state === 'Open' && <Button variant="danger" onClick={() => setClosing(true)}><Lock size={15} /> Close campaign</Button>}
          </>
        }
      />
      {problem && <div className="mb-4"><Callout tone="bad" title="That didn't work">{problem}</Callout></div>}
      {verification && (
        <div className="mb-4">
          {verification.mismatched.length === 0 ? (
            <Callout tone="good" title="Evidence intact">All {verification.reviews} reviews match the seal taken when the campaign started (checked {fmtDateTime(verification.checkedAt)}).</Callout>
          ) : (
            <Callout tone="bad" title="Evidence changed">
              {verification.mismatched.length} of {verification.reviews} reviews no longer match their seal: {verification.mismatched.map((m) => m.resource).join(', ')}. Treat them as unreliable and investigate the database.
            </Callout>
          )}
        </div>
      )}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-3 mb-4">
        <Stat label="Reviews submitted" value={`${c.submitted} of ${c.reviews}`} />
        <Stat label="In progress" value={String(c.inProgress)} />
        <Stat label="Not started" value={String(c.pending)} />
        <Stat label="Decisions made" value={`${c.decided} of ${c.decisionsRequired}`} />
      </div>
      {c.skipped.length > 0 && (
        <div className="mb-4">
          <Callout tone="warn" title={`${c.skipped.length} owned ${c.skipped.length === 1 ? 'resource was' : 'resources were'} not included`}>
            <ul className="list-disc pl-5 mt-1">
              {c.skipped.map((s) => (
                <li key={s.folderId}><Unc>{s.resource}</Unc>: {s.reason}</li>
              ))}
            </ul>
          </Callout>
        </div>
      )}
      <Card pad={false}>
        <div className="overflow-x-auto">
          <table className="w-full text-[13.5px] min-w-[820px]">
            <thead>
              <tr><Th>Share or folder</Th><Th>Reviewer</Th><Th>Progress</Th><Th className="text-right">Remove</Th><Th>Status</Th></tr>
            </thead>
            <tbody>
              {detail.data.reviews.map((r) => (
                <tr key={r.id} className="border-t border-line cursor-pointer hover:bg-surface-2" onClick={() => setReviewId(r.id)}>
                  <Td><Unc>{r.resource}</Unc></Td>
                  <Td>
                    {r.ownerName}
                    {r.delegateName && <div className="text-[12px] text-ink-3">Handed to {r.delegateName}</div>}
                    {r.escalatedAt && <div className="text-[12px]" style={{ color: 'var(--s-medium)' }}>Escalated to {r.backupName}</div>}
                  </Td>
                  <Td className="min-w-[140px]">
                    <Meter value={progress(r)} tone={r.state === 'Submitted' ? 'good' : 'accent'} label="Decisions made" />
                    <div className="text-[12px] text-ink-3 tnum mt-1">{r.decided} of {r.decisionsRequired}</div>
                  </Td>
                  <Td className="text-right tnum">{r.remove}</Td>
                  <Td><Tag tone={r.overdue ? 'bad' : REVIEW_STATE[r.state].tone}>{r.overdue ? 'Overdue' : REVIEW_STATE[r.state].label}</Tag></Td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </Card>
      <Drawer open={closing} onClose={() => setClosing(false)} title="Close this campaign?" sub={c.name} width={480}>
        <div className="space-y-3 text-[13.5px]">
          <p>Owners can no longer decide or submit. {c.reviews - c.submitted > 0 ? `${c.reviews - c.submitted} unsubmitted review(s) are kept as they are, marked as closed before submission, and create no tasks.` : 'Every review was submitted.'}</p>
          <Field label="Note (optional)" htmlFor="close-note">
            <textarea id="close-note" className={cx(inputCls, 'h-20 py-2')} value={note} onChange={(e) => setNote(e.target.value)} maxLength={1000} />
          </Field>
          <div className="flex gap-2">
            <Button variant="danger" onClick={() => act(async () => (await api.closeCampaign(id, note.trim() || undefined), setClosing(false), await detail.reload()))}>Close campaign</Button>
            <Button variant="ghost" onClick={() => setClosing(false)}>Cancel</Button>
          </div>
        </div>
      </Drawer>
    </>
  );
}

function AdminReview({ id, admin, onBack }: { id: string; admin: boolean; onBack: () => void }) {
  const detail = useData(() => api.review(id), [id]);
  const loadUsers = useCallback((itemId: number, search?: string) => api.reviewItemUsers(id, itemId, search), [id]);
  if (detail.error) return <Callout tone="bad" title="Can't open this review">{detail.error}</Callout>;
  if (!detail.data) return <p className="text-ink-3">Loading…</p>;
  return (
    <>
      <div className="mb-3"><Callout tone="info">You are viewing this review read-only. Only the owner, the person they hand it to, or the backup owner after escalation can decide.</Callout></div>
      <ReviewView
        detail={detail.data}
        loadUsers={loadUsers}
        onDelegate={admin ? async (p) => (await api.reassignReview(id, p?.sid ?? null), await detail.reload()) : undefined}
        peopleSearch={searchDirectoryUsers}
        adminReassign={admin}
        onBack={onBack}
        backLabel="Campaign"
      />
    </>
  );
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="bg-surface border border-line rounded-xl px-4 py-3">
      <div className="text-[12px] text-ink-3">{label}</div>
      <div className="font-display text-[22px] font-[650] tnum mt-0.5">{value}</div>
    </div>
  );
}
