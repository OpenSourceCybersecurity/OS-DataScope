import { useEffect, useMemo, useState } from 'react';
import { ArrowLeft, CheckCircle2, ShieldCheck, UserRound, Users } from 'lucide-react';
import type { DecisionInput, DecisionKind, PersonRef, ReviewDetail, ReviewItem, ReviewItemUser } from '../../api/client';
import { Button, Callout, Card, Drawer, Meter, Tag, Td, Th, Unc, cx, fmtDate, fmtDateTime, fmtNum, inputCls } from '../../ui/kit';
import { PersonPicker } from './PersonPicker';
import { DECISIONS, FLAGS, MAX_TEXT, REVIEW_STATE, ROLE_TEXT, decisionProblem, dueText, progress, today } from './reviews';

const KINDS: DecisionKind[] = ['Keep', 'Remove', 'Investigate', 'Exception'];

const TRUSTEE_KIND: Record<string, string> = {
  User: 'Person',
  Group: 'Group',
  Computer: 'Computer',
  ForeignSecurityPrincipal: 'Other domain',
  LocalGroup: 'Server group',
  WellKnown: 'Built-in',
  Deleted: 'Deleted account',
  Unknown: 'Unknown account',
};

type Props = {
  detail: ReviewDetail;
  loadUsers: (itemId: number, search?: string) => Promise<ReviewItemUser[]>;
  /** Absent when the viewer can only read the review. */
  onDecide?: (decisions: DecisionInput[]) => Promise<void>;
  onSubmit?: () => Promise<void>;
  onDelegate?: (person: PersonRef | null) => Promise<void>;
  peopleSearch?: (q: string) => Promise<PersonRef[]>;
  onBack?: () => void;
  backLabel?: string;
  /** An administrator may reassign any open review, not just the owner. */
  adminReassign?: boolean;
};

/**
 * One frozen access review: every entry in the folder's permissions as it stood when the campaign started, the people
 * each one reaches, and the owner's decisions. Deciding never changes anything in Windows; IT acts on the decisions.
 */
