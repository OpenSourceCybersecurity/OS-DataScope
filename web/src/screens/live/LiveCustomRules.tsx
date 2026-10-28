import { useEffect, useState } from 'react';
import { FlaskConical, Loader2, Pencil, Plus, Power, PowerOff, Trash2 } from 'lucide-react';
import {
  ApiError,
  api,
  type CustomRule,
  type CustomRuleDraft,
  type CustomRuleLimits,
  type CustomRules,
  type CustomRuleTemplate,
  type CustomRuleTestResult,
} from '../../api/client';
import { canManage, canReadSettings, useBackend } from '../../api/backend';
import { useNav } from '../../App';
import { Button, Callout, Card, ClassPill, Field, LinkArrow, PageHeader, Segmented, Tag, Td, Th, ago, cx, fmtDateTime, fmtNum, inputCls } from '../../ui/kit';
import {
  ACTION_LABEL,
  EFFECT_HINT,
  EFFECT_LABEL,
  draftFromRule,
  draftFromTemplate,
  draftProblems,
  emptyDraft,
  fieldErrors,
  filesText,
  parseWords,
  ruleBody,
  wordsText,
} from './customRules';
import { useData } from './useData';

/**
 * Custom detection (D133): administrators' own words, phrases and patterns for data the built-in detectors don't
 * look for. Matches are possible matches like any other, with masked evidence, and appear wherever findings do.
 */
export default function LiveCustomRules() {
  const backend = useBackend();
  const admin = canManage(backend);
  const loaded = useData(() => api.customRules(), []);
  const [info, setInfo] = useState<CustomRules>();
  const [version, setVersion] = useState(0);
  const [adding, setAdding] = useState<CustomRuleDraft | 'pick'>();
  const [example, setExample] = useState('');
  useEffect(() => setInfo(loaded.data), [loaded.data]);
  const reload = async () => {
    setInfo(await api.customRules());
    setVersion((v) => v + 1);
  };
  const enabled = info?.rules.filter((r) => r.enabled).length ?? 0;

  const startFrom = (t?: CustomRuleTemplate) => {
    setAdding(t ? draftFromTemplate(t) : emptyDraft());
    setExample(t?.example ?? '');
  };

  return (
    <>
      <PageHeader
        title="Custom detection"
        sub="Add your own words, phrases and patterns for data Data Scope doesn't look for on its own, such as IP addresses, project code names or your organisation's ID numbers. Matches are possible matches, shown with masked evidence like any other."
        actions={admin && info && !adding && (
          <Button variant="primary" onClick={() => setAdding('pick')} disabled={info.rules.length >= info.limits.maxRules}>
            <Plus size={14} /> Add a rule
          </Button>
        )}
      />
      {loaded.error && <Callout tone="bad" title="Can't load custom detection rules">{loaded.error}</Callout>}
      {!info && !loaded.error && <p className="text-[13px] text-ink-3 flex items-center gap-2"><Loader2 size={14} className="animate-spin" /> Loading…</p>}
      {info && (
        <div className="space-y-4">
          {!admin && <Callout title="View only">Only Data Scope administrators can add or change rules.</Callout>}
          {info.filesToReread > 0 && (
            <Callout tone="info" title={`${fmtNum(info.filesToReread)} ${info.filesToReread === 1 ? 'file is' : 'files are'} read again on the next scan`}>
              {info.filesToReread === 1 ? 'It was' : 'They were'} last checked with other rules. Until then, results show what the earlier rules found.
            </Callout>
          )}

          {adding === 'pick' && (
            <Card title="Start from">
              <div className="grid sm:grid-cols-2 lg:grid-cols-3 gap-2">
                {info.templates.map((t) => (
                  <button key={t.id} className="text-left rounded-lg border border-line bg-surface hover:border-accent p-3 min-w-0" onClick={() => startFrom(t)}>
                    <div className="text-[13.5px] font-medium">{t.name}</div>
                    <div className="text-[12.5px] text-ink-3 mt-0.5">{t.description}</div>
                  </button>
                ))}
                <button className="text-left rounded-lg border border-dashed border-line bg-surface hover:border-accent p-3" onClick={() => startFrom()}>
                  <div className="text-[13.5px] font-medium">Start from scratch</div>
                  <div className="text-[12.5px] text-ink-3 mt-0.5">Your own words and phrases, or your own pattern.</div>
                </button>
              </div>
              <div className="mt-3"><Button onClick={() => setAdding(undefined)}>Cancel</Button></div>
            </Card>
          )}

          {adding && adding !== 'pick' && (
            <Card title="New rule">
              <RuleEditor
                initial={adding}
                example={example}
                limits={info.limits}
                canTurnOn={enabled < info.limits.maxEnabled}
                onCancel={() => setAdding(undefined)}
                onSave={async (draft, reason, turnOn) => {
                  await api.addCustomRule({ ...ruleBody(draft), enabled: turnOn, reason });
                  setAdding(undefined);
                  await reload();
                }}
              />
            </Card>
          )}

          {info.rules.length === 0 && !adding && (
            <Card>
              <p className="text-[13px] text-ink-2 max-w-2xl">
                No custom rules yet. Data Scope looks for Social Security and taxpayer numbers, card numbers, bank accounts, passwords and keys, health information and
                dates of birth on its own. {admin ? 'Add a rule for anything else your organisation treats as sensitive.' : 'An administrator can add rules for anything else.'}
              </p>
            </Card>
          )}

          {info.rules.map((r) => (
            <RuleCard key={r.id} rule={r} info={info} enabledCount={enabled} admin={admin} onChanged={reload} />
          ))}

          {info.rules.length > 0 && (
            <p className="text-[12.5px] text-ink-3">
              {fmtNum(enabled)} of at most {fmtNum(info.limits.maxEnabled)} rules on · policy <span className="font-mono">{info.policyVersion}</span>
            </p>
          )}
          {canReadSettings(backend) && <HistoryCard version={version} />}
        </div>
      )}
    </>
  );
}

