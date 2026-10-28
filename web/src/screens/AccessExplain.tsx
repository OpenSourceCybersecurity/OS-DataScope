import { ArrowRight, Ban, CircleHelp } from 'lucide-react';
import { useApp } from '../api/store';
import type { AclGrant, EffectiveAccess } from '../domain/access';
import { Tag, cx, rightsLabel } from '../ui/kit';

/** Plain-English explanation of why someone has access: membership chain + the ACE that grants it. */
export function AccessExplain({ e }: { e: EffectiveAccess }) {
  const shareGrants = e.grants.filter((g) => g.acl === 'share');
  const ntfsGrants = e.grants.filter((g) => g.acl === 'ntfs');
  return (
    <div className="flex flex-col gap-3 text-[13px]">
      <div className="grid grid-cols-3 gap-2">
        <Cell label="Share permission" value={rightsLabel(e.shareRights)} muted={e.shareRights !== e.rights} />
        <Cell label="NTFS permission" value={rightsLabel(e.ntfsRights)} muted={e.ntfsRights !== e.rights} />
        <Cell label="Effective" value={rightsLabel(e.rights)} strong />
      </div>
      {!!ntfsGrants.length && <Paths title="Granted on NTFS by" grants={ntfsGrants} />}
      {!!shareGrants.length && <Paths title="Allowed through the share by" grants={shareGrants} />}
      {!!e.denies.length && <Paths title="Removed by deny entries" grants={e.denies} deny />}
      {e.notes.map((n) => (
        <div key={n} className="flex gap-2 text-ink-2">
          <CircleHelp size={15} className="shrink-0 mt-0.5 text-ink-3" />
          {n}
        </div>
      ))}
      <div className="text-[12px] text-ink-3">
        {e.confidence === 'partial' ? (
          <Tag tone="warn">Partial result</Tag>
        ) : (
          <Tag tone="neutral">Likely access</Tag>
        )}{' '}
        Computed from the last permission and group snapshot. Not confirmed by a Windows access check.
      </div>
    </div>
  );
}

function Cell({ label, value, strong, muted }: { label: string; value: string; strong?: boolean; muted?: boolean }) {
  return (
    <div className={cx('rounded-lg px-3 py-2', strong ? 'bg-accent-soft' : 'bg-surface-2')}>
      <div className="text-[11.5px] text-ink-3">{label}</div>
      <div className={cx('font-semibold', muted && 'text-ink-3 line-through decoration-1')}>{value}</div>
    </div>
  );
}

function Paths({ title, grants, deny }: { title: string; grants: AclGrant[]; deny?: boolean }) {
  const { dir } = useApp();
  return (
    <div>
      <div className="eyebrow mb-1.5">{title}</div>
      <ul className="flex flex-col gap-1.5">
        {grants.map((g, i) => (
          <li key={i} className="flex flex-wrap items-center gap-1.5">
            {g.chain.map((sid, j) => (
              <span key={sid + j} className="inline-flex items-center gap-1.5">
                {j > 0 && <ArrowRight size={13} className="text-ink-3" />}
                <span
                  className={cx('rounded px-1.5 py-0.5 text-[12.5px]', j === 0 ? 'bg-surface-2' : 'bg-surface-3 font-medium')}
                  title={j > 0 ? 'member of' : undefined}
                >
                  {dir.get(sid)?.displayName ?? <span className="font-mono">{sid}</span>}
                </span>
              </span>
            ))}
            <span className="inline-flex items-center gap-1 ml-1 text-ink-2">
              {deny ? <Ban size={13} style={{ color: 'var(--s-critical)' }} /> : null}
              {deny ? 'Deny' : 'Allow'} {rightsLabel(g.ace.rights)}
              {g.ace.inherited && <span className="text-ink-3">(inherited)</span>}
              <span className="font-mono text-[11px] text-ink-3">{g.ace.mask}</span>
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
