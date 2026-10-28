import { useEffect, useRef, useState } from 'react';
import { FileX2, FolderMinus, Loader2, Pencil, Plus, Power, PowerOff, Trash2 } from 'lucide-react';
import {
  ApiError,
  api,
  type ExclusionEffect,
  type ExclusionKind,
  type ScanExclusion,
  type ScanScopeInfo,
  type ScopeImpact,
  type ScopeShare,
} from '../../api/client';
import { canManage, useBackend } from '../../api/backend';
import { useNav } from '../../App';
import { Button, Callout, Card, LinkArrow, PageHeader, Segmented, Tag, Td, Th, Unc, ago, cx, fmtBytes, fmtDateTime, fmtNum, inputCls } from '../../ui/kit';
import { EFFECT_LABEL, actionLabel, excludeParam, formProblem, impactText, impactToConfirm, share as pct, shareImpactText } from './scope';
import { useData } from './useData';

/**
 * Scan scope and exclusions (D124). Built on share inclusion: whole shares are left out here (or from Shares), and folders
 * and file types can be left out too, each with a reason. Everything left out stays visible as a coverage gap; nothing
 * here makes data count as checked. Changes that would hide a lot need confirming, and every change is in the audit log.
 */
export default function LiveScanScope() {
  const admin = canManage(useBackend());
  const loaded = useData(() => api.scanScope(), []);
  const [info, setInfo] = useState<ScanScopeInfo>();
  useEffect(() => setInfo(loaded.data), [loaded.data]);
  const reload = async () => setInfo(await api.scanScope());

  return (
    <>
      <PageHeader
        title="Scan scope & exclusions"
        sub="What scans cover, and what an administrator has left out and why. Anything left out is a coverage gap, never clean data."
      />
      {loaded.error && <Callout tone="bad" title="Can't load the scan scope">{loaded.error}</Callout>}
      {!info && !loaded.error && <p className="text-[13px] text-ink-3 flex items-center gap-2"><Loader2 size={14} className="animate-spin" /> Loading…</p>}
      {info && (
        <div className="space-y-4">
          {!admin && <Callout title="View only">Only Data Scope administrators can change what is scanned.</Callout>}
          <CoverageStrip info={info} />
          <TwoKinds />
          <SharesCard info={info} admin={admin} onChanged={reload} />
          <ExclusionsCard info={info} admin={admin} onChanged={reload} />
          <HistoryCard info={info} />
        </div>
      )}
    </>
  );
}

function CoverageStrip({ info }: { info: ScanScopeInfo }) {
  const c = info.coverage;
  const shares = c.sharesIncluded + c.sharesExcluded;
  return (
    <div className="grid grid-cols-2 lg:grid-cols-4 gap-3">
      <Stat label="Shares scanned" value={`${fmtNum(c.sharesIncluded)} of ${fmtNum(shares)}`} sub={c.sharesExcluded ? `${fmtNum(c.sharesExcluded)} left out` : 'None left out'} warn={c.sharesExcluded > 0} />
      <Stat
        label="Folders not scanned"
        value={fmtNum(c.foldersNotScanned)}
        sub={`${fmtNum(c.folderExclusions)} folder ${c.folderExclusions === 1 ? 'exclusion' : 'exclusions'}`}
        warn={c.foldersNotScanned > 0 || c.folderExclusions > 0}
      />
      <Stat
        label="Files listed, contents not read"
        value={fmtNum(c.filesNotRead)}
        sub={`${fmtNum(c.contentExclusions)} content ${c.contentExclusions === 1 ? 'exclusion' : 'exclusions'}`}
        warn={c.filesNotRead > 0 || c.contentExclusions > 0}
      />
      <Stat label="Files in shares left out" value={fmtNum(c.filesInExcludedShares)} sub="As last seen; no longer updated" warn={c.filesInExcludedShares > 0} />
    </div>
  );
}

function Stat({ label, value, sub, warn }: { label: string; value: string; sub?: string; warn?: boolean }) {
  return (
    <div className="bg-surface border border-line rounded-xl px-4 py-3 min-w-0">
      <div className="text-[12px] text-ink-3">{label}</div>
      <div className="font-display text-[22px] font-[650] tnum mt-0.5" style={warn ? { color: 'var(--s-medium)' } : undefined}>{value}</div>
      {sub && <div className="text-[12px] text-ink-3">{sub}</div>}
    </div>
  );
}