function RuleCard({ rule: r, info, enabledCount, admin, onChanged }: { rule: CustomRule; info: CustomRules; enabledCount: number; admin: boolean; onChanged: () => Promise<void> }) {
  const { go } = useNav();
  const [mode, setMode] = useState<'edit' | 'toggle' | 'remove'>();
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string>();
  const close = () => {
    setMode(undefined);
    setProblem(undefined);
    setReason('');
  };
  const run = async (action: () => Promise<unknown>) => {
    setBusy(true);
    setProblem(undefined);
    try {
      await action();
      close();
      await onChanged();
    } catch (e) {
      setProblem(e instanceof ApiError ? Object.values(e.errors).flat()[0] ?? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  const toggle = () => {
    if (reason.trim().length < 3) return setProblem('Say why, so others know later.');
    void run(() => api.updateCustomRule(r.id, { enabled: !r.enabled, reason: reason.trim() }));
  };

  return (
    <Card
      title={
        <span className="flex flex-wrap items-center gap-2">
          {r.name}
          <Tag tone={r.enabled ? 'accent' : 'neutral'}>{r.enabled ? 'On' : 'Off'}</Tag>
          <Tag tone={r.effect === 'Restricted' ? 'bad' : r.effect === 'Confidential' ? 'warn' : 'neutral'}>{EFFECT_LABEL[r.effect]}</Tag>
        </span>
      }
      action={
        admin && !mode ? (
          <div className="flex flex-wrap gap-2">
            <Button size="sm" onClick={() => setMode('edit')}><Pencil size={14} /> Change</Button>
            <Button size="sm" onClick={() => setMode('toggle')} disabled={!r.enabled && enabledCount >= info.limits.maxEnabled}
              title={!r.enabled && enabledCount >= info.limits.maxEnabled ? `${info.limits.maxEnabled} rules are already on` : undefined}>
              {r.enabled ? <><PowerOff size={14} /> Turn off</> : <><Power size={14} /> Turn on</>}
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setMode('remove')}><Trash2 size={14} /> Remove</Button>
          </div>
        ) : undefined
      }
    >
      {r.description && <p className="text-[13px] text-ink-2 max-w-3xl mb-1.5">{r.description}</p>}
      <p className="text-[12.5px] text-ink-2 max-w-3xl break-words">{r.summary}</p>
      {r.kind === 'Pattern' && r.pattern && (
        <pre className="mt-2 max-w-3xl overflow-x-auto rounded-md bg-surface-2 px-2.5 py-1.5 font-mono text-[12px] text-ink-2 whitespace-pre-wrap break-all">{r.pattern}</pre>
      )}
      {r.kind === 'Keywords' && (
        <div className="mt-2 flex flex-wrap gap-1.5">
          {r.keywords.slice(0, 12).map((k) => <Tag key={k}>{k}</Tag>)}
          {r.keywords.length > 12 && <span className="text-[12px] text-ink-3">and {fmtNum(r.keywords.length - 12)} more</span>}
        </div>
      )}
      <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2">
        <span className="text-[13px] font-medium tnum">{filesText(r)}</span>
        <span className="text-[12px] text-ink-3 font-mono">{r.ruleId}</span>
        {r.files > 0 && <span className="ml-auto"><LinkArrow onClick={() => go('discovery', `rule:${r.ruleId}`)}>See files</LinkArrow></span>}
      </div>
      <p className="mt-2 text-[12.5px] text-ink-3">
        Changed by {r.updatedByName} {ago(r.updatedAt)}: <span className="text-ink-2">“{r.reason}”</span>
      </p>

      {mode === 'edit' && (
        <div className="mt-4 rounded-xl border border-line bg-surface-2 p-4">
          <RuleEditor
            initial={draftFromRule(r)}
            example=""
            limits={info.limits}
            onCancel={close}
            onSave={async (draft, why) => {
              await api.updateCustomRule(r.id, { ...ruleBody(draft), reason: why });
              close();
              await onChanged();
            }}
          />
        </div>
      )}
      {(mode === 'toggle' || mode === 'remove') && (
        <div className="mt-4 rounded-xl border border-line bg-surface-2 p-4 space-y-3">
          <p className="text-[13px] text-ink-2">
            {mode === 'remove'
              ? 'Remove this rule? The audit log keeps what it was. Files it found something in are read again on the next scan; until then their results show it as a removed rule.'
              : r.enabled
                ? 'Scans stop using this rule. Files it found something in are read again on the next scan.'
                : 'Scans use this rule again, and every file is read again on the next scan to apply it.'}
          </p>
          {mode === 'toggle' && (
            <Field label="Why">
              <textarea className={cx(inputCls, 'h-auto min-h-[64px] py-2')} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)}
                placeholder={r.enabled ? 'For example: too many false matches; tuning it.' : 'For example: tuned to leave out version numbers.'} />
            </Field>
          )}
          {problem && <Callout tone="bad">{problem}</Callout>}
          <div className="flex gap-2">
            {mode === 'remove' ? (
              <Button variant="danger" disabled={busy} onClick={() => void run(() => api.removeCustomRule(r.id))}>
                {busy && <Loader2 size={14} className="animate-spin" />} Remove
              </Button>
            ) : (
              <Button variant="primary" disabled={busy} onClick={toggle}>
                {busy && <Loader2 size={14} className="animate-spin" />} {r.enabled ? 'Turn off' : 'Turn on'}
              </Button>
            )}
            <Button onClick={close} disabled={busy}>Cancel</Button>
          </div>
        </div>
      )}
    </Card>
  );
}

