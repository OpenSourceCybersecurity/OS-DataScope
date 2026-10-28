import { useMemo, useState } from 'react';
import { ChevronDown, ChevronRight } from 'lucide-react';
import { useApp } from '../api/store';
import { useNav } from '../App';
import { effectiveAccess, membersOf, resolveMembership } from '../domain/access';
import { effectiveClass, scopeOf } from '../domain/risk';
import { Card, PageHeader, Person, Tag, ago, inputCls, rightsLabel, Unc, cx } from '../ui/kit';
import { AccessExplain } from './AccessExplain';
import { useBackend } from '../api/backend';
import LiveAccess from './live/LiveAccess';

export default function AccessExplorer() {
  const backend = useBackend();
  return backend.mode === 'live' ? <LiveAccess /> : <DemoAccessExplorer />;
}

function DemoAccessExplorer() {
  const s = useApp();
  const { param } = useNav();
  const people = [...s.dir.values()].filter((p) => p.type === 'user' || p.type === 'group').sort((a, b) => (a.type === b.type ? a.displayName.localeCompare(b.displayName) : a.type === 'user' ? -1 : 1));
  const [sel, setSel] = useState(param && s.dir.has(param) ? param : people.find((p) => p.sam === 'ifrost')!.sid);
  const [q, setQ] = useState('');
  const list = people.filter((p) => (p.displayName + p.sam + (p.department ?? '')).toLowerCase().includes(q.toLowerCase()));
  const p = s.dir.get(sel)!;

  return (
    <>
      <PageHeader title="Who has access" sub="Pick a person or group to see every share they can open, and exactly why." />
      <div className="grid lg:grid-cols-[280px_1fr] gap-4 items-start">
        <Card pad={false} className="lg:sticky lg:top-20">
          <div className="p-3 border-b border-line">
            <input id="people-filter" className={inputCls} placeholder="Filter people and groups" value={q} onChange={(e) => setQ(e.target.value)} />
          </div>
          <ul className="max-h-[60vh] overflow-y-auto scroll-thin py-1">
            {list.map((x) => (
              <li key={x.sid}>
                <button onClick={() => setSel(x.sid)} className={cx('w-full text-left px-3 py-1.5 hover:bg-surface-2', sel === x.sid && 'bg-accent-soft')}>
                  <Person sid={x.sid} size={24} />
                </button>
              </li>
            ))}
          </ul>
        </Card>
        <div className="min-w-0">{p.type === 'user' ? <UserView sid={sel} /> : <GroupView sid={sel} onPick={setSel} />}</div>
      </div>
    </>
  );
}

function UserView({ sid }: { sid: string }) {
  const s = useApp();
  const p = s.dir.get(sid)!;
  const m = useMemo(() => resolveMembership(s.dir, sid), [s.dir, sid]);
  const [open, setOpen] = useState<string | null>(null);
  const rows = useMemo(
    () =>
      s.shares.flatMap((sh) => {
        const scopes = ['', ...sh.folders.filter((f) => f.inheritanceBroken).map((f) => f.path)];
        return scopes.map((scope) => {
          const e = effectiveAccess(s.dir, sh, sid, scope || undefined);
          const files = s.files.filter((f) => f.shareId === sh.id && scopeOf(sh, f.path) === scope);
          const sensitive = files.filter((f) => ['Restricted', 'Confidential'].includes(effectiveClass(f))).length;
          return { key: sh.id + scope, sh, scope, e, sensitive };
        });
      }).filter((r) => r.e.rights !== 'None'),
    [s.dir, s.shares, s.files, sid],
  );
  const groups = [...m.chains.entries()].filter(([g]) => g !== sid);
  const sensitiveTotal = rows.reduce((a, r) => a + r.sensitive, 0);
  const lastActivity = s.events.find((e) => e.principalSid === sid && e.certainty === 'confirmed');

  return (
    <div className="flex flex-col gap-4">
      <Card>
        <div className="flex flex-wrap items-start justify-between gap-4">
          <Person sid={sid} size={44} />
          <div className="flex flex-wrap gap-6 text-[13px]">
            <Stat label="Shares and folders" value={rows.length} />
            <Stat label="Sensitive files reachable" value={sensitiveTotal} />
            <Stat label="Groups (incl. nested)" value={groups.length} />
          </div>
        </div>
        <div className="flex flex-wrap gap-x-6 gap-y-1 mt-4 text-[12.5px] text-ink-3">
          <span>{p.sam}@northwind.local</span>
          <span>Last sign-in {ago(p.lastLogon)}</span>
          <span>Last file activity {lastActivity ? ago(lastActivity.time) : 'none in collected events'}</span>
          {m.cycles.length > 0 && <Tag tone="warn">Group nesting loop</Tag>}
        </div>
      </Card>
      <Card title="Can open" pad={false}>
        <ul className="divide-y divide-[var(--line)] border-t border-line">
          {rows.map((r) => (
            <li key={r.key}>
              <button onClick={() => setOpen(open === r.key ? null : r.key)} className="w-full text-left flex items-center gap-3 px-4 py-2.5 hover:bg-surface-2" aria-expanded={open === r.key}>
                {open === r.key ? <ChevronDown size={15} className="text-ink-3" /> : <ChevronRight size={15} className="text-ink-3" />}
                <span className="flex-1 min-w-0">
                  <span className="font-medium">{r.sh.name}{r.scope && <span className="text-ink-2"> \ {r.scope}</span>}</span>
                  <Unc><span className="block text-ink-3">{r.sh.unc}{r.scope && '\\' + r.scope}</span></Unc>
                </span>
                {r.sensitive > 0 && <Tag tone={r.sensitive > 3 ? 'bad' : 'warn'}>{r.sensitive} sensitive</Tag>}
                {r.e.confidence === 'partial' && <Tag tone="warn">Partial</Tag>}
                <span className="w-24 text-right font-medium text-[13px]">{rightsLabel(r.e.rights)}</span>
              </button>
              {open === r.key && (
                <div className="px-5 pb-4 pt-1">
                  <AccessExplain e={r.e} />
                </div>
              )}
            </li>
          ))}
          {!rows.length && <li className="px-4 py-6 text-ink-3">No access to any scanned share.</li>}
        </ul>
      </Card>
      <Card title="Group memberships">
        <ul className="flex flex-col gap-1.5 text-[13px]">
          {groups.map(([g, chain]) => (
            <li key={g} className="flex flex-wrap items-center gap-1.5">
              <span className="font-medium">{s.dir.get(g)?.displayName ?? g}</span>
              {chain.length > 2 && <span className="text-ink-3">via {chain.slice(1, -1).map((c) => s.dir.get(c)?.displayName ?? c).join(' → ')}</span>}
              {chain.length <= 2 && <span className="text-ink-3">direct</span>}
            </li>
          ))}
        </ul>
      </Card>
    </div>
  );
}

