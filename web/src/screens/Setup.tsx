import { useEffect, useState } from 'react';
import { AlertTriangle, Check, CircleDashed, HardDrive, Loader2, Plus, ShieldCheck, X } from 'lucide-react';
import { actions, useApp } from '../api/store';
import { useNav } from '../App';
import { useBackend } from '../api/backend';
import LiveSetup from './live/LiveSetup';
import { Button, Callout, Card, Drawer, Field, Meter, PageHeader, Tag, Td, Th, Unc, ago, cx, fmtNum, inputCls } from '../ui/kit';
import { shareStats } from './common';

export default function Setup() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LiveSetup />;
  return <DemoSetup />;
}

function DemoSetup() {
  const s = useApp();
  const [wizard, setWizard] = useState(false);
  return (
    <>
      <PageHeader
        title="Servers & scans"
        sub="File servers Data Scope reads from, scanning limits, and the read-only account it uses."
        actions={<Button variant="primary" onClick={() => setWizard(true)}><Plus size={16} /> Add file server</Button>}
      />
      <div className="grid lg:grid-cols-2 gap-4 mb-4">
        {s.servers.map((sv) => {
          const shares = s.shares.filter((x) => x.serverId === sv.id);
          const files = shares.reduce((a, sh) => a + shareStats(s, sh.id).files.length, 0);
          const scanning = shares.filter((sh) => sh.scan.state === 'running' || sh.scan.state === 'paused');
          return (
            <Card key={sv.id}>
              <div className="flex items-start gap-3">
                <div className="size-10 rounded-lg bg-surface-2 grid place-items-center text-ink-2"><HardDrive size={20} /></div>
                <div className="flex-1 min-w-0">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium">{sv.hostname}</span>
                    <Tag tone="good"><ShieldCheck size={12} /> Enrolled</Tag>
                    {scanning.length > 0 && <Tag tone="accent">Scanning</Tag>}
                  </div>
                  <div className="text-[12.5px] text-ink-3">{sv.fqdn} · {sv.os} · {sv.ip}</div>
                  <div className="flex flex-wrap gap-x-5 gap-y-1 mt-2.5 text-[13px]">
                    <span><b className="tnum">{shares.length}</b> shares</span>
                    <span><b className="tnum">{fmtNum(files)}</b> files indexed</span>
                    <span>Last contact {ago(sv.lastSeen)}</span>
                  </div>
                </div>
              </div>
            </Card>
          );
        })}
      </div>

      <div className="grid lg:grid-cols-[1.4fr_1fr] gap-4">
        <Card title="Scans" pad={false}>
          <div className="overflow-x-auto">
            <table className="w-full text-[13px] min-w-[560px]">
              <thead><tr><Th>Share</Th><Th>Coverage</Th><Th>Status</Th><Th /></tr></thead>
              <tbody>
                {s.shares.map((sh) => {
                  const st = shareStats(s, sh.id);
                  const cov = (st.files.length - st.unscanned) / Math.max(1, st.files.length);
                  const job = s.jobs.find((j) => j.shareId === sh.id);
                  return (
                    <tr key={sh.id} className="border-t border-line">
                      <Td><div className="font-medium">{sh.name}</div><Unc><span className="text-ink-3">{sh.unc}</span></Unc></Td>
                      <Td>
                        <div className="w-32"><Meter value={cov} tone={cov < 0.75 ? 'warn' : 'good'} /></div>
                        <div className="text-[12px] text-ink-3 mt-1 tnum">{Math.round(cov * 100)}% · {st.unscanned} not readable</div>
                      </Td>
                      <Td>
                        {job ? (
                          <span className="inline-flex items-center gap-2 tnum">{job.state === 'running' ? <Loader2 size={14} className="animate-spin" /> : null}{job.state === 'running' ? 'Scanning' : 'Paused'} {job.done}/{job.total}</span>
                        ) : (
                          <span>{sh.scan.state === 'partial' ? 'Partial' : 'Complete'} · {ago(sh.scan.lastScan)}</span>
                        )}
                      </Td>
                      <Td className="text-right">
                        {job ? (
                          <Button size="sm" onClick={() => actions.pauseScan(sh.id)}>{job.state === 'running' ? 'Pause' : 'Resume'}</Button>
                        ) : (
                          <Button size="sm" variant="ghost" onClick={() => actions.startScan(sh.id)}>Rescan</Button>
                        )}
                      </Td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </Card>
        <div className="flex flex-col gap-4">
          <Card title="Scanner account">
            <div className="text-[13px] flex flex-col gap-1.5">
              <div className="font-mono text-[12.5px]">NORTHWIND\svc_datascope</div>
              <div className="text-ink-2">Read-only on enrolled share roots, read on AD. Not a Domain Admin. Password stored with Windows DPAPI on the Data Scope server.</div>
            </div>
          </Card>
          <Card title="Scanning limits">
            <dl className="grid grid-cols-2 gap-y-2 text-[13px]">
              <dt className="text-ink-3">Parallel files</dt><dd>4 per server</dd>
              <dt className="text-ink-3">CPU cap</dt><dd>25% of one core per worker</dd>
              <dt className="text-ink-3">Scan window</dt><dd>Weeknights 8 pm to 6 am</dd>
              <dt className="text-ink-3">Max file size</dt><dd>50 MB (larger: metadata only)</dd>
              <dt className="text-ink-3">Junctions</dt><dd>Not followed</dd>
              <dt className="text-ink-3">Excluded</dt><dd className="font-mono text-[12px]">*.iso *.vhdx ~$* Thumbs.db</dd>
            </dl>
          </Card>
        </div>
      </div>
      {wizard && <AddServerWizard onClose={() => setWizard(false)} />}
    </>
  );
}

const STEPS = ['Server', 'Check access', 'Choose shares', 'Limits', 'Scan'];
const CHECKS: { label: string; result: 'ok' | 'warn'; detail: string }[] = [
  { label: 'Resolve name in DNS', result: 'ok', detail: '10.20.1.23' },
  { label: 'Reach SMB (TCP 445)', result: 'ok', detail: 'SMB 3.1.1, signing required' },
  { label: 'List shares as svc_datascope', result: 'ok', detail: '5 shares found' },
  { label: 'Read share roots', result: 'ok', detail: 'Read granted on Marketing and Facilities' },
  { label: 'Read permissions (ACLs)', result: 'ok', detail: 'READ_CONTROL granted' },
  { label: 'Look up users and groups in AD', result: 'ok', detail: 'LDAPS to nw-dc01:636' },
  { label: 'Read Security event log', result: 'warn', detail: 'Optional. Needed later for File activity (V1.5).' },
];
const FOUND = [
  { name: 'Marketing', path: 'D:\\Shares\\Marketing', system: false },
  { name: 'Facilities', path: 'D:\\Shares\\Facilities', system: false },
  { name: 'ADMIN$', path: 'C:\\Windows', system: true },
  { name: 'C$', path: 'C:\\', system: true },
  { name: 'print$', path: 'C:\\Windows\\system32\\spool\\drivers', system: true },
];

function AddServerWizard({ onClose }: { onClose: () => void }) {
  const s = useApp();
  const { go } = useNav();
  const [step, setStep] = useState(0);
  const [host, setHost] = useState('NW-FS03');
  const [checked, setChecked] = useState(0);
  const [picked, setPicked] = useState<string[]>(['Marketing', 'Facilities']);
  const [parallel, setParallel] = useState(4);
  const [windowed, setWindowed] = useState(false);
  const [created, setCreated] = useState<string[] | null>(null);

  useEffect(() => {
    if (step !== 1 || checked >= CHECKS.length) return;
    const t = setTimeout(() => setChecked((c) => c + 1), 380);
    return () => clearTimeout(t);
  }, [step, checked]);

  const jobs = created ? s.jobs.filter((j) => created.includes(j.shareId)) : [];
  const finished = created && !jobs.length;

  return (
    <Drawer open onClose={onClose} title="Add a file server" sub="Read-only. Data Scope never changes files, timestamps or permissions." width={600}>
      <ol className="flex gap-1 mb-6 text-[11.5px]">
        {STEPS.map((x, i) => (
          <li key={x} className="flex-1">
            <div className={cx('h-1.5 rounded-full', i <= step ? 'bg-accent' : 'bg-surface-3')} />
            <div className={cx('mt-1', i === step ? 'text-ink font-medium' : 'text-ink-3')}>{x}</div>
          </li>
        ))}
      </ol>

      {step === 0 && (
        <form className="flex flex-col gap-4" onSubmit={(e) => { e.preventDefault(); if (host.trim()) setStep(1); }}>
          <Field label="Server name" htmlFor="wiz-host" hint="A Windows Server joined to NORTHWIND.LOCAL.">
            <input id="wiz-host" className={inputCls} value={host} onChange={(e) => setHost(e.target.value)} />
          </Field>
          <Field label="Scanner account" htmlFor="wiz-acct" hint="Use a dedicated read-only account. Domain Admin is not needed or allowed.">
            <input id="wiz-acct" className={inputCls} defaultValue="NORTHWIND\svc_datascope" />
          </Field>
          <div><Button type="submit" variant="primary" disabled={!host.trim()}>Check access</Button></div>
        </form>
      )}

      {step === 1 && (
        <div className="flex flex-col gap-4">
          <ul className="flex flex-col gap-2.5">
            {CHECKS.map((c, i) => (
              <li key={c.label} className="flex items-start gap-3 text-[13.5px]">
                <span className="mt-0.5">
                  {i < checked ? (c.result === 'ok' ? <Check size={17} style={{ color: 'var(--s-good)' }} /> : <AlertTriangle size={17} style={{ color: 'var(--s-medium)' }} />) : i === checked ? <Loader2 size={17} className="animate-spin text-ink-3" /> : <CircleDashed size={17} className="text-ink-3" />}
                </span>
                <span>
                  <span className={i <= checked ? '' : 'text-ink-3'}>{c.label}</span>
                  {i < checked && <span className="block text-[12px] text-ink-3">{c.detail}</span>}
                </span>
              </li>
            ))}
          </ul>
          {checked >= CHECKS.length && (
            <>
              <Callout tone="good" title="Ready to scan">All required checks passed on {host.toUpperCase()}. One optional check can wait until you turn on file activity.</Callout>
              <div><Button variant="primary" onClick={() => setStep(2)}>Choose shares</Button></div>
            </>
          )}
        </div>
      )}

      {step === 2 && (
        <div className="flex flex-col gap-4">
          <ul className="border border-line rounded-lg divide-y divide-[var(--line)]">
            {FOUND.map((f) => (
              <li key={f.name}>
                <label className={cx('flex items-center gap-3 px-3 py-2.5', f.system ? 'opacity-55' : 'cursor-pointer hover:bg-surface-2')}>
                  <input
                    type="checkbox"
                    className="size-4 accent-[var(--accent)]"
                    disabled={f.system}
                    checked={picked.includes(f.name)}
                    onChange={(e) => setPicked((p) => (e.target.checked ? [...p, f.name] : p.filter((x) => x !== f.name)))}
                  />
                  <span className="flex-1">
                    <span className="block font-medium">{f.name}</span>
                    <span className="block font-mono text-[12px] text-ink-3">{f.path}</span>
                  </span>
                  {f.system && <Tag>System share, skipped</Tag>}
                </label>
              </li>
            ))}
          </ul>
          <div><Button variant="primary" disabled={!picked.length} onClick={() => setStep(3)}>Set limits</Button></div>
        </div>
      )}

      {step === 3 && (
        <div className="flex flex-col gap-4">
          <Field label={`Files scanned in parallel: ${parallel}`} htmlFor="wiz-par" hint="Lower is gentler on the file server.">
            <input id="wiz-par" type="range" min={1} max={8} value={parallel} onChange={(e) => setParallel(Number(e.target.value))} className="w-full accent-[var(--accent)]" />
          </Field>
          <fieldset className="flex flex-col gap-2">
            <legend className="text-[13px] font-medium mb-1">When to scan</legend>
            <label className="flex items-center gap-2 text-[13.5px]"><input type="radio" name="win" checked={!windowed} onChange={() => setWindowed(false)} className="accent-[var(--accent)]" /> Now, then weeknights 8 pm to 6 am</label>
            <label className="flex items-center gap-2 text-[13.5px]"><input type="radio" name="win" checked={windowed} onChange={() => setWindowed(true)} className="accent-[var(--accent)]" /> Only during the weeknight window</label>
          </fieldset>
          <Callout tone="info">Files over 50 MB are indexed by metadata only. Junctions and symbolic links are not followed. Scans can be paused at any time without losing progress.</Callout>
          <div><Button variant="primary" onClick={() => setStep(4)}>Review</Button></div>
        </div>
      )}

      {step === 4 && !created && (
        <div className="flex flex-col gap-4">
          <dl className="grid grid-cols-[140px_1fr] gap-y-2 text-[13.5px]">
            <dt className="text-ink-3">Server</dt><dd>{host.toUpperCase()}</dd>
            <dt className="text-ink-3">Shares</dt><dd>{picked.join(', ')}</dd>
            <dt className="text-ink-3">Parallel files</dt><dd>{parallel}</dd>
            <dt className="text-ink-3">Schedule</dt><dd>{windowed ? 'Weeknights only' : 'Start now'}</dd>
          </dl>
          <div><Button variant="primary" onClick={() => setCreated(actions.enrollServer({ hostname: host.trim(), shareNames: picked }))}>Enroll and start scan</Button></div>
        </div>
      )}

      {step === 4 && created && (
        <div className="flex flex-col gap-4">
          {created.map((id) => {
            const sh = s.shares.find((x) => x.id === id)!;
            const j = s.jobs.find((x) => x.shareId === id);
            const st = shareStats(s, id);
            return (
              <div key={id}>
                <div className="flex justify-between text-[13px] mb-1.5">
                  <span className="font-medium">{sh.name}</span>
                  <span className="text-ink-3 tnum">{j ? `${j.done} / ${j.total} files` : `Done · ${st.sensitive} sensitive`}</span>
                </div>
                <Meter value={j ? j.done / j.total : 1} tone={j ? 'accent' : 'good'} />
              </div>
            );
          })}
          {finished && (
            <>
              <Callout tone={created.some((id) => shareStats(s, id).sensitive) ? 'warn' : 'good'} title="First results are in">
                {created.reduce((a, id) => a + shareStats(s, id).sensitive, 0)} sensitive file(s) found. AllStaff can modify {created.length > 1 ? 'these shares' : 'this share'}, and {created.length > 1 ? 'they need owners' : 'it needs an owner'} before an access review.
              </Callout>
              <div className="flex gap-2">
                <Button variant="primary" onClick={() => { onClose(); go('risks'); }}>See risks</Button>
                <Button onClick={() => { onClose(); go('owners'); }}>Assign owners</Button>
              </div>
            </>
          )}
        </div>
      )}
      {step > 0 && !created && (
        <button className="mt-6 text-[13px] text-ink-3 hover:text-ink inline-flex items-center gap-1" onClick={() => setStep(step - 1)}>
          <X size={13} /> Back
        </button>
      )}
    </Drawer>
  );
}
