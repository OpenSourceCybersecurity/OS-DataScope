import { useState } from 'react';
import { useApp } from '../api/store';
import { useNav } from '../App';
import { useBackend } from '../api/backend';
import LiveRisks from './live/LiveRisks';
import { SEVERITY_ORDER, effectiveClass, type Risk } from '../domain/risk';
import type { Severity } from '../domain/types';
import { Button, Callout, ClassPill, Drawer, PageHeader, Person, Segmented, SeverityPill, Unc, sevColor } from '../ui/kit';

export default function Risks() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LiveRisks />;
  return <DemoRisks />;
}

function DemoRisks() {
  const { risks, param } = useNav();
  const [sev, setSev] = useState<'all' | Severity>('all');
  const [open, setOpen] = useState<string | undefined>(param);
  const counts = Object.fromEntries(SEVERITY_ORDER.map((x) => [x, risks.filter((r) => r.severity === x).length])) as Record<Severity, number>;
  const shown = risks.filter((r) => sev === 'all' || r.severity === sev);
  const rules = [...new Set(shown.map((r) => r.rule))];
  const current = risks.find((r) => r.id === open);

  return (
    <>
      <PageHeader
        title="Risks"
        sub="Each risk combines how sensitive the data is, how many people can reach it, and how sure we are. Every one says why and what to do."
      />
      <div className="flex flex-wrap items-center gap-3 mb-4">
        <Segmented
          value={sev}
          onChange={setSev}
          options={[{ id: 'all', label: `All ${risks.length}` }, ...SEVERITY_ORDER.map((x) => ({ id: x, label: `${x} ${counts[x]}` }))]}
        />
      </div>
      <div className="flex flex-col gap-5">
        {rules.map((rule) => (
          <section key={rule}>
            <h2 className="text-[13px] font-semibold text-ink-2 mb-2">{rule}</h2>
            <ul className="bg-surface border border-line rounded-xl divide-y divide-[var(--line)] overflow-hidden">
              {shown.filter((r) => r.rule === rule).map((r) => (
                <li key={r.id}>
                  <button onClick={() => setOpen(r.id)} className="w-full text-left flex items-start gap-3 px-4 py-3 hover:bg-surface-2 relative">
                    <span className="absolute left-0 top-0 bottom-0 w-[3px]" style={{ background: sevColor(r.severity) }} />
                    <span className="w-[80px] shrink-0"><SeverityPill s={r.severity} /></span>
                    <span className="min-w-0 flex-1">
                      <span className="block font-medium">{r.title}</span>
                      <span className="block text-[13px] text-ink-2 mt-0.5 line-clamp-2">{r.why}</span>
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
      {current && <RiskDrawer r={current} onClose={() => setOpen(undefined)} />}
    </>
  );
}

function RiskDrawer({ r, onClose }: { r: Risk; onClose: () => void }) {
  const s = useApp();
  const { go, readOnly } = useNav();
  const share = s.shares.find((x) => x.id === r.shareId);
  const files = s.files.filter((f) => r.affectedFiles.includes(f.id));
  return (
    <Drawer open onClose={onClose} title={r.title} sub={<span className="inline-flex items-center gap-2"><SeverityPill s={r.severity} /> {r.rule}</span>} width={620}>
      <div className="flex flex-col gap-5">
        {share && (
          <div>
            <div className="eyebrow mb-1">Where</div>
            <Unc>{share.unc}{r.scope && '\\' + r.scope.replace(/\//g, '\\')}</Unc>
          </div>
        )}
        <div>
          <div className="eyebrow mb-1">Why it matters</div>
          <p className="text-ink-2 max-w-[62ch]">{r.why}</p>
        </div>
        <div>
          <div className="eyebrow mb-2">How the severity was set</div>
          <dl className="grid sm:grid-cols-3 gap-2 text-[13px]">
            {(['sensitivity', 'exposure', 'confidence'] as const).map((k) => (
              <div key={k} className="bg-surface-2 rounded-lg px-3 py-2">
                <dt className="text-[11.5px] text-ink-3 capitalize">{k}</dt>
                <dd>{r.factors[k]}</dd>
              </div>
            ))}
          </dl>
        </div>
        <Callout tone="info" title="What to do">
          {r.fix}
        </Callout>
        {!readOnly && share && (
          <div className="flex flex-wrap gap-2">
            <Button onClick={() => go('shares', r.shareId)}>Open share</Button>
          </div>
        )}
        {!!r.principals.length && (
          <div>
            <div className="eyebrow mb-2">Accounts involved</div>
            <div className="flex flex-col gap-2">
              {r.principals.slice(0, 8).map((p) => (
                <button key={p} className="text-left hover:bg-surface-2 rounded-md p-1 -m-1" onClick={() => go('access', p)}>
                  <Person sid={p} size={26} />
                </button>
              ))}
            </div>
          </div>
        )}
        {!!files.length && (
          <div>
            <div className="eyebrow mb-2">Files ({files.length})</div>
            <ul className="border border-line rounded-lg divide-y divide-[var(--line)]">
              {files.slice(0, 12).map((f) => (
                <li key={f.id}>
                  <button className="w-full text-left flex items-center gap-3 px-3 py-2 hover:bg-surface-2" onClick={() => go('discovery', f.id)}>
                    <ClassPill c={effectiveClass(f)} />
                    <span className="min-w-0">
                      <span className="block truncate text-[13px]">{f.name}</span>
                      <span className="block truncate text-[12px] text-ink-3">{f.findings.map((x) => x.category).join(', ') || f.scanStatus}</span>
                    </span>
                  </button>
                </li>
              ))}
            </ul>
            {files.length > 12 && <p className="text-[12px] text-ink-3 mt-1">and {files.length - 12} more</p>}
          </div>
        )}
      </div>
    </Drawer>
  );
}
