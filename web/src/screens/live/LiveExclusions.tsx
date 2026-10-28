import { useState } from 'react';
import { Loader2, Plus, X } from 'lucide-react';
import { api, type PersonRef } from '../../api/client';
import { canManageReviews, useBackend } from '../../api/backend';
import { Button, Callout, Card, Field, PageHeader, fmtDate, inputCls } from '../../ui/kit';
import { PersonPicker } from './PersonPicker';
import { useData } from './useData';

/**
 * Settings: users and groups that owners are never asked about in access reviews (D110), such as Data Scope's scan
 * account, other service accounts and IT admin groups. Administrators change the list; analysts and auditors see it.
 */
export default function LiveExclusions() {
  const admin = canManageReviews(useBackend());
  return (
    <>
      <PageHeader
        title="Review exclusions"
        sub="Service accounts and IT groups whose access IT manages, such as svc-datascope or Domain Admins. Owners don't see them in access reviews, so reviews stay about people."
      />
      <ExclusionList admin={admin} />
    </>
  );
}

/** Users and groups, from the directory sync, for the review exclusions. */
const searchUsersAndGroups = async (q: string): Promise<PersonRef[]> =>
  (await api.principals(q, undefined, 0, 20)).items
    .filter((p) => (p.kind === 'User' && p.enabled) || p.kind === 'Group')
    .map((p) => ({
      sid: p.sid,
      name: p.kind === 'User' && p.displayName ? `${p.displayName} (${p.name})` : p.name,
      department: p.kind === 'Group' ? 'Group' : p.department,
      title: p.title,
    }));

/**
 * The administrators' list of users and groups that need no decision, such as IT admin groups and backup accounts.
 * Owners are not asked about them and do not see them; administrators and auditors still see them, marked, in every
 * review. Changes apply to campaigns started afterwards.
 */
function ExclusionList({ admin }: { admin: boolean }) {
  const list = useData(() => api.reviewExemptions(), []);
  const [who, setWho] = useState<PersonRef | null>(null);
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState<string>();
  const [problem, setProblem] = useState<string>();

  const run = async (key: string, fn: () => Promise<unknown>) => {
    setBusy(key);
    setProblem(undefined);
    try {
      await fn();
      await list.reload();
      return true;
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
      return false;
    } finally {
      setBusy(undefined);
    }
  };
  const add = async (e: React.FormEvent) => {
    e.preventDefault();
    if (who && (await run('add', () => api.addReviewExemption(who.sid, reason.trim())))) {
      setWho(null);
      setReason('');
    }
  };
  const entries = list.data ?? [];

  return (
    <Card title="Excluded users and groups">
      <p className="text-[13px] text-ink-2 max-w-2xl">
        Owners are not asked about these accounts and don't see them in their reviews. Administrators and auditors still see them, marked "Managed by IT".
        Changes apply to campaigns started afterwards.
      </p>
      {list.error && <div className="mt-3"><Callout tone="bad" title="Can't load the list">{list.error}</Callout></div>}
      {problem && <div className="mt-3"><Callout tone="bad" title="That didn't work">{problem}</Callout></div>}
      {entries.length === 0 && list.data && <p className="text-[13px] text-ink-3 mt-3">Nobody is on the list.</p>}
      {entries.length > 0 && (
        <ul className="mt-3 divide-y divide-line border-y border-line">
          {entries.map((x) => (
            <li key={x.id} className="flex items-start gap-3 py-2">
              <div className="flex-1 min-w-0">
                <div className="text-[13.5px] font-medium break-words">{x.name} <span className="text-ink-3 font-normal">· {x.kind === 'Group' ? 'Group' : 'Person'}</span></div>
                <div className="text-[12.5px] text-ink-2 break-words">{x.reason}</div>
                <div className="text-[12px] text-ink-3">Added by {x.addedByName}, {fmtDate(x.addedAt)}</div>
              </div>
              {admin && (
                <Button size="sm" variant="ghost" disabled={!!busy} title={`Take ${x.name} off the list`} onClick={() => void run(String(x.id), () => api.removeReviewExemption(x.id))}>
                  {busy === String(x.id) ? <Loader2 size={13} className="animate-spin" /> : <X size={13} />} Remove
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}
      {admin && (
        <form onSubmit={add} className="mt-4 grid gap-3 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto] items-end">
          <Field label="User or group" htmlFor="nd-who">
            <PersonPicker id="nd-who" value={who} onChange={setWho} search={searchUsersAndGroups} placeholder="Type a name, such as svc-datascope or Domain Admins" exclude={entries.map((x) => x.sid)} />
          </Field>
          <Field label="Why" htmlFor="nd-why">
            <input id="nd-why" className={inputCls} value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Service account; access managed by IT" maxLength={500} />
          </Field>
          <Button type="submit" variant="primary" disabled={!who || reason.trim().length < 3 || !!busy}>
            {busy === 'add' ? <Loader2 size={14} className="animate-spin" /> : <Plus size={14} />} Add
          </Button>
        </form>
      )}
    </Card>
  );
}