export function ReviewView({ detail, loadUsers, onDecide, onSubmit, onDelegate, peopleSearch, onBack, backLabel = 'All reviews', adminReassign }: Props) {
  const r = detail.review;
  const canDecide = detail.canDecide && !!onDecide;
  const required = detail.items.filter((i) => i.requiresDecision);
  const other = detail.items.filter((i) => !i.requiresDecision);
  const [selected, setSelected] = useState<Set<number>>(new Set());
  const [editing, setEditing] = useState<ReviewItem[] | null>(null);
  const [people, setPeople] = useState<ReviewItem>();
  const [problem, setProblem] = useState<string>();
  const [confirming, setConfirming] = useState(false);
  const [busy, setBusy] = useState(false);
  const [delegating, setDelegating] = useState(false);
  const undecided = required.filter((i) => !i.decision).length;
  const canDelegate = !!onDelegate && (detail.canDelegate || (!!adminReassign && r.campaignState === 'Open' && r.state !== 'Submitted'));

  useEffect(() => setSelected(new Set()), [detail]);

  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    setProblem(undefined);
    try {
      await work();
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  const toggle = (id: number) =>
    setSelected((s) => {
      const next = new Set(s);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });

  return (
    <>
      {onBack && (
        <button className="inline-flex items-center gap-1 text-[13px] text-ink-2 hover:text-ink mb-3" onClick={onBack}>
          <ArrowLeft size={15} /> {backLabel}
        </button>
      )}
      <div className="flex flex-wrap items-start justify-between gap-4 mb-4">
        <div className="min-w-0">
          <div className="eyebrow mb-1">{r.campaignName}</div>
          <h1 className="font-display text-[24px] leading-tight font-[650] break-all">{r.resource}</h1>
          <div className="flex flex-wrap items-center gap-2 mt-2 text-[13px] text-ink-2">
            <Tag tone={REVIEW_STATE[r.state].tone}>{REVIEW_STATE[r.state].label}</Tag>
            {r.state !== 'Submitted' && r.campaignState === 'Open' && <Tag tone={r.overdue ? 'bad' : 'neutral'}>{dueText(r.dueOn, today())}</Tag>}
            {r.campaignState === 'Closed' && r.state !== 'Submitted' && <Tag>Campaign closed</Tag>}
            <span>Owner: {r.ownerName}</span>
            {r.delegateName && <span>· Handed to {r.delegateName}</span>}
            {r.myRole && <span>· {ROLE_TEXT[r.myRole]}</span>}
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          {canDelegate && (
            <Button onClick={() => setDelegating(true)} disabled={busy}>
              <UserRound size={15} /> {r.delegateName ? 'Change who reviews' : adminReassign ? 'Reassign' : 'Hand to someone'}
            </Button>
          )}
          {canDecide && onSubmit && (
            <Button variant="primary" onClick={() => setConfirming(true)} disabled={busy || undecided > 0} title={undecided > 0 ? `${undecided} entries still need a decision` : undefined}>
              <CheckCircle2 size={15} /> Submit review
            </Button>
          )}
        </div>
      </div>

      {problem && <div className="mb-4"><Callout tone="bad" title="That didn't work">{problem}</Callout></div>}

      <div className="grid md:grid-cols-[1fr_320px] gap-4 mb-4">
        <Card>
          <div className="flex items-baseline justify-between gap-3 mb-2">
            <div className="text-[13px] text-ink-2">
              <b className="text-ink tnum">{r.decided}</b> of <b className="text-ink tnum">{r.decisionsRequired}</b> entries decided
            </div>
            <div className="text-[12.5px] text-ink-3 tnum">
              {r.remove > 0 && <>{r.remove} remove · </>}
              {r.investigate > 0 && <>{r.investigate} investigate · </>}
              {r.exception > 0 && <>{r.exception} exception</>}
            </div>
          </div>
          <Meter value={progress(r)} tone={r.state === 'Submitted' ? 'good' : 'accent'} label="Decisions made" />
          <p className="text-[13px] text-ink-2 mt-3">
            Below is everyone who could open files here when the campaign started on {fmtDateTime(detail.frozenAt)}. For each group or person, decide whether they still need it.
            Nothing changes in Windows or Active Directory when you decide: IT makes any removal you ask for.
          </p>
        </Card>
        <div className="space-y-2">
          {detail.notes.map((n) => (
            <Callout key={n} tone={n.startsWith('Access is computed') || n.includes('administrators manage') ? 'info' : 'warn'}>{n}</Callout>
          ))}
        </div>
      </div>

      {canDecide && selected.size > 0 && (
        <div className="sticky z-20 mb-3 flex flex-wrap items-center gap-2 rounded-lg border border-line bg-surface px-3 py-2" style={{ top: 'calc(env(safe-area-inset-top, 0px) + 64px)', boxShadow: 'var(--shadow-pop)' }}>
          <span className="text-[13px] font-medium">{selected.size} selected</span>
          <Button size="sm" variant="primary" onClick={() => setEditing(required.filter((i) => selected.has(i.id)))}>Decide for all selected</Button>
          <Button size="sm" variant="ghost" onClick={() => setSelected(new Set())}>Clear</Button>
        </div>
      )}

      <Card title={`Needs your decision (${required.length})`} pad={false}>
        {required.length === 0 ? (
          <p className="px-4 pb-4 text-[13.5px] text-ink-2">Only administrators and built-in system accounts have access here. There is nothing to decide; submit to record that.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-[13.5px] min-w-[860px]">
              <thead>
                <tr>
                  {canDecide && (
                    <Th className="w-8">
                      <input
                        type="checkbox"
                        className="size-4 accent-[var(--accent)]"
                        aria-label="Select all"
                        checked={selected.size === required.length}
                        onChange={(e) => setSelected(e.target.checked ? new Set(required.map((i) => i.id)) : new Set())}
                      />
                    </Th>
                  )}
                  <Th>Who</Th>
                  <Th>Can</Th>
                  <Th className="text-right">People</Th>
                  <Th>Decision</Th>
                  <Th />
                </tr>
              </thead>
              <tbody>
                {required.map((i) => (
                  <tr key={i.id} className={cx('border-t border-line', selected.has(i.id) && 'bg-accent-soft/40')}>
                    {canDecide && (
                      <Td>
                        <input type="checkbox" className="size-4 accent-[var(--accent)]" aria-label={`Select ${i.trusteeName}`} checked={selected.has(i.id)} onChange={() => toggle(i.id)} />
                      </Td>
                    )}
                    <Td>
                      <div className="font-medium break-all">{i.trusteeName}</div>
                      <div className="flex flex-wrap gap-1 mt-1">
                        <Tag>{TRUSTEE_KIND[i.trusteeKind] ?? i.trusteeKind}</Tag>
                        {i.inherited && <Tag title="Comes from a parent folder; removing it may need to happen there.">Inherited</Tag>}
                        {i.flags.map((f) => (
                          <Tag key={f} tone={FLAGS[f].tone} title={FLAGS[f].help}>{FLAGS[f].label}</Tag>
                        ))}
                      </div>
                    </Td>
                    <Td className="whitespace-nowrap">{i.rights}</Td>
                    <Td className="text-right tnum whitespace-nowrap">
                      <button className="text-accent hover:underline inline-flex items-center gap-1" onClick={() => setPeople(i)}>
                        <Users size={14} />
                        {fmtNum(i.usersCertain)}
                        {i.usersPossible > 0 && <span className="text-ink-3">+{fmtNum(i.usersPossible)}?</span>}
                      </button>
                      {i.usersWithOtherAccess > 0 && (
                        <div className="text-[11.5px] text-ink-3" title="These people would keep access through another entry if this one were removed.">
                          {fmtNum(i.usersWithOtherAccess)} also have it another way
                        </div>
                      )}
                    </Td>
                    <Td>
                      {i.decision ? (
                        <div>
                          <Tag tone={DECISIONS[i.decision.decision].tone}>{DECISIONS[i.decision.decision].label}</Tag>
                          {i.decision.exceptionExpiresOn && <span className="text-[12px] text-ink-3"> until {fmtDate(i.decision.exceptionExpiresOn)}</span>}
                          {i.decision.justification && <div className="text-[12px] text-ink-2 mt-0.5 max-w-[32ch] truncate" title={i.decision.justification}>{i.decision.justification}</div>}
                          <div className="text-[11.5px] text-ink-3">
                            {i.decision.decidedByName}
                            {i.revisions > 1 && <> · changed {i.revisions - 1}×</>}
                          </div>
                        </div>
                      ) : (
                        <span className="text-ink-3">Not decided</span>
                      )}
                    </Td>
                    <Td className="text-right">
                      {canDecide && (
                        <Button size="sm" onClick={() => setEditing([i])}>
                          {i.decision ? 'Change' : 'Decide'}
                        </Button>
                      )}
                    </Td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </Card>

      {other.length > 0 && (
        <details className="mt-4 bg-surface border border-line rounded-xl">
          <summary className="px-4 py-3 cursor-pointer text-[13px] font-semibold">Also on this folder, no decision needed ({other.length})</summary>
          <ul className="px-4 pb-3 space-y-1.5 text-[13px]">
            {other.map((i) => (
              <li key={i.id} className="flex flex-wrap items-center gap-2">
                <span className="font-medium">{i.trusteeName}</span>
                <Tag tone={i.effect === 'Deny' ? 'warn' : 'neutral'}>{i.effect === 'Deny' ? `Denied ${i.rights.toLowerCase()}` : i.rights}</Tag>
                <span className="text-ink-3">
                  {i.effect === 'Deny'
                    ? 'A deny entry takes access away; it is shown for completeness.'
                    : i.administrative
                      ? 'Administrators and system accounts that Windows needs.'
                      : 'A placeholder Windows fills in for whoever creates a file.'}
                </span>
              </li>
            ))}
          </ul>
        </details>
      )}

      {detail.events.length > 0 && (
        <div className="mt-4">
          <Card title="History">
            <ol className="space-y-2 text-[13px]">
              {detail.events.map((e, n) => (
                <li key={n}>
                  <div className="text-ink-2">{e.detail}</div>
                  <div className="text-[11.5px] text-ink-3">{e.actorName} · {fmtDateTime(e.at)}</div>
                </li>
              ))}
            </ol>
          </Card>
        </div>
      )}

      <details className="mt-4 text-[12.5px] text-ink-3">
        <summary className="cursor-pointer">Evidence seal</summary>
        <p className="mt-1">
          SHA-256 of the frozen review: <Unc>{detail.evidenceHash}</Unc>. Auditors can check it has not changed since the campaign started.
        </p>
      </details>

      {editing && onDecide && (
        <DecisionDrawer
          items={editing}
          busy={busy}
          onClose={() => setEditing(null)}
          onSave={(kind, justification, expires) =>
            run(async () => {
              await onDecide(editing.map((i) => ({ itemId: i.id, decision: kind, justification: justification || undefined, exceptionExpiresOn: kind === 'Exception' ? expires : undefined })));
              setEditing(null);
              setSelected(new Set());
            })
          }
        />
      )}

      <PeopleDrawer item={people} onClose={() => setPeople(undefined)} loadUsers={loadUsers} />

      <Drawer open={confirming} onClose={() => setConfirming(false)} title="Submit this review?" sub={r.resource} width={480}>
        <div className="space-y-3 text-[13.5px]">
          <p>
            You decided {r.decisionsRequired} {r.decisionsRequired === 1 ? 'entry' : 'entries'}: {r.decisionsRequired - r.remove - r.investigate - r.exception} keep, {r.remove} remove, {r.investigate} investigate, {r.exception} exception.
          </p>
          <p className="text-ink-2">
            Once submitted, the decisions are final and kept as audit evidence.
            Nothing changes in Windows until IT acts, and a later scan confirms each removal.
          </p>
          <div className="flex gap-2 pt-2">
            <Button
              variant="primary"
              disabled={busy}
              onClick={() =>
                run(async () => {
                  await onSubmit!();
                  setConfirming(false);
                })
              }
            >
              <ShieldCheck size={15} /> Yes, submit
            </Button>
            <Button variant="ghost" onClick={() => setConfirming(false)}>Not yet</Button>
          </div>
        </div>
      </Drawer>

      {onDelegate && peopleSearch && (
        <DelegateDrawer
          open={delegating}
          current={r.delegateName}
          exclude={[r.ownerSid]}
          search={peopleSearch}
          busy={busy}
          onClose={() => setDelegating(false)}
          onSave={(p) =>
            run(async () => {
              await onDelegate(p);
              setDelegating(false);
            })
          }
        />
      )}
    </>
  );
}

function DecisionDrawer({
  items,
  busy,
  onClose,
  onSave,
}: {
  items: ReviewItem[];
  busy: boolean;
  onClose: () => void;
  onSave: (kind: DecisionKind, justification: string, expires: string | undefined) => void;
}) {
  const first = items.length === 1 ? items[0].decision : null;
  const [kind, setKind] = useState<DecisionKind>(first?.decision ?? 'Keep');
  const [why, setWhy] = useState(first?.justification ?? '');
  const [until, setUntil] = useState(first?.exceptionExpiresOn ?? '');
  const [touched, setTouched] = useState(false);
  const problem = decisionProblem(kind, why, kind === 'Exception' ? until || undefined : undefined, today());
  const people = useMemo(() => items.reduce((a, i) => a + i.usersCertain + i.usersPossible, 0), [items]);
  return (
    <Drawer open onClose={onClose} title={items.length === 1 ? items[0].trusteeName : `${items.length} entries`} sub={`${items.length === 1 ? items[0].rights + ' · ' : ''}reaches ${fmtNum(people)} ${people === 1 ? 'person' : 'people'}`} width={520}>
      <form
        className="space-y-4"
        onSubmit={(e) => {
          e.preventDefault();
          setTouched(true);
          if (!problem) onSave(kind, why.trim(), until || undefined);
        }}
      >
        <fieldset>
          <legend className="text-[13px] font-medium mb-2">Decision</legend>
          <div className="grid grid-cols-2 gap-2">
            {KINDS.map((k) => (
              <label key={k} className={cx('rounded-lg border px-3 py-2 cursor-pointer', kind === k ? 'border-accent bg-accent-soft' : 'border-line hover:bg-surface-2')}>
                <input type="radio" name="decision" value={k} checked={kind === k} onChange={() => setKind(k)} className="sr-only" />
                <span className="block text-[13.5px] font-medium">{DECISIONS[k].label}</span>
                <span className="block text-[12px] text-ink-3">{DECISIONS[k].help}</span>
              </label>
            ))}
          </div>
        </fieldset>
        <label className="block">
          <span className="block text-[13px] font-medium mb-1">{DECISIONS[kind].needsReason ? 'Why (required)' : 'Note (optional)'}</span>
          <textarea className={cx(inputCls, 'h-24 py-2')} value={why} maxLength={MAX_TEXT} onChange={(e) => setWhy(e.target.value)} placeholder={kind === 'Remove' ? 'For example: moved to another team in June' : ''} />
        </label>
        {kind === 'Exception' && (
          <label className="block">
            <span className="block text-[13px] font-medium mb-1">Exception ends on</span>
            <input type="date" className={inputCls} value={until} min={today()} onChange={(e) => setUntil(e.target.value)} />
            <span className="block text-[12px] text-ink-3 mt-1">Within a year. It must be reviewed again by then.</span>
          </label>
        )}
        {touched && problem && <Callout tone="bad">{problem}</Callout>}
        {kind === 'Remove' && items.some((i) => i.usersWithOtherAccess > 0) && (
          <Callout tone="info">Some of these people also have access another way and will keep it. Open “People” to see which.</Callout>
        )}
        <div className="flex gap-2">
          <Button type="submit" variant="primary" disabled={busy}>Save decision</Button>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
        </div>
      </form>
    </Drawer>
  );
}

function PeopleDrawer({ item, onClose, loadUsers }: { item?: ReviewItem; onClose: () => void; loadUsers: (itemId: number, search?: string) => Promise<ReviewItemUser[]> }) {
  const [users, setUsers] = useState<ReviewItemUser[]>();
  const [search, setSearch] = useState('');
  const [error, setError] = useState<string>();
  useEffect(() => {
    if (!item) return;
    let live = true;
    const t = setTimeout(() => {
      loadUsers(item.id, search.trim() || undefined).then(
        (u) => live && (setUsers(u), setError(undefined)),
        (e) => live && setError(e instanceof Error ? e.message : String(e)),
      );
    }, 200);
    return () => {
      live = false;
      clearTimeout(t);
    };
  }, [item, search, loadUsers]);
  useEffect(() => {
    setUsers(undefined);
    setSearch('');
  }, [item]);
  if (!item) return null;
  return (
    <Drawer open onClose={onClose} title={`People reached by ${item.trusteeName}`} sub={`${fmtNum(item.usersCertain)} certain${item.usersPossible ? `, ${fmtNum(item.usersPossible)} possible` : ''} · as at the campaign start`} width={600}>
      <input className={cx(inputCls, 'mb-3')} placeholder="Filter by name or department" value={search} onChange={(e) => setSearch(e.target.value)} />
      {item.usersTruncated && <div className="mb-3"><Callout tone="warn">This entry reaches more people than Data Scope lists. The counts above are complete.</Callout></div>}
      {error && <Callout tone="bad">{error}</Callout>}
      {users && users.length === 0 && <p className="text-ink-3 text-[13px]">Nobody matches.</p>}
      <ul className="divide-y divide-line">
        {users?.map((u) => (
          <li key={u.sid} className="py-2">
            <div className="flex flex-wrap items-center gap-2">
              <span className="font-medium text-[13.5px]">{u.name}</span>
              {!u.enabled && <Tag>Disabled</Tag>}
              {!u.certain && <Tag tone="warn" title="Membership could not be fully resolved.">Possible</Tag>}
            </div>
            <div className="text-[12px] text-ink-3">{[u.title, u.department].filter(Boolean).join(' · ') || '—'}</div>
            <div className="text-[12px] text-ink-2 mt-0.5">Through {u.chain.slice(1).join(' → ') || 'a direct entry'}</div>
            {u.alsoVia && <div className="text-[12px] text-ink-2">Would keep access through {u.alsoVia}</div>}
          </li>
        ))}
      </ul>
    </Drawer>
  );
}

function DelegateDrawer({
  open,
  current,
  exclude,
  search,
  busy,
  onClose,
  onSave,
}: {
  open: boolean;
  current: string | null;
  exclude: string[];
  search: (q: string) => Promise<PersonRef[]>;
  busy: boolean;
  onClose: () => void;
  onSave: (p: PersonRef | null) => void;
}) {
  const [person, setPerson] = useState<PersonRef | null>(null);
  return (
    <Drawer open={open} onClose={onClose} title="Hand this review to someone" sub="They can decide and submit. You can still see and decide too." width={480}>
      <div className="space-y-3">
        <PersonPicker id="delegate" value={person} onChange={setPerson} search={search} exclude={exclude} placeholder="Type a colleague's name" />
        <p className="text-[12.5px] text-ink-3">They get an email with a link to Data Scope, where they sign in with Windows. The email itself cannot approve anything.</p>
        <div className="flex flex-wrap gap-2">
          <Button variant="primary" disabled={!person || busy} onClick={() => onSave(person)}>Hand over</Button>
          {current && (
            <Button disabled={busy} onClick={() => onSave(null)}>
              Take it back from {current}
            </Button>
          )}
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
        </div>
      </div>
    </Drawer>
  );
}