/** Add or change a rule, with a box to try it on made-up text first. */
function RuleEditor({ initial, example, limits, canTurnOn, onSave, onCancel }: {
  initial: CustomRuleDraft;
  example: string;
  limits: CustomRuleLimits;
  /** Only when adding: whether there is room to turn the new rule on. */
  canTurnOn?: boolean;
  onSave: (draft: CustomRuleDraft, reason: string, turnOn: boolean) => Promise<void>;
  onCancel: () => void;
}) {
  const [d, setD] = useState(initial);
  const [keywords, setKeywords] = useState(wordsText(initial.keywords));
  const [near, setNear] = useState(wordsText(initial.nearWords));
  const [reason, setReason] = useState('');
  const [turnOn, setTurnOn] = useState(canTurnOn ?? true);
  const [sample, setSample] = useState(example);
  const [tried, setTried] = useState<CustomRuleTestResult>();
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<'save' | 'test'>();
  const adding = canTurnOn !== undefined;

  const draft = (): CustomRuleDraft => ({ ...d, keywords: parseWords(keywords), nearWords: parseWords(near) });
  const set = (change: Partial<CustomRuleDraft>) => {
    setD({ ...d, ...change });
    setTried(undefined);
  };
  const fail = (e: unknown) => {
    if (e instanceof ApiError && Object.keys(e.errors).length > 0) setErrors(fieldErrors(e.errors));
    else setErrors({ form: e instanceof ApiError ? e.message : String(e) });
  };

  const tryIt = async () => {
    setBusy('test');
    setErrors({});
    try {
      const { name: _n, description: _d, ...body } = ruleBody(draft());
      setTried(await api.testCustomRule(body, sample));
    } catch (e) {
      setTried(undefined);
      fail(e);
    } finally {
      setBusy(undefined);
    }
  };
  const save = async () => {
    const local = draftProblems(draft(), reason, limits);
    if (Object.keys(local).length > 0) return setErrors(local);
    setBusy('save');
    setErrors({});
    try {
      await onSave(draft(), reason.trim(), turnOn);
    } catch (e) {
      fail(e);
    } finally {
      setBusy(undefined);
    }
  };
  const err = (field: string) => errors[field] && <span className="block text-[12px] text-[var(--s-critical)] mt-1">{errors[field]}</span>;

  return (
    <div className="space-y-4">
      <div className="grid md:grid-cols-2 gap-3">
        <Field label="Name" hint="Shown on findings, for example in Sensitive data.">
          <input className={inputCls} maxLength={limits.maxNameChars} value={d.name} onChange={(e) => set({ name: e.target.value })} placeholder="For example: Project Falcon" />
          {err('name')}
        </Field>
        <Field label="Description (optional)">
          <input className={inputCls} maxLength={limits.maxDescriptionChars} value={d.description} onChange={(e) => set({ description: e.target.value })} placeholder="What it finds and why it matters" />
          {err('description')}
        </Field>
      </div>

      <div className="space-y-2">
        <span className="block text-[13px] font-medium">Look for</span>
        <Segmented
          value={d.kind}
          onChange={(kind) => set({ kind })}
          options={[
            { id: 'Keywords', label: 'Words and phrases' },
            { id: 'Pattern', label: 'A pattern' },
          ]}
        />
        {d.kind === 'Keywords' ? (
          <>
            <textarea className={cx(inputCls, 'h-auto min-h-[96px] py-2')} value={keywords} onChange={(e) => { setKeywords(e.target.value); setTried(undefined); }}
              placeholder={'One per line, for example:\nProject Falcon\nattorney-client privileged'} />
            <span className="block text-[12px] text-ink-3">
              Whole words only, so "pay" doesn't match "payroll". Phrases match across line breaks. Up to {limits.maxKeywords} per rule.
            </span>
            {err('keywords')}
          </>
        ) : (
          <>
            <input className={cx(inputCls, 'font-mono text-[13px]')} maxLength={limits.maxPatternChars} value={d.pattern} onChange={(e) => set({ pattern: e.target.value })}
              placeholder="For example: \bEMP-[0-9]{6}\b" spellCheck={false} />
            <span className="block text-[12px] text-ink-3">
              A .NET regular expression. Look-ahead, look-behind and back-references aren't supported, so every pattern runs in time proportional to the file's length.
              A match is at most {limits.maxMatchChars} characters; matched values are masked in evidence.
            </span>
            {err('pattern')}
          </>
        )}
        <label className="inline-flex items-center gap-2 text-[13px]">
          <input type="checkbox" checked={d.caseSensitive} onChange={(e) => set({ caseSensitive: e.target.checked })} /> Match case
        </label>
      </div>

      <div className="grid md:grid-cols-2 gap-3">
        <Field label="Only near these words (optional)" hint={`A match counts only with one of these within ${limits.nearWindow} characters. One per line.`}>
          <textarea className={cx(inputCls, 'h-auto min-h-[64px] py-2')} value={near} onChange={(e) => { setNear(e.target.value); setTried(undefined); }} placeholder={'For example:\nIP\nserver'} />
          {err('nearWords')}
        </Field>
        <Field label="Report a file with at least" hint="Different matching values; a value repeated counts once. Use 10 or more for lists, such as of email addresses.">
          <span className="flex items-center gap-2">
            <input type="number" min={1} max={limits.maxMinMatches} className={cx(inputCls, 'w-28')} value={d.minMatches}
              onChange={(e) => set({ minMatches: Number(e.target.value) })} />
            <span className="text-[13px] text-ink-2">{d.minMatches === 1 ? 'match' : 'matches'}</span>
          </span>
          {err('minMatches')}
        </Field>
      </div>

      <div className="space-y-1.5">
        <span className="block text-[13px] font-medium">A file with a match</span>
        <Segmented
          value={d.effect}
          onChange={(effect) => set({ effect })}
          options={[
            { id: 'Restricted', label: 'Label Restricted' },
            { id: 'Confidential', label: 'Label Confidential' },
            { id: 'ListOnly', label: 'List only' },
          ]}
        />
        <span className="block text-[12px] text-ink-3">{EFFECT_HINT[d.effect]}</span>
        {err('effect')}
      </div>

      <div className="rounded-lg border border-line bg-surface p-3 space-y-2">
        <span className="block text-[13px] font-medium">Try it</span>
        <textarea className={cx(inputCls, 'h-auto min-h-[72px] py-2 text-[13px]')} maxLength={limits.maxSampleChars} value={sample}
          onChange={(e) => { setSample(e.target.value); setTried(undefined); }}
          placeholder="Paste or type some made-up text. It is not saved or logged." />
        {err('sample')}
        <div className="flex flex-wrap items-center gap-3">
          <Button size="sm" onClick={tryIt} disabled={busy !== undefined || !sample}>
            {busy === 'test' ? <Loader2 size={14} className="animate-spin" /> : <FlaskConical size={14} />} Try it
          </Button>
          {tried && (
            <span className="text-[13px] flex flex-wrap items-center gap-2">
              {tried.values === 0
                ? 'No match.'
                : <>{fmtNum(tried.values)} different {tried.values === 1 ? 'value' : 'values'} found. {tried.reported ? 'A file like this would be' : `Not enough to report a file (at least ${fmtNum(d.minMatches)} needed); it stays`}</>}
              {tried.values > 0 && <ClassPill c={tried.label} />}
              {tried.truncated && <span className="text-ink-3">Only the first {fmtNum(limits.maxSampleChars)} characters were tried.</span>}
            </span>
          )}
        </div>
        {tried && tried.evidence.length > 0 && (
          <ul className="space-y-1">
            {tried.evidence.map((e, i) => <li key={i} className="font-mono text-[12px] text-ink-2 bg-surface-2 rounded px-2 py-1 break-all">{e}</li>)}
          </ul>
        )}
      </div>

      <Field label="Why">
        <textarea className={cx(inputCls, 'h-auto min-h-[64px] py-2')} maxLength={500} value={reason} onChange={(e) => setReason(e.target.value)}
          placeholder="For example: our server addresses are confidential under our security policy." />
        {err('reason')}
      </Field>
      {adding && (
        <label className="inline-flex items-center gap-2 text-[13px]">
          <input type="checkbox" checked={turnOn} disabled={!canTurnOn} onChange={(e) => setTurnOn(e.target.checked)} />
          Turn on now {canTurnOn ? '(every file is read again on the next scan)' : `(${limits.maxEnabled} rules are already on)`}
        </label>
      )}
      {errors.form && <Callout tone="bad">{errors.form}</Callout>}
      <div className="flex gap-2">
        <Button variant="primary" onClick={save} disabled={busy !== undefined}>
          {busy === 'save' && <Loader2 size={14} className="animate-spin" />} {adding ? 'Add rule' : 'Save'}
        </Button>
        <Button onClick={onCancel} disabled={busy !== undefined}>Cancel</Button>
      </div>
    </div>
  );
}

function HistoryCard({ version }: { version: number }) {
  const history = useData(() => api.customRuleHistory(), [version]);
  const rows = history.data ?? [];
  return (
    <Card title="History" pad={false}>
      {history.error && <div className="p-4"><Callout tone="bad">{history.error}</Callout></div>}
      {history.data && !rows.length && <p className="px-4 py-3 text-[13px] text-ink-3">No changes yet.</p>}
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
