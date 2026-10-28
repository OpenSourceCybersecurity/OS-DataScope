import { useEffect, useState } from 'react';
import { Download, Loader2, Search, UserRound, X } from 'lucide-react';
import { ApiError, api, type PersonItem, type PersonSearchResult, type SearchHit } from '../../api/client';
import { canManage, useBackend } from '../../api/backend';
import { useNav } from '../../App';
import { Button, Callout, Card, ClassPill, Empty, Field, LinkArrow, PageHeader, Tag, Td, Th, fmtDate, fmtDateTime, fmtNum, inputCls } from '../../ui/kit';
import { PERSON_SECTIONS, SOURCE_LABEL, personCsv, personCsvName, personItemTarget, personItemWhere, splitAddresses, splitNames } from './personSearch';

/**
 * Find everything about a person (D160), for a subject access or other personal-data request: what Data Scope already
 * holds about them, from names, sharing, ownership, the directory, activity and scan evidence. Platform admins only,
 * with a reason; every search is in the audit log.
 */
export default function LivePersonSearch() {
  const backend = useBackend();
  const { go } = useNav();
  const [picked, setPicked] = useState<SearchHit>();
  const [names, setNames] = useState('');
  const [addresses, setAddresses] = useState('');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [result, setResult] = useState<{ data: PersonSearchResult; reason: string }>();

  if (!canManage(backend)) {
    return (
      <>
        <PageHeader title="Find a person" />
        <Callout tone="info">Only platform admins can search for a person, because the results bring together everything Data Scope holds about them.</Callout>
      </>
    );
  }

  const run = async () => {
    setBusy(true);
    setErrors({});
    try {
      const data = await api.searchPerson({ sid: picked?.id, names: splitNames(names), addresses: splitAddresses(addresses), reason: reason.trim() });
      setResult({ data, reason: reason.trim() });
    } catch (e) {
      if (e instanceof ApiError && Object.keys(e.errors).length > 0) {
        setErrors(Object.fromEntries(Object.entries(e.errors).map(([k, v]) => [k.toLowerCase(), v[0]])));
      } else {
        setErrors({ form: e instanceof Error ? e.message : String(e) });
      }
    } finally {
      setBusy(false);
    }
  };

  const download = () => {
    if (!result || backend.mode !== 'live') return;
    const blob = new Blob([personCsv(result.data, result.reason, backend.me.name)], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = personCsvName(result.data.searchedAt);
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  };

  const err = (field: string) => errors[field] && <span className="block text-[12px] text-[var(--s-critical)] mt-1">{errors[field]}</span>;

  return (
    <div className="space-y-4">
      <PageHeader
        title="Find a person"
        sub="Everything Data Scope holds about one person, for a subject access or other personal-data request: their accounts, files named after them, what they own, and their file activity. Every search is in the audit log with who was searched for and why."
      />
      <Card title="Who to look for">
        <div className="grid md:grid-cols-2 gap-4 max-w-4xl">
          <div className="md:col-span-2">
            <Field label="Person in Active Directory (optional)" hint="Their display name, logon name and email addresses are searched too.">
              <PersonPicker picked={picked} onPick={setPicked} />
            </Field>
            {err('sid')}
          </div>
          <div>
            <Field label="Other names" htmlFor="ps-names" hint="One per line, as they might be written: Jo Bloggs, Bloggs Jo, Joanna Bloggs.">
              <textarea id="ps-names" className={inputCls} rows={3} value={names} onChange={(e) => setNames(e.target.value)} />
            </Field>
            {err('names')}
          </div>
          <div>
            <Field label="Email addresses" htmlFor="ps-addresses" hint="Including personal ones they wrote to you from.">
              <textarea id="ps-addresses" className={inputCls} rows={3} value={addresses} onChange={(e) => setAddresses(e.target.value)} />
            </Field>
            {err('addresses')}
          </div>
          <div className="md:col-span-2">
            <Field label="Reason" htmlFor="ps-reason" hint="Kept in the audit log, such as your request reference.">
              <input id="ps-reason" className={inputCls} value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Subject access request 2026-014" />
            </Field>
            {err('reason')}
          </div>
        </div>
        {errors.form && <div className="mt-3"><Callout tone="bad">{errors.form}</Callout></div>}
        <div className="mt-4 flex items-center gap-3">
          <Button variant="primary" onClick={run} disabled={busy}>
            {busy ? <Loader2 size={14} className="animate-spin" /> : <Search size={14} />} Search
          </Button>
          <span className="text-[12.5px] text-ink-3">Reads what the last scans and the directory sync recorded. File contents are not searched.</span>
        </div>
      </Card>
      {result && <Results r={result.data} onDownload={download} go={go} />}
    </div>
  );
}

/** Picks one person from the last directory sync, through the search box's own search. */
function PersonPicker({ picked, onPick }: { picked?: SearchHit; onPick: (h?: SearchHit) => void }) {
  const [q, setQ] = useState('');
  const [hits, setHits] = useState<SearchHit[]>([]);
  const term = q.trim();
  useEffect(() => {
    if (term.length < 2) {
      setHits([]);
      return;
    }
    const abort = new AbortController();
    const t = setTimeout(() => {
      api.search(term, { signal: abort.signal }).then(
        (r) => setHits(r.groups.find((g) => g.kind === 'User')?.items ?? []),
        () => undefined,
      );
    }, 200);
    return () => {
      clearTimeout(t);
      abort.abort();
    };
  }, [term]);

  if (picked) {
    return (
      <div className="flex items-center gap-2 h-9">
        <UserRound size={15} className="text-ink-3" />
        <span className="font-medium text-[13.5px]">{picked.title}</span>
        {picked.detail && <span className="text-[12.5px] text-ink-3">{picked.detail}</span>}
        <button className="ml-1 text-ink-3 hover:text-ink" aria-label="Clear the person" onClick={() => onPick(undefined)}>
          <X size={14} />
        </button>
      </div>
    );
  }
  return (
    <div className="relative">
      <input className={inputCls} aria-label="Find a person in Active Directory" placeholder="Start typing a name or logon name" value={q} onChange={(e) => setQ(e.target.value)} />
      {hits.length > 0 && (
        <ul className="absolute z-10 mt-1 w-full bg-surface border border-line rounded-lg shadow-lg overflow-hidden">
          {hits.map((h) => (
            <li key={h.id}>
              <button
                className="w-full text-left px-3 py-2 hover:bg-surface-2"
                onClick={() => {
                  onPick(h);
                  setQ('');
                  setHits([]);
                }}
              >
                <span className="text-[13.5px]">{h.title}</span>
                {h.disabled && <> <Tag tone="warn">Disabled</Tag></>}
                {h.detail && <span className="block text-[12px] text-ink-3">{h.detail}</span>}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function Results({ r, onDownload, go }: { r: PersonSearchResult; onDownload: () => void; go: ReturnType<typeof useNav>['go'] }) {
  const total = r.items.length;
  const a = r.activity;
  return (
    <div className="space-y-4">
      <Card
        title={`Found for ${[...r.searched.names.slice(0, 1), ...r.searched.addresses.slice(0, 1)].join(', ') || 'this person'}`}
        action={<Button size="sm" onClick={onDownload}><Download size={13} /> Download the list</Button>}
      >
        <p className="text-[13px] text-ink-2">
          {total === 0 ? 'Nothing was found.' : `${fmtNum(total)} ${total === 1 ? 'place' : 'places'} in ${r.sections.filter((s) => s.count > 0).length} parts below.`} Searched{' '}
          {[...r.searched.names, ...r.searched.addresses].map((t, i) => <span key={t}>{i > 0 && ', '}<b className="font-medium">{t}</b></span>)}, on {fmtDateTime(r.searchedAt)}.
        </p>
        {r.accounts.length > 0 && (
          <div className="mt-3 grid gap-2">
            {r.accounts.map((acc) => (
              <div key={acc.sid} className="rounded-lg border border-line p-3">
                <div className="flex flex-wrap items-center gap-2">
                  <UserRound size={15} className="text-ink-3" />
                  <span className="font-medium">{acc.displayName ?? acc.name}</span>
                  <span className="text-[12.5px] text-ink-3">{acc.name}{acc.mail ? ` · ${acc.mail}` : ''}</span>
                  {!acc.enabled && <Tag tone="warn">Disabled</Tag>}
                </div>
                <div className="text-[12.5px] text-ink-2 mt-1">
                  {[acc.title, acc.department].filter(Boolean).join(' · ')}
                  {acc.lastLogon && <> {acc.title || acc.department ? '· ' : ''}Last signed in {fmtDate(acc.lastLogon)}</>}
                </div>
                {acc.groups.length > 0 && <div className="text-[12.5px] text-ink-3 mt-1">Member of {acc.groups.join(', ')}</div>}
                <div className="mt-2">
                  <LinkArrow onClick={() => go('access', `user:${acc.sid}`)}>Everything this person can open, in Who has access</LinkArrow>
                </div>
              </div>
            ))}
          </div>
        )}
        <div className="mt-3 grid gap-2 text-[13px]">
          <div className="rounded-lg bg-surface-2 p-3">
            <div className="text-ink-3 text-[12px]">File share activity</div>
            {a.timedOut ? 'Took too long, left out.' : a.fileEvents === 0 ? 'None recorded.' : <>{fmtNum(a.fileEvents)} events, {fmtDate(a.fileFirst ?? undefined)} to {fmtDate(a.fileLast ?? undefined)}{a.fileShares.length > 0 && <span className="text-ink-3"> · {a.fileShares.slice(0, 3).join(', ')}</span>}</>}
          </div>
        </div>
        {a.fileEvents > 0 && (
          <div className="mt-2"><LinkArrow onClick={() => go('activity')}>See the events in File activity</LinkArrow></div>
        )}
      </Card>
      {PERSON_SECTIONS.map((s) => {
        const section = r.sections.find((x) => x.why === s.why);
        const items = r.items.filter((i) => i.why === s.why);
        if (!section || (items.length === 0 && !section.timedOut)) return null;
        return (
          <Card key={s.why} title={`${s.title} (${fmtNum(items.length)}${section.more ? '+' : ''})`} pad={false}>
            <p className="px-4 pt-3 text-[12.5px] text-ink-3 max-w-3xl">{s.about}</p>
            {section.timedOut ? (
              <div className="p-4"><Callout tone="warn">This part took too long and is left out. Search again with fewer or longer names.</Callout></div>
            ) : (
              <ItemTable items={items} go={go} />
            )}
            {section.more && <p className="px-4 pb-3 text-[12.5px] text-[var(--s-medium)]">Only the first {fmtNum(r.limit)} are listed. Search with a longer name to narrow it.</p>}
          </Card>
        );
      })}
      {total === 0 && (
        <Card>
          <Empty icon={<Search size={18} />} title="Nothing found">
            Nothing Data Scope recorded names this person. Their name may still be inside files: only text near data the scans flagged is kept.
          </Empty>
        </Card>
      )}
    </div>
  );
}

function ItemTable({ items, go }: { items: PersonItem[]; go: ReturnType<typeof useNav>['go'] }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-[13px] mt-2">
        <thead><tr><Th>Name</Th><Th>Where</Th><Th>Label</Th><Th>Detail</Th></tr></thead>
        <tbody>
          {items.map((i) => {
            const target = personItemTarget(i);
            return (
              <tr key={`${i.why}|${i.kind}|${i.id}`} className="border-t border-line align-top">
                <Td className="max-w-[280px]">
                  <button className="text-left hover:underline break-all" onClick={() => go(target.route, target.param)}>{i.name}</button>
                  <span className="block text-[12px] text-ink-3">{i.kind} · {SOURCE_LABEL[i.source]}</span>
                </Td>
                <Td className="max-w-[360px] break-all text-ink-2">{personItemWhere(i)}</Td>
                <Td>{i.label === 'Restricted' || i.label === 'Confidential' ? <ClassPill c={i.label} /> : <span className="text-ink-2">{i.label ?? '-'}</span>}</Td>
                <Td className="max-w-[320px] text-ink-2">{i.detail}</Td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
