import { useEffect, useState } from 'react';
import { Download, Loader2, Pencil, Power, PowerOff, Printer } from 'lucide-react';
import { ApiError, api, type ComplianceStandard, type ComplianceStandards } from '../../api/client';
import { canManage, canReadReviews, canReadSettings, useBackend } from '../../api/backend';
import type { FindingCategory } from '../../domain/types';
import { useNav } from '../../App';
import { Button, ButtonLink, Callout, Card, LinkArrow, PageHeader, Segmented, Tag, Td, Th, ago, cx, fmtDateTime, fmtNum, inputCls } from '../../ui/kit';
import { ACTION_LABEL, changeProblem, countsText, customised } from './compliance';
import { useData } from './useData';

/**
 * Compliance standards (D126): which standards apply here and which kinds of data count towards each. Data Scope tags
 * the files, folders and shares holding that data so Sensitive data and the Exposure map can be narrowed to a standard.
 * It shows evidence and gaps; it never says anything is compliant.
 */
export default function LiveCompliance() {
  const backend = useBackend();
  const admin = canManage(backend);
  const loaded = useData(() => api.complianceStandards(), []);
  const [info, setInfo] = useState<ComplianceStandards>();
  const [version, setVersion] = useState(0);
  useEffect(() => setInfo(loaded.data), [loaded.data]);
  const reload = async () => {
    setInfo(await api.complianceStandards());
    setVersion((v) => v + 1);
  };
  const { go } = useNav();

  return (
    <>
      <PageHeader
        title="Compliance standards"
        sub="Turn on the standards that apply to your organisation. Data Scope tags the files, folders and shares holding the data each one covers. It shows the evidence and the gaps; it never says you are compliant."
      />
      {loaded.error && <Callout tone="bad" title="Can't load compliance standards">{loaded.error}</Callout>}
      {!info && !loaded.error && <p className="text-[13px] text-ink-3 flex items-center gap-2"><Loader2 size={14} className="animate-spin" /> Loading…</p>}
      {info && (
        <div className="space-y-4">
          {!admin && <Callout title="View only">Only Data Scope administrators can turn standards on or change what counts.</Callout>}
          {info.filesNotRead > 0 && (
            <Callout tone="warn" title={`${fmtNum(info.filesNotRead)} ${info.filesNotRead === 1 ? 'file was' : 'files were'} not read`}>
              {info.filesNotRead === 1 ? 'It may hold data under these standards. It is never counted as clean.' : 'Any of them may hold data under these standards. They are never counted as clean.'}{' '}
              <button className="underline underline-offset-2" onClick={() => go('discovery', 'unread')}>See files not read</button>
            </Callout>
          )}
          {info.standards.map((s) => (
            <StandardCard key={s.id} s={s} all={info.categories} admin={admin} evidence={canReadReviews(backend)} onChanged={reload} />
          ))}
          {canReadSettings(backend) && <HistoryCard version={version} />}
        </div>
      )}
    </>
  );
}

type Editing = { mode: 'on' | 'off' | 'change'; categories: FindingCategory[]; minConfidence: 'Medium' | 'High'; reason: string };