function GroupView({ sid, onPick }: { sid: string; onPick: (s: string) => void }) {
  const s = useApp();
  const g = s.dir.get(sid)!;
  const members = useMemo(() => membersOf(s.dir, sid), [s.dir, sid]);
  const nested = [...s.dir.values()].filter((p) => p.type === 'group' && p.memberOf.includes(sid));
  const parents = g.memberOf.map((x) => s.dir.get(x)).filter(Boolean);
  const onAcls = s.shares.flatMap((sh) => [
    ...sh.ntfsAcl.filter((a) => a.sid === sid).map((a) => ({ sh, where: 'NTFS (root)', a })),
    ...sh.shareAcl.filter((a) => a.sid === sid).map((a) => ({ sh, where: 'Share', a })),
    ...sh.folders.flatMap((f) => (f.ntfsAcl ?? []).filter((a) => a.sid === sid).map((a) => ({ sh, where: `NTFS: ${f.path}`, a }))),
  ]);
  return (
    <div className="flex flex-col gap-4">
      <Card>
        <Person sid={sid} size={44} />
        <div className="flex flex-wrap gap-6 mt-4">
          <Stat label="Effective members" value={members.length} />
          <Stat label="Nested groups" value={nested.length} />
          <Stat label="Permission entries" value={onAcls.length} />
        </div>
      </Card>
      <Card title="Appears on">
        {onAcls.length ? (
          <ul className="flex flex-col gap-1.5 text-[13px]">
            {onAcls.map((x, i) => (
              <li key={i} className="flex flex-wrap gap-2">
                <span className="font-medium">{x.sh.name}</span>
                <span className="text-ink-3">{x.where}</span>
                <span>{x.a.type === 'deny' ? 'Deny ' : ''}{rightsLabel(x.a.rights)}</span>
              </li>
            ))}
          </ul>
        ) : (
          <p className="text-ink-3 text-[13px]">Not on any scanned share directly{parents.length ? `; it grants access through ${parents.map((p) => p!.displayName).join(', ')}.` : '.'}</p>
        )}
      </Card>
      <Card title="Members">
        {!!nested.length && (
          <div className="mb-3 text-[13px]">
            <span className="text-ink-3">Nested groups: </span>
            {nested.map((n, i) => (
              <button key={n.sid} className="text-accent hover:underline" onClick={() => onPick(n.sid)}>
                {n.displayName}{i < nested.length - 1 ? ', ' : ''}
              </button>
            ))}
          </div>
        )}
        <div className="grid sm:grid-cols-2 gap-2">
          {members.map((m) => (
            <button key={m} onClick={() => onPick(m)} className="text-left rounded-md hover:bg-surface-2 p-1">
              <Person sid={m} size={26} />
            </button>
          ))}
        </div>
      </Card>
    </div>
  );
}

function Stat({ label, value }: { label: string; value: number }) {
  return (
    <div>
      <div className="font-display text-[22px] font-[650] leading-none tnum">{value}</div>
      <div className="text-[12px] text-ink-3 mt-1">{label}</div>
    </div>
  );
}