/** The two ways to leave something out, said once so every other line can be short. */
function TwoKinds() {
  return (
    <div className="grid md:grid-cols-2 gap-3">
      {(['Skip', 'ListOnly'] as ExclusionEffect[]).map((e) => (
        <div key={e} className="rounded-xl border border-line bg-surface-2 px-4 py-3">
          <div className="text-[13.5px] font-medium flex items-center gap-2"><EffectTag effect={e} /></div>
          <p className="text-[12.5px] text-ink-2 mt-1">{EFFECT_LABEL[e].about}</p>
          <p className="text-[12px] text-ink-3 mt-1">
            {e === 'Skip'
              ? 'Shown as “Excluded by an administrator” in the share’s Not covered list, and the share counts as Partial.'
              : 'Each file shows “Excluded by an administrator” with sensitivity Unknown. Reports count them as not read.'}
          </p>
        </div>
      ))}
    </div>
  );
}

function EffectTag({ effect }: { effect: ExclusionEffect }) {
  return <Tag tone="warn">{EFFECT_LABEL[effect].title}</Tag>;
}

// ---------- Shares ----------

function SharesCard({ info, admin, onChanged }: { info: ScanScopeInfo; admin: boolean; onChanged: () => Promise<void> }) {
  const { param, go } = useNav();
  const linked = excludeParam(param);
  const [leaving, setLeaving] = useState<string | undefined>(() => (linked && info.shares.some((s) => s.id === linked && s.included) ? linked : undefined));
  const [problem, setProblem] = useState<string>();
  const [busy, setBusy] = useState<string>();
  const disk = info.shares.filter((s) => s.kind === 'Disk');
  const special = info.shares.length - disk.length;

  useEffect(() => {
    if (leaving) document.getElementById(`share-${leaving}`)?.scrollIntoView({ block: 'center' });
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const scanAgain = async (s: ScopeShare) => {
    setBusy(s.id);
    setProblem(undefined);
    try {
      await api.setIncluded(s.id, true);
      await onChanged();
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(undefined);
    }
  };

  return (
    <Card title="Shares" action={<LinkArrow onClick={() => go('shares')}>Open Shares</LinkArrow>}>
      <p className="text-[13px] text-ink-2 max-w-3xl">
        New disk shares are scanned when they are found. Leave a share out only when it holds nothing you need to govern, such as software installers.
        {special > 0 && ` ${fmtNum(special)} administrative or system ${special === 1 ? 'share is' : 'shares are'} never scanned.`}
      </p>
      {problem && <div className="mt-3"><Callout tone="bad" title="That didn't work">{problem}</Callout></div>}
      {disk.length === 0 ? (
        <p className="mt-3 text-[13px] text-ink-3">No shares found yet. Add a file server and run a scan under Servers &amp; scans.</p>
      ) : (
        <div className="mt-3 overflow-x-auto border border-line rounded-lg">
          <table className="w-full text-[13.5px] min-w-[720px]">
            <thead>
              <tr><Th>Share</Th><Th>Scanned</Th><Th className="text-right">Files</Th><Th className="text-right">Size</Th><Th>Why left out</Th>{admin && <Th />}</tr>
            </thead>
            <tbody>
              {disk.map((s) => (
                <ShareRow
                  key={s.id}
                  s={s}
                  admin={admin}
                  busy={busy === s.id}
                  open={leaving === s.id}
                  onOpen={() => (setLeaving(s.id), setProblem(undefined))}
                  onClose={() => setLeaving(undefined)}
                  onScanAgain={() => void scanAgain(s)}
                  onLeftOut={async () => (setLeaving(undefined), await onChanged())}
                />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

function ShareRow({ s, admin, busy, open, onOpen, onClose, onScanAgain, onLeftOut }: {
  s: ScopeShare;
  admin: boolean;
  busy: boolean;
  open: boolean;
  onOpen: () => void;
  onClose: () => void;
  onScanAgain: () => void;
  onLeftOut: () => Promise<void>;
}) {
  const cols = admin ? 6 : 5;
  return (
    <>
      <tr id={`share-${s.id}`} className={cx('border-t border-line align-top', open && 'bg-surface-2')}>
        <Td>
          <div className="font-medium">{s.name}</div>
          <Unc>{s.uncPath}</Unc>
        </Td>
        <Td>{s.included ? <Tag tone="good">Yes</Tag> : <Tag tone="warn">Left out</Tag>}</Td>
        <Td className="text-right tnum">{s.state === 'NotScanned' ? '—' : fmtNum(s.fileCount)}</Td>
        <Td className="text-right tnum">{s.state === 'NotScanned' ? '—' : fmtBytes(s.totalBytes)}</Td>
        <Td className="max-w-[320px]">
          {s.included ? (
            <span className="text-ink-3">—</span>
          ) : (
            <>
              <div className="break-words">{s.exclusionReason ?? <span className="text-ink-3">No reason recorded (left out before reasons were required)</span>}</div>
              {s.excludedByName && <div className="text-[12px] text-ink-3">{s.excludedByName}{s.excludedAt ? `, ${ago(s.excludedAt)}` : ''}</div>}
            </>
          )}
        </Td>
        {admin && (
          <Td className="text-right whitespace-nowrap">
            {s.included ? (
              !open && <Button size="sm" variant="ghost" onClick={onOpen}>Leave out…</Button>
            ) : (
              <Button size="sm" disabled={busy} onClick={onScanAgain}>{busy && <Loader2 size={14} className="animate-spin" />} Scan again</Button>
            )}
          </Td>
        )}
      </tr>
      {open && (
        <tr className="bg-surface-2">
          <td colSpan={cols} className="px-3 pb-3">
            <LeaveShareOut s={s} onCancel={onClose} onDone={onLeftOut} />
          </td>
        </tr>
      )}
    </>
  );
}

/** Leaving a whole share out: always with a reason, and confirmed when the share holds anything. */
function LeaveShareOut({ s, onCancel, onDone }: { s: ScopeShare; onCancel: () => void; onDone: () => Promise<void> }) {
  const [reason, setReason] = useState('');
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string>();
  const needsConfirm = s.state === 'NotScanned' || s.fileCount > 0;
  const blocked = reason.trim().length < 3 || (needsConfirm && !confirmed);

  const send = async (e: React.FormEvent) => {
    e.preventDefault();
    if (blocked) return;
    setBusy(true);
    setProblem(undefined);
    try {
      await api.setIncluded(s.id, false, reason.trim());
      await onDone();
    } catch (err) {
      setProblem(err instanceof Error ? err.message : String(err));
      setBusy(false);
    }
  };

  return (
    <form onSubmit={send} aria-label={`Leave ${s.name} out of scans`} className="rounded-lg border border-line bg-surface p-3 space-y-3 text-[13px]">
      <div>
        <div className="font-medium">Leave {s.uncPath} out of scans?</div>
        <p className="text-ink-2 mt-0.5">
          {shareImpactText(s.fileCount, s.totalBytes, s.state)} Its files, permissions and sensitive data stop being checked. What was already found stays as last seen
          and is no longer updated. To leave out only part of it, add a folder exclusion below instead.
        </p>
      </div>
      <label className="block">
        <span className="block font-medium mb-1">Why</span>
        <textarea rows={2} className={cx(inputCls, 'h-auto py-2')} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)} placeholder="For example: software installers only, no business data" />
      </label>
      {needsConfirm && (
        <label className="flex items-start gap-2 cursor-pointer">
          <input type="checkbox" className="size-4 mt-0.5 accent-[var(--accent)]" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
          <span>I understand nothing in this share will be scanned until it is turned back on.</span>
        </label>
      )}
      {problem && <Callout tone="bad" title="That didn't save">{problem}</Callout>}
      <div className="flex flex-wrap gap-2">
        <Button type="submit" size="sm" variant="primary" disabled={blocked || busy}>{busy && <Loader2 size={14} className="animate-spin" />} Leave out</Button>
        <Button size="sm" variant="ghost" disabled={busy} onClick={onCancel}>Cancel</Button>
      </div>
    </form>
  );
}

// ---------- Folder and file type exclusions ----------

function ExclusionsCard({ info, admin, onChanged }: { info: ScanScopeInfo; admin: boolean; onChanged: () => Promise<void> }) {
  const [adding, setAdding] = useState(false);
  const list = info.exclusions;
  return (
    <Card
      title="Folders and file types left out"
      action={admin && !adding ? <Button size="sm" onClick={() => setAdding(true)}><Plus size={14} /> Add an exclusion</Button> : undefined}
    >
      <p className="text-[13px] text-ink-2 max-w-3xl">
        Each one has a reason and shows what the last scans found in it. Turn one off to scan it again without losing the reason; changes apply from the next scan.
      </p>
      {adding && (
        <div className="mt-3">
          <AddExclusion info={info} onCancel={() => setAdding(false)} onAdded={async () => (setAdding(false), await onChanged())} />
        </div>
      )}
      {list.length === 0 ? (
        <p className="mt-3 text-[13px] text-ink-3">Nothing is left out. Every folder and file type on scanned shares is scanned.</p>
      ) : (
        <ul className="mt-3 divide-y divide-line border-y border-line">
          {list.map((x) => <ExclusionRow key={x.id} x={x} admin={admin} onChanged={onChanged} />)}
        </ul>
      )}
    </Card>
  );
}

function ExclusionRow({ x, admin, onChanged }: { x: ScanExclusion; admin: boolean; onChanged: () => Promise<void> }) {
  const [mode, setMode] = useState<'edit' | 'remove' | 'confirm-on'>();
  const [reason, setReason] = useState(x.reason);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string>();
  const [warnings, setWarnings] = useState<string[]>(x.impact.warnings);

  const run = async (work: () => Promise<unknown>) => {
    setBusy(true);
    setProblem(undefined);
    try {
      await work();
      setMode(undefined);
      await onChanged();
    } catch (e) {
      const impact = impactToConfirm(e);
      if (impact) {
        setWarnings(impact.warnings);
        setMode('confirm-on');
      } else setProblem(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  const turnOn = (confirm: boolean) => run(() => api.updateExclusion(x.id, { enabled: true, confirm }));
  const Icon = x.kind === 'Folder' ? FolderMinus : FileX2;

  return (
    <li className={cx('py-3 grid gap-2 md:grid-cols-[minmax(0,1fr)_auto] md:gap-6', !x.enabled && 'opacity-80')}>
      <div className="min-w-0 space-y-1">
        <div className="flex flex-wrap items-center gap-2">
          <Icon size={16} className="text-ink-3 shrink-0" />
          <span className="font-mono text-[12.5px] break-all">{x.target}</span>
          <EffectTag effect={x.effect} />
          {!x.enabled && <Tag>Off: scanned normally</Tag>}
        </div>
        {mode === 'edit' ? (
          <form
            className="flex flex-wrap items-start gap-2"
            onSubmit={(e) => {
              e.preventDefault();
              if (reason.trim().length >= 3) void run(() => api.updateExclusion(x.id, { reason: reason.trim() }));
            }}
          >
            <textarea aria-label="Why" rows={2} maxLength={500} className={cx(inputCls, 'h-auto py-2 flex-1 min-w-[16rem]')} value={reason} onChange={(e) => setReason(e.target.value)} />
            <Button type="submit" size="sm" variant="primary" disabled={busy || reason.trim().length < 3 || reason.trim() === x.reason}>Save</Button>
            <Button size="sm" variant="ghost" disabled={busy} onClick={() => (setMode(undefined), setReason(x.reason))}>Cancel</Button>
          </form>
        ) : (
          <p className="text-[13px]"><span className="text-ink-3">Why: </span>{x.reason}</p>
        )}
        <p className="text-[12.5px] text-ink-2">
          {x.enabled ? '' : 'While off, this would cover: '}
          {impactText(x.impact, x.kind, x.extension)}
        </p>
        <p className="text-[12px] text-ink-3">
          Added by {x.createdByName} on {fmtDateTime(x.createdAt)}
          {x.updatedAt !== x.createdAt && `; last changed by ${x.updatedByName}, ${ago(x.updatedAt)}`}.
        </p>
        {mode === 'remove' && (
          <div className="rounded-lg border border-line bg-surface-2 p-3 text-[13px] space-y-2">
            <p>Remove this exclusion? The next scan covers it again. The audit log keeps what it was and why.</p>
            <div className="flex gap-2">
              <Button size="sm" variant="primary" disabled={busy} onClick={() => void run(() => api.removeExclusion(x.id))}>Remove</Button>
              <Button size="sm" variant="ghost" disabled={busy} onClick={() => setMode(undefined)}>Cancel</Button>
            </div>
          </div>
        )}
        {mode === 'confirm-on' && (
          <div className="space-y-2">
            <Callout tone="warn" title="This leaves out a lot">
              <ul className="list-disc pl-5 space-y-0.5">{warnings.map((w) => <li key={w}>{w}</li>)}</ul>
            </Callout>
            <div className="flex gap-2">
              <Button size="sm" variant="primary" disabled={busy} onClick={() => void turnOn(true)}>Turn on anyway</Button>
              <Button size="sm" variant="ghost" disabled={busy} onClick={() => setMode(undefined)}>Cancel</Button>
            </div>
          </div>
        )}
        {problem && <Callout tone="bad" title="That didn't save">{problem}</Callout>}
      </div>
      {admin && !mode && (
        <div className="flex flex-wrap md:flex-col md:items-end gap-1.5">
          {x.enabled ? (
            <Button size="sm" variant="ghost" disabled={busy} onClick={() => void run(() => api.updateExclusion(x.id, { enabled: false }))}><PowerOff size={14} /> Turn off</Button>
          ) : (
            <Button size="sm" variant="ghost" disabled={busy} onClick={() => (x.impact.large ? (setWarnings(x.impact.warnings), setMode('confirm-on')) : void turnOn(false))}><Power size={14} /> Turn on</Button>
          )}
          <Button size="sm" variant="ghost" disabled={busy} onClick={() => setMode('edit')}><Pencil size={14} /> Change why</Button>
          <Button size="sm" variant="ghost" disabled={busy} onClick={() => setMode('remove')}><Trash2 size={14} /> Remove</Button>
        </div>
      )}
    </li>
  );
}

interface Form {
  kind: ExclusionKind;
  effect: ExclusionEffect;
  shareId: string;
  path: string;
  extension: string;
  reason: string;
}

/** Adding an exclusion: the target, what it does, why, and a preview of what it would hide before it is saved. */
function AddExclusion({ info, onCancel, onAdded }: { info: ScanScopeInfo; onCancel: () => void; onAdded: () => Promise<void> }) {
  const shares = info.shares.filter((s) => s.kind === 'Disk' && s.included);
  const [f, setF] = useState<Form>({ kind: 'Folder', effect: 'Skip', shareId: '', path: '', extension: '', reason: '' });
  const [impact, setImpact] = useState<ScopeImpact>();
  const [impactProblem, setImpactProblem] = useState<string>();
  const [checking, setChecking] = useState(false);
  const [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [problem, setProblem] = useState<string>();
  const seq = useRef(0);
  const set = (patch: Partial<Form>) => {
    setF((x) => ({ ...x, ...patch }));
    setErrors({});
    if (!('reason' in patch)) setConfirmed(false);
  };
  const share = shares.find((s) => s.id === f.shareId);
  const targetReady = f.kind === 'Folder' ? !!f.shareId && f.path.trim().replace(/[\\/]/g, '') !== '' : f.extension.trim().replace(/^\*?\./, '') !== '';

  // Preview what the exclusion would cover, a moment after typing stops.
  useEffect(() => {
    const mine = ++seq.current;
    setImpact(undefined);
    setImpactProblem(undefined);
    if (!targetReady) return;
    setChecking(true);
    const t = setTimeout(async () => {
      try {
        const i = await api.scopeImpact({ kind: f.kind, effect: f.kind === 'FileType' ? 'ListOnly' : f.effect, shareId: f.shareId || null, path: f.kind === 'Folder' ? f.path : undefined, extension: f.kind === 'FileType' ? f.extension : undefined });
        if (mine === seq.current) setImpact(i);
      } catch (e) {
        if (mine === seq.current) setImpactProblem(e instanceof Error ? e.message : String(e));
      } finally {
        if (mine === seq.current) setChecking(false);
      }
    }, 400);
    return () => clearTimeout(t);
  }, [f.kind, f.effect, f.shareId, f.path, f.extension, targetReady]);

  const blockedBy = formProblem(f, impact, confirmed) ?? (impactProblem ? impactProblem : checking ? 'Checking what this covers…' : undefined);

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    if (blockedBy) return;
    setBusy(true);
    setProblem(undefined);
    setErrors({});
    try {
      await api.addExclusion({
        kind: f.kind,
        effect: f.kind === 'FileType' ? 'ListOnly' : f.effect,
        shareId: f.shareId || null,
        path: f.kind === 'Folder' ? f.path : undefined,
        extension: f.kind === 'FileType' ? f.extension : undefined,
        reason: f.reason.trim(),
        confirm: confirmed,
      });
      await onAdded();
    } catch (err) {
      const i = impactToConfirm(err);
      if (i) {
        setImpact(i);
        setConfirmed(false);
      } else if (err instanceof ApiError && Object.keys(err.errors).length) setErrors(err.errors);
      else setProblem(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
    }
  };
  const fieldError = (k: string) => errors[k]?.[0];

  return (
    <form onSubmit={save} aria-label="Add an exclusion" className="rounded-lg border border-line bg-surface-2 p-3 space-y-3 text-[13px]">
      <Segmented<ExclusionKind>
        value={f.kind}
        onChange={(kind) => set({ kind, effect: kind === 'FileType' ? 'ListOnly' : 'Skip' })}
        options={[{ id: 'Folder', label: 'A folder' }, { id: 'FileType', label: 'A file type' }]}
      />
      {f.kind === 'Folder' ? (
        <div className="grid gap-3 md:grid-cols-[minmax(0,16rem)_minmax(0,1fr)]">
          <label className="block min-w-0">
            <span className="block font-medium mb-1">Share</span>
            <select className={inputCls} value={f.shareId} onChange={(e) => set({ shareId: e.target.value })} aria-invalid={!!fieldError('shareId') || undefined}>
              <option value="">Choose a share</option>
              {shares.map((s) => <option key={s.id} value={s.id}>{s.uncPath}</option>)}
            </select>
            {fieldError('shareId') && <span className="block mt-1 text-[12.5px] text-[var(--s-critical)]">{fieldError('shareId')}</span>}
          </label>
          <label className="block min-w-0">
            <span className="block font-medium mb-1">Folder inside the share</span>
            <input className={cx(inputCls, 'font-mono text-[12.5px]')} value={f.path} maxLength={1024} onChange={(e) => set({ path: e.target.value })} placeholder="Archive\2015" aria-invalid={!!fieldError('path') || undefined} />
            <span className="block mt-1 text-[12px] text-ink-3 break-all">
              {fieldError('path') ? <span className="text-[var(--s-critical)]">{fieldError('path')}</span> : share && f.path.trim() ? `${share.uncPath}\\${f.path.trim().replace(/^[\\/]+|[\\/]+$/g, '').replace(/\//g, '\\')} and everything below it` : 'One folder and everything below it. No wildcards.'}
            </span>
          </label>
        </div>
      ) : (
        <div className="grid gap-3 md:grid-cols-[minmax(0,10rem)_minmax(0,16rem)]">
          <label className="block min-w-0">
            <span className="block font-medium mb-1">File type</span>
            <input className={cx(inputCls, 'font-mono text-[12.5px]')} value={f.extension} maxLength={34} onChange={(e) => set({ extension: e.target.value })} placeholder=".bak" aria-invalid={!!fieldError('extension') || undefined} />
            {fieldError('extension') && <span className="block mt-1 text-[12.5px] text-[var(--s-critical)]">{fieldError('extension')}</span>}
          </label>
          <label className="block min-w-0">
            <span className="block font-medium mb-1">On</span>
            <select className={inputCls} value={f.shareId} onChange={(e) => set({ shareId: e.target.value })}>
              <option value="">Every scanned share</option>
              {shares.map((s) => <option key={s.id} value={s.id}>{s.uncPath}</option>)}
            </select>
          </label>
        </div>
      )}

      <fieldset>
        <legend className="font-medium mb-1">What happens there</legend>
        {f.kind === 'Folder' ? (
          <div className="grid md:grid-cols-2 gap-2">
            {(['Skip', 'ListOnly'] as ExclusionEffect[]).map((e) => (
              <label key={e} className={cx('flex items-start gap-2 rounded-lg border p-2.5 cursor-pointer bg-surface', f.effect === e ? 'border-accent' : 'border-line')}>
                <input type="radio" name="effect" className="mt-0.5 accent-[var(--accent)]" checked={f.effect === e} onChange={() => set({ effect: e })} />
                <span>
                  <span className="block font-medium">{EFFECT_LABEL[e].title}</span>
                  <span className="block text-[12.5px] text-ink-2">{EFFECT_LABEL[e].about}</span>
                </span>
              </label>
            ))}
          </div>
        ) : (
          <p className="text-ink-2">
            <EffectTag effect="ListOnly" /> Files of this type are still listed with their permissions; only their contents are never opened, so sensitive data in them is not found.
          </p>
        )}
      </fieldset>

      <label className="block">
        <span className="block font-medium mb-1">Why</span>
        <textarea rows={2} maxLength={500} className={cx(inputCls, 'h-auto py-2')} value={f.reason} onChange={(e) => set({ reason: e.target.value })} placeholder="For example: backup copies of the accounting database, checked by the vendor" aria-invalid={!!fieldError('reason') || undefined} />
        {fieldError('reason') && <span className="block mt-1 text-[12.5px] text-[var(--s-critical)]">{fieldError('reason')}</span>}
      </label>

      {targetReady && (
        <div className="rounded-lg border border-line bg-surface p-3 space-y-2" aria-live="polite">
          <div className="font-medium">What this leaves out</div>
          {checking && !impact && <p className="text-ink-3 flex items-center gap-2"><Loader2 size={14} className="animate-spin" /> Checking the last scans…</p>}
          {impactProblem && <p className="text-[var(--s-critical)]">{impactProblem}</p>}
          {impact && (
            <>
              <p>{impactText(impact, f.kind, f.extension.trim().replace(/^\*?\./, '').toLowerCase())}</p>
              {impact.notes.map((n) => <p key={n} className="text-ink-3">{n}</p>)}
              {impact.warnings.length > 0 && (
                <Callout tone="warn" title={impact.large ? 'This leaves out a lot' : 'Worth knowing'}>
                  <ul className="list-disc pl-5 space-y-0.5">{impact.warnings.map((w) => <li key={w}>{w}</li>)}</ul>
                </Callout>
              )}
              {impact.large && (
                <label className="flex items-start gap-2 cursor-pointer">
                  <input type="checkbox" className="size-4 mt-0.5 accent-[var(--accent)]" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} />
                  <span>
                    I understand {pct(impact.files, impact.totalFiles) ? `${pct(impact.files, impact.totalFiles)} of the files ` : 'these files '}
                    will not be checked for sensitive data, and that this shows as a coverage gap.
                  </span>
                </label>
              )}
            </>
          )}
        </div>
      )}

      {problem && <Callout tone="bad" title="That didn't save">{problem}</Callout>}
      <div className="flex flex-wrap items-center gap-2">
        <Button type="submit" size="sm" variant="primary" disabled={!!blockedBy || busy}>{busy ? <Loader2 size={14} className="animate-spin" /> : <Plus size={14} />} Add exclusion</Button>
        <Button size="sm" variant="ghost" disabled={busy} onClick={onCancel}>Cancel</Button>
        {blockedBy && !busy && <span className="text-[12.5px] text-ink-3">{blockedBy}</span>}
      </div>
    </form>
  );
}

// ---------- History ----------

function HistoryCard({ info }: { info: ScanScopeInfo }) {
  return (
    <Card title="History">
      <p className="text-[13px] text-ink-2">Every change to what is scanned, newest first. The full record is in the audit log.</p>
      {info.history.length === 0 ? (
        <p className="mt-3 text-[13px] text-ink-3">No changes yet.</p>
      ) : (
        <div className="mt-3 overflow-x-auto">
          <table className="w-full text-[13px] min-w-[640px]">
            <thead><tr><Th>When</Th><Th>Who</Th><Th>What</Th><Th>Where</Th><Th>Why</Th></tr></thead>
            <tbody>
              {info.history.map((h, i) => (
                <tr key={i} className="border-t border-line align-top">
                  <Td className="whitespace-nowrap text-ink-2">{fmtDateTime(h.at)}</Td>
                  <Td>{h.actorName}</Td>
                  <Td>{actionLabel(h.action)}</Td>
                  <Td className="font-mono text-[12px] break-all max-w-[260px]">{h.target}</Td>
                  <Td className="break-words max-w-[280px]">{h.reason ?? <span className="text-ink-3">—</span>}</Td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