function StandardCard({ s, all, admin, evidence, onChanged }: { s: ComplianceStandard; all: FindingCategory[]; admin: boolean; evidence: boolean; onChanged: () => Promise<void> }) {
  const { go } = useNav();
  const [edit, setEdit] = useState<Editing>();
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string>();
  const start = (mode: Editing['mode']) => {
    setProblem(undefined);
    setEdit({ mode, categories: [...s.categories], minConfidence: s.minConfidence, reason: mode === 'change' ? s.reason ?? '' : '' });
  };
  const save = async () => {
    if (!edit) return;
    const local = edit.mode === 'off' ? (edit.reason.trim().length < 3 ? 'Say why, so others know later.' : undefined) : changeProblem(edit);
    if (local) return setProblem(local);
    setBusy(true);
    setProblem(undefined);
    try {
      await api.setComplianceStandard(
        s.id,
        edit.mode === 'off'
          ? { enabled: false, reason: edit.reason.trim() }
          : { enabled: true, categories: edit.categories, minConfidence: edit.minConfidence, reason: edit.reason.trim() },
      );
      setEdit(undefined);
      await onChanged();
    } catch (e) {
      setProblem(e instanceof ApiError ? Object.values(e.errors).flat()[0] ?? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  const c = s.counts;

  return (
    <Card
      title={
        <span className="flex flex-wrap items-center gap-2">
          {s.name}
          <Tag tone={s.enabled ? 'accent' : 'neutral'}>{s.enabled ? 'On' : 'Off'}</Tag>
          {s.enabled && customised(s) && <Tag tone="neutral">Changed from the defaults</Tag>}
        </span>
      }
      action={
        admin && !edit ? (
          <div className="flex gap-2">
            {s.enabled && <Button size="sm" onClick={() => start('change')}><Pencil size={14} /> Change</Button>}
            <Button size="sm" variant={s.enabled ? 'secondary' : 'primary'} onClick={() => start(s.enabled ? 'off' : 'on')}>
              {s.enabled ? <><PowerOff size={14} /> Turn off</> : <><Power size={14} /> Turn on</>}
            </Button>
          </div>
        ) : undefined
      }
    >
      <p className="text-[13px] text-ink-2 max-w-3xl">{s.summary}</p>

      {s.enabled && c && (
        <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2">
          <span className="text-[13.5px] font-medium tnum">{countsText(c)}</span>
          {c.files > 0 && (
            <span className="text-[12.5px] text-ink-3 tnum">
              {fmtNum(c.restricted)} Restricted, {fmtNum(c.confidential)} Confidential
            </span>
          )}
          <span className="flex gap-3 ml-auto">
            <LinkArrow onClick={() => go('discovery', `standard:${s.id}`)}>See files</LinkArrow>
            <LinkArrow onClick={() => go('exposure', `standard:${s.id}`)}>See who can read them</LinkArrow>
            <LinkArrow onClick={() => go('risks', `standard:${s.id}`)}>See risks</LinkArrow>
          </span>
        </div>
      )}

      {s.enabled && evidence && (
        <div className="mt-3 flex flex-wrap items-center gap-2">
          <span className="text-[12.5px] text-ink-3 mr-1">Evidence report for an assessor:</span>
          <ButtonLink size="sm" href={api.reportUrl('compliance-evidence', 'html', {}, undefined, s.id)} newTab><Printer size={14} /> Printable page</ButtonLink>
          <ButtonLink size="sm" href={api.reportUrl('compliance-evidence', 'csv', {}, undefined, s.id)} download><Download size={14} /> CSV</ButtonLink>
        </div>
      )}

      <div className="mt-3 text-[12.5px] text-ink-2 flex flex-wrap items-center gap-1.5">
        <span className="text-ink-3">What counts:</span>
        {s.categories.map((x) => <Tag key={x}>{x}</Tag>)}
        <span className="text-ink-3">{s.minConfidence === 'High' ? 'high-confidence matches only' : 'medium or high confidence'}</span>
      </div>

      {s.reason && (
        <p className="mt-2 text-[12.5px] text-ink-3">
          {s.enabled ? 'Turned on' : 'Turned off'} by {s.updatedByName} {s.updatedAt ? ago(s.updatedAt) : ''}: <span className="text-ink-2">“{s.reason}”</span>
        </p>
      )}

      {edit && (
        <div className="mt-4 rounded-xl border border-line bg-surface-2 p-4 space-y-3">
          {edit.mode !== 'off' && (
            <>
              <fieldset>
                <legend className="text-[12.5px] font-medium mb-1.5">Which kinds of data count</legend>
                <div className="flex flex-wrap gap-x-4 gap-y-1.5">
                  {all.map((x) => (
                    <label key={x} className="inline-flex items-center gap-2 text-[13px]">
                      <input
                        type="checkbox"
                        checked={edit.categories.includes(x)}
                        onChange={(e) => setEdit({ ...edit, categories: e.target.checked ? [...edit.categories, x] : edit.categories.filter((y) => y !== x) })}
                      />
                      {x}
                      {s.defaultCategories.includes(x) && <span className="text-ink-3 text-[11.5px]">default</span>}
                    </label>
                  ))}
                </div>
              </fieldset>
              <div className="flex flex-wrap items-center gap-3">
                <span className="text-[12.5px] font-medium">Count matches with</span>
                <Segmented
                  value={edit.minConfidence}
                  onChange={(v) => setEdit({ ...edit, minConfidence: v })}
                  options={[
                    { id: 'Medium', label: 'Medium or high confidence' },
                    { id: 'High', label: 'High confidence only' },
                  ]}
                />
              </div>
            </>
          )}
          {edit.mode === 'off' && (
            <p className="text-[13px] text-ink-2">
              Files stop being tagged with {s.name}, and it can no longer be chosen on Sensitive data or the Exposure map. Nothing about the files changes.
            </p>
          )}
          <label className="block">
            <span className="text-[12.5px] font-medium">Why</span>
            <textarea
              className={cx(inputCls, 'mt-1 min-h-[64px]')}
              maxLength={500}
              value={edit.reason}
              placeholder={edit.mode === 'off' ? 'For example: we stopped taking card payments in March.' : 'For example: we take card payments by phone.'}
              onChange={(e) => setEdit({ ...edit, reason: e.target.value })}
            />
          </label>
          {problem && <Callout tone="bad">{problem}</Callout>}
          <div className="flex gap-2">
            <Button variant="primary" onClick={save} disabled={busy}>
              {busy && <Loader2 size={14} className="animate-spin" />} {edit.mode === 'on' ? 'Turn on' : edit.mode === 'off' ? 'Turn off' : 'Save'}
            </Button>
            <Button onClick={() => setEdit(undefined)} disabled={busy}>Cancel</Button>
          </div>
        </div>
      )}

      <div className="mt-4 grid md:grid-cols-2 gap-4">
        <details className="min-w-0">
          <summary className="text-[12.5px] font-medium cursor-pointer">What Data Scope can't find for {s.name}</summary>
          <ul className="mt-2 list-disc pl-5 space-y-1 text-[12.5px] text-ink-2">
            {s.notDetected.map((x) => <li key={x}>{x}</li>)}
          </ul>
        </details>
        <details className="min-w-0">
          <summary className="text-[12.5px] font-medium cursor-pointer">Requirements this evidence helps with</summary>
          <ul className="mt-2 space-y-2 text-[12.5px]">
            {s.requirements.map((r) => (
              <li key={r.ref}>
                <div className="font-mono text-[12px] text-ink-2">{r.ref}</div>
                <div className="text-ink-2">{r.text}</div>
                <div className="text-ink-3">Data Scope shows: {r.helps}</div>
              </li>
            ))}
          </ul>
        </details>
      </div>
    </Card>
  );
}

function HistoryCard({ version }: { version: number }) {
  const history = useData(() => api.complianceHistory(), [version]);
  const rows = history.data ?? [];
  return (
    <Card title="History" pad={false}>
      {history.error && <div className="p-4"><Callout tone="bad">{history.error}</Callout></div>}
      {history.data && !rows.length && <p className="px-4 py-3 text-[13px] text-ink-3">No changes yet. Every standard starts turned off.</p>}
      {rows.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-[13px] min-w-[620px]">
            <thead>
              <tr><Th>When</Th><Th>Who</Th><Th>What</Th><Th>Why</Th></tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={i} className="border-t border-line">
                  <Td className="whitespace-nowrap text-ink-2">{fmtDateTime(r.at)}</Td>
                  <Td>{r.actorName}</Td>
                  <Td>{ACTION_LABEL[r.action] ?? r.action} {r.target}</Td>
                  <Td className="text-ink-2">{r.reason ?? '—'}</Td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}
