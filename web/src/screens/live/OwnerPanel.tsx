import { useEffect, useState } from 'react';
import { api, type FolderOwner, type OwnerSuggestion, type PersonRef, type ReviewFrequency } from '../../api/client';
import { Button, Callout, Field, Tag, cx, fmtDate, inputCls } from '../../ui/kit';
import { PersonPicker, searchDirectoryUsers } from './PersonPicker';

export const SUGGESTION_BASIS: Record<OwnerSuggestion['basis'], string> = {
  Manager: 'Manager',
  WindowsOwner: 'Windows owner',
  DirectRights: 'Direct rights',
};

/**
 * The owner of one share or folder: who it is, who Data Scope suggests, and (for administrators) the form to assign,
 * change or remove them. Used on the Owners screen and in a share's details.
 */
export function OwnerPanel({ folderId, admin, onChanged, onLoaded }: {
  folderId: string;
  admin: boolean;
  onChanged?: () => Promise<void>;
  onLoaded?: (d: FolderOwner) => void;
}) {
  const [data, setData] = useState<FolderOwner>();
  const [owner, setOwner] = useState<PersonRef | null>(null);
  const [backup, setBackup] = useState<PersonRef | null>(null);
  const [frequency, setFrequency] = useState<ReviewFrequency>('Quarterly');
  const [purpose, setPurpose] = useState('');
  const [department, setDepartment] = useState('');
  const [ownerEmail, setOwnerEmail] = useState('');
  const [notes, setNotes] = useState('');
  const [ending, setEnding] = useState(false);
  const [reason, setReason] = useState('');
  const [problem, setProblem] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);

  const load = async () => {
    const d = await api.folderOwner(folderId);
    setData(d);
    onLoaded?.(d);
    const c = d.current;
    setOwner(c ? { sid: c.ownerSid, name: c.ownerName, department: c.department, title: null } : null);
    setBackup(c?.backupSid ? { sid: c.backupSid, name: c.backupName ?? c.backupSid, department: null, title: null } : null);
    setFrequency(c?.reviewFrequency ?? 'Quarterly');
    setPurpose(c?.purpose ?? '');
    setDepartment(c?.department ?? '');
    setOwnerEmail(c?.ownerEmail ?? '');
    setNotes(c?.notes ?? '');
  };
  useEffect(() => {
    setData(undefined);
    setProblem(undefined);
    load().catch((e) => setProblem(e instanceof Error ? e.message : String(e)));
  }, [folderId]); // eslint-disable-line react-hooks/exhaustive-deps

  const act = async (work: () => Promise<unknown>) => {
    setBusy(true);
    setProblem(undefined);
    setSaved(false);
    try {
      await work();
      await onChanged?.();
      await load();
      setSaved(true);
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const save = () =>
    act(() =>
      api.assignOwner(folderId, {
        ownerSid: owner!.sid,
        ownerEmail: ownerEmail.trim() || undefined,
        backupSid: backup?.sid,
        reviewFrequency: frequency,
        purpose: purpose.trim() || undefined,
        department: department.trim() || undefined,
        notes: notes.trim() || undefined,
      }),
    );

  const current = data?.current;
  const suggestions = (data?.suggestions ?? []).filter((sg) => sg.sid !== current?.ownerSid);
  return (
    <div>
      {problem && <div className="mb-3"><Callout tone="bad" title="That didn't work">{problem}</Callout></div>}
      {saved && !problem && <div className="mb-3"><Callout tone="good">Saved.</Callout></div>}
      {!data && !problem && <p className="text-ink-3 text-[13.5px]">Loading…</p>}
      {data && (
        <div className="space-y-4">
          {admin ? (
            <form className="space-y-3" onSubmit={(e) => (e.preventDefault(), void save())}>
              {suggestions.length > 0 && (
                <div>
                  <div className="text-[13px] font-medium mb-1">{current ? 'Other suggestions' : 'Suggestions'}</div>
                  <div className="grid gap-2">
                    {suggestions.map((sg) => (
                      <button
                        key={sg.sid}
                        type="button"
                        className={cx('text-left rounded-lg border px-3 py-2 hover:bg-surface-2', owner?.sid === sg.sid ? 'border-accent bg-accent-soft' : 'border-line')}
                        onClick={() => setOwner({ sid: sg.sid, name: sg.name, department: sg.department, title: sg.title })}
                      >
                        <span className="flex flex-wrap items-center gap-2">
                          <span className="text-[13.5px] font-medium">{sg.name}</span>
                          <Tag tone={sg.basis === 'Manager' ? 'accent' : 'neutral'}>{SUGGESTION_BASIS[sg.basis]}</Tag>
                        </span>
                        {(sg.title || sg.department) && <span className="block text-[12px] text-ink-2">{[sg.title, sg.department].filter(Boolean).join(' · ')}</span>}
                        <span className="block text-[12px] text-ink-3">{sg.reason}</span>
                      </button>
                    ))}
                  </div>
                  <p className="text-[12px] text-ink-3 mt-1">Suggestions come from the folder's permissions and the reporting lines in Active Directory. Confirm with the business before assigning.</p>
                </div>
              )}
              <Field label="Owner" htmlFor="owner">
                <PersonPicker id="owner" value={owner} onChange={setOwner} search={searchDirectoryUsers} exclude={backup ? [backup.sid] : []} />
              </Field>
              <Field label="Backup owner" htmlFor="backup" hint="Gets overdue reviews three days after the due date. Recommended.">
                <PersonPicker id="backup" value={backup} onChange={setBackup} search={searchDirectoryUsers} exclude={owner ? [owner.sid] : []} />
              </Field>
              <div className="grid sm:grid-cols-2 gap-3">
                <Field label="Review" htmlFor="freq">
                  <select id="freq" className={inputCls} value={frequency} onChange={(e) => setFrequency(e.target.value as ReviewFrequency)}>
                    <option value="Quarterly">Every quarter</option>
                    <option value="Semiannual">Twice a year</option>
                    <option value="Annual">Every year</option>
                  </select>
                </Field>
                <Field label="Department" htmlFor="dept">
                  <input id="dept" className={inputCls} value={department} onChange={(e) => setDepartment(e.target.value)} maxLength={128} placeholder="From Active Directory if empty" />
                </Field>
              </div>
              <Field label="Email for review notices" htmlFor="mail" hint="Leave empty to use the address in Active Directory.">
                <input id="mail" type="email" className={inputCls} value={ownerEmail} onChange={(e) => setOwnerEmail(e.target.value)} maxLength={256} />
              </Field>
              <Field label="What it holds" htmlFor="purpose">
                <input id="purpose" className={inputCls} value={purpose} onChange={(e) => setPurpose(e.target.value)} maxLength={500} placeholder="For example: payroll and HR records" />
              </Field>
              <Field label="Notes" htmlFor="notes">
                <textarea id="notes" className={cx(inputCls, 'h-16 py-2')} value={notes} onChange={(e) => setNotes(e.target.value)} maxLength={1000} />
              </Field>
              <div className="flex flex-wrap gap-2">
                <Button type="submit" variant="primary" disabled={busy || !owner}>{current ? 'Save changes' : 'Assign owner'}</Button>
                {current && !ending && <Button variant="ghost" onClick={() => setEnding(true)}>Remove owner</Button>}
              </div>
            </form>
          ) : current ? (
            <dl className="grid grid-cols-[130px_1fr] gap-y-1.5 text-[13.5px]">
              <dt className="text-ink-3">Owner</dt><dd>{current.ownerName}</dd>
              <dt className="text-ink-3">Backup</dt><dd>{current.backupName ?? 'None'}</dd>
              <dt className="text-ink-3">Review</dt><dd>{current.reviewFrequency}</dd>
              <dt className="text-ink-3">Purpose</dt><dd>{current.purpose ?? '—'}</dd>
              <dt className="text-ink-3">Assigned</dt><dd>{fmtDate(current.assignedAt)} by {current.assignedByName}</dd>
            </dl>
          ) : (
            <p className="text-ink-2 text-[13.5px]">No owner yet. An administrator can assign one.</p>
          )}
          {ending && (
            <div className="space-y-2 border-t border-line pt-3">
              <Field label="Why does it no longer have this owner?" htmlFor="reason">
                <input id="reason" className={inputCls} value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} placeholder="For example: share retired" />
              </Field>
              <div className="flex gap-2">
                <Button variant="danger" disabled={busy || reason.trim().length < 3} onClick={() => act(async () => (await api.endOwner(folderId, reason.trim()), setEnding(false), setReason('')))}>Remove owner</Button>
                <Button variant="ghost" onClick={() => setEnding(false)}>Cancel</Button>
              </div>
            </div>
          )}
          {data.history.length > 0 && (
            <div className="border-t border-line pt-3">
              <div className="text-[13px] font-medium mb-1">Earlier owners</div>
              <ul className="space-y-1 text-[13px]">
                {data.history.map((h) => (
                  <li key={h.id}>
                    {h.ownerName} <span className="text-ink-3">· {fmtDate(h.assignedAt)} to {fmtDate(h.endedAt ?? undefined)}{h.endReason && ` · ${h.endReason}`}</span>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
