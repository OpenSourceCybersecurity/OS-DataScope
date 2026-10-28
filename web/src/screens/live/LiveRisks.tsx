import { useState } from 'react';
import { ArrowRight, RefreshCw, ShieldCheck } from 'lucide-react';
import { api, type Risk, type RiskDecisionKind, type RiskRule, type RiskSeverity, type RiskView } from '../../api/client';
import { canTriageRisks, useBackend } from '../../api/backend';
import { useNav } from '../../App';
import { Button, Callout, Card, Drawer, Empty, Field, PageHeader, Segmented, SeverityPill, Tag, Unc, ago, cx, filterCls, fmtDateTime, fmtNum, inputCls, sevColor } from '../../ui/kit';
import { DURATIONS, MAX_DAYS, addDays, fmtDay, groupByRule, raisedText, riskDetail, riskLinks, riskTags, severityCounts, standardNames, todayUtc, triageProblem } from './risks';
import { useData } from './useData';
import { risksParam, shareOf } from './links';

const EMPTY_VIEW: Record<Exclude<RiskView, 'open'>, string> = {
  accepted: 'No risk is accepted right now. Accepting a risk takes it off the list until a date you choose.',
  snoozed: 'No risk is snoozed right now. Snoozing hides a risk for up to 90 days.',
  all: 'No rule matched the latest scan and directory sync.',
};

/**
 * What needs attention across the file servers, worked out from the latest scan, classification and directory sync.
 * Each risk says why it matters and what to do; Data Scope never changes permissions itself.
 */
export default function LiveRisks() {
  const triage = canTriageRisks(useBackend());
  const [view, setView] = useState<RiskView>('open');
  const [severity, setSeverity] = useState<'all' | RiskSeverity>('all');
  const [rule, setRule] = useState<'' | RiskRule>('');
  const [open, setOpen] = useState<Risk>();
  const [notice, setNotice] = useState<string>();
  // Other screens link here with "share:<id>" (the Exposure map, for one) to show one share's risks, or with
  // "standard:<id>" (Compliance standards) to show the risks involving one standard's data.
  const { param } = useNav();
  const [shareId, setShareId] = useState(risksParam(param).shareId);
  const [standard, setStandard] = useState(risksParam(param).standard ?? '');
  const risks = useData(
    () => api.risks({ severity: severity === 'all' ? undefined : severity, rule: rule || undefined, view, shareId, standard: standard || undefined }),
    [severity, rule, view, shareId, standard],
  );
  // The turned-on standards, for the filter and to name them on each risk. Risks still show if this can't load.
  const compliance = useData(() => api.complianceStandards(), []);
  const on = compliance.data?.standards.filter((s) => s.enabled) ?? [];
  const d = risks.data;
  const counts = d ? severityCounts(d.rules) : { high: 0, medium: 0, low: 0, total: 0 };
  const total = counts.total;
  const groups = d ? groupByRule(d.items, d.rules) : [];

  return (
    <>
      <PageHeader
        title="Risks"
        sub="What needs attention, from the latest scan, sensitive data found and Active Directory sync. Severity follows fixed rules over how sensitive the data is, who can reach it and how sure Data Scope is. Every risk says why and what to do."
        actions={<Button size="sm" variant="ghost" onClick={() => void risks.reload()}><RefreshCw size={14} /> Check again</Button>}
      />
      {risks.error && <div className="mb-4"><Callout tone="bad" title="Can't reach the Data Scope server">{risks.error}</Callout></div>}
      {notice && <div className="mb-4"><Callout tone="good">{notice}</Callout></div>}
      {d && (
        <>
          <div className="mb-3">
            <Segmented
              value={view}
              onChange={(v) => (setView(v), setNotice(undefined))}
              options={[
                { id: 'open', label: `Needs attention ${fmtNum(d.high + d.medium + d.low)}` },
                { id: 'accepted', label: `Accepted ${fmtNum(d.accepted)}` },
                { id: 'snoozed', label: `Snoozed ${fmtNum(d.snoozed)}` },
                { id: 'all', label: 'All' },
              ]}
            />
          </div>
          <div className="flex flex-wrap items-center gap-3 mb-2">
            <Segmented
              value={severity}
              onChange={setSeverity}
              options={[
                { id: 'all', label: `All ${fmtNum(total)}` },
                { id: 'High', label: `High ${fmtNum(counts.high)}` },
                { id: 'Medium', label: `Medium ${fmtNum(counts.medium)}` },
                { id: 'Low', label: `Low ${fmtNum(counts.low)}` },
              ]}
            />
            <select className={filterCls} value={rule} onChange={(e) => setRule(e.target.value as RiskRule | '')} aria-label="Kind of risk">
              <option value="">Every kind of risk</option>
              {d.rules.map((r) => (
                <option key={r.id} value={r.id}>{r.name} ({fmtNum(r.high + r.medium + r.low)})</option>
              ))}
            </select>
            {on.length > 0 && (
              <select className={filterCls} value={standard} onChange={(e) => setStandard(e.target.value)} aria-label="Compliance standard">
                <option value="">Any standard or none</option>
                {on.map((s) => (
                  <option key={s.id} value={s.id}>{s.name}</option>
                ))}
              </select>
            )}
            {shareId && (
              <span className="inline-flex items-center gap-2 text-[12.5px] text-ink-2">
                <Tag tone="accent">One share{d.items[0]?.where ? `: ${shareOf(d.items[0].where)}` : ''}</Tag>
                <button type="button" className="text-accent hover:underline" onClick={() => setShareId(undefined)}>Show every share</button>
              </span>
            )}
          </div>
          <p className="text-[12.5px] text-ink-3 mb-4">
            Checked {ago(d.checkedAt)}, from {d.lastScanAt ? `the scan finished ${ago(d.lastScanAt)}` : 'no completed scan yet'}
            {d.directorySyncedAt ? ` and the directory synced ${ago(d.directorySyncedAt)}` : ', without a directory sync'}.
          </p>
          {d.limits.map((l) => <div key={l} className="mb-3"><Callout tone="warn">{l}</Callout></div>)}
          {d.truncated && <div className="mb-3"><Callout tone="info">Showing the first {fmtNum(d.items.length)}. Pick a severity or a kind of risk to see the rest.</Callout></div>}
        </>
      )}

      {d && total === 0 && view !== 'open' && (
        <Card><Empty icon={<ShieldCheck size={20} />} title="Nothing here">{EMPTY_VIEW[view]}</Empty></Card>
      )}
      {d && total === 0 && view === 'open' && (
        <Card>
          <Empty icon={<ShieldCheck size={20} />} title={d.lastScanAt ? 'Nothing needs attention' : 'Nothing scanned yet'}>
            {d.lastScanAt
              ? d.accepted + d.snoozed > 0
                ? `Every risk found is accepted or snoozed (${fmtNum(d.accepted + d.snoozed)}). They come back here when their date passes.`
                : 'No rule matched the latest scan and directory sync. Risks appear here as soon as one does.'
              : 'Add a file server under Servers & scans and run a scan. Risks are worked out from what it finds.'}
          </Empty>
        </Card>
      )}
      {d && total > 0 && !d.items.length && <Callout tone="info">No risks match these filters.</Callout>}

      <div className="flex flex-col gap-5">
        {groups.map(({ rule: r, items }) => (
          <section key={r.id}>
            <h2 className="text-[13px] font-semibold text-ink-2 mb-2">
              {r.name} <span className="font-normal text-ink-3">· {fmtNum(items.length)}</span>
            </h2>
            <ul className="bg-surface border border-line rounded-xl divide-y divide-[var(--line)] overflow-hidden">
              {items.map((x) => (
                <li key={x.id}>
                  <button onClick={() => setOpen(x)} className="w-full text-left flex items-start gap-3 px-4 py-3 hover:bg-surface-2 relative">
                    <span className="absolute left-0 top-0 bottom-0 w-[3px]" style={{ background: sevColor(x.severity) }} />
                    <span className="w-[80px] shrink-0"><SeverityPill s={x.severity} /></span>
                    <span className="min-w-0 flex-1">
                      <span className="block font-medium">{x.title}</span>
                      <span className="block font-mono text-[12px] text-ink-2 break-all">{x.where}</span>
                      {riskDetail(x) && <span className="block text-[12.5px] text-ink-3 mt-0.5">{riskDetail(x)}</span>}
                      {riskTags(x).length + x.standards.length > 0 && (
                        <span className="flex flex-wrap gap-1.5 mt-1.5">
                          {standardNames(x, on).map((n) => <Tag key={`std-${n}`} tone="warn">{n}</Tag>)}
                          {riskTags(x).map((t) => <Tag key={t.label} tone={t.tone}>{t.label}</Tag>)}
                        </span>
                      )}
                    </span>
                  </button>
                </li>
              ))}
            </ul>
          </section>
        ))}
      </div>
      {open && (
        <RiskDrawer
          r={open}
          standards={standardNames(open, on)}
          triage={triage}
          onClose={() => setOpen(undefined)}
          onDone={async (message) => {
            setOpen(undefined);
            setNotice(message);
            await risks.reload();
          }}
        />
      )}
    </>
  );
}

function RiskDrawer({ r, standards, triage, onClose, onDone }: { r: Risk; standards: string[]; triage: boolean; onClose: () => void; onDone: (message: string) => Promise<void> }) {
  const { go } = useNav();
  const links = riskLinks(r);
  const sensitivity = r.restricted || r.confidential
    ? [r.restricted ? `${fmtNum(r.restricted)} restricted` : '', r.confidential ? `${fmtNum(r.confidential)} confidential` : ''].filter(Boolean).join(', ') + ' files'
    : undefined;
  const facts: [string, string | undefined][] = [
    ['Sensitive files', sensitivity],
    ['Who', r.who ?? undefined],
    ['Access', r.access ?? undefined],
    ['Certainty', r.certainty ?? undefined],
    ['Compliance standards', standards.length ? standards.join(', ') : undefined],
    ['Severity', raisedText(r, standards)],
  ];
  return (
    <Drawer open onClose={onClose} title={r.title} sub={<span className="inline-flex items-center gap-2"><SeverityPill s={r.severity} /></span>} width={620}>
      <div className="flex flex-col gap-5">
        <div>
          <div className="eyebrow mb-1">Where</div>
          {r.where.startsWith('\\\\') ? <Unc>{r.where}</Unc> : <span>{r.where}</span>}
        </div>
        <div>
          <div className="eyebrow mb-1">Why it matters</div>
          <p className="text-ink-2 max-w-[62ch]">{r.why}</p>
        </div>
        {facts.some(([, v]) => v) && (
          <div>
            <div className="eyebrow mb-2">What the severity is based on</div>
            <dl className="grid sm:grid-cols-2 gap-2 text-[13px]">
              {facts.filter(([, v]) => v).map(([k, v]) => (
                <div key={k} className="bg-surface-2 rounded-lg px-3 py-2">
                  <dt className="text-[11.5px] text-ink-3">{k}</dt>
                  <dd>{v}</dd>
                </div>
              ))}
            </dl>
          </div>
        )}
        <Callout tone="info" title="What to do">{r.fix}</Callout>
        {links.length > 0 && (
          <div className="flex flex-wrap gap-2">
            {links.map((l, i) => (
              <Button key={l.label} variant={i === 0 ? 'primary' : 'secondary'} onClick={() => go(l.route, l.param)}>
                {l.label} <ArrowRight size={14} />
              </Button>
            ))}
          </div>
        )}
        <Triage r={r} triage={triage} onDone={onDone} />
        <p className="text-[12px] text-ink-3">
          Access is computed from the permissions the latest scan read and the latest directory sync. It is not evidence that anyone opened a file. Data Scope does not change permissions; IT makes each fix in Windows. Accepting or snoozing a risk only hides it here.
        </p>
      </div>
    </Drawer>
  );
}

type Mode = RiskDecisionKind;

/** Accept or snooze, and what was already decided. Nothing here changes a permission, a group or a file. */
function Triage({ r, triage, onDone }: { r: Risk; triage: boolean; onDone: (message: string) => Promise<void> }) {
  const today = todayUtc();
  const [mode, setMode] = useState<Mode>();
  const [note, setNote] = useState('');
  const [until, setUntil] = useState('');
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string>();
  const d = r.decision;

  const start = (m: Mode) => {
    setMode(m);
    setProblem(undefined);
    setNote('');
    setUntil(addDays(today, DURATIONS[m][1].days));
  };
  const run = async (work: () => Promise<unknown>, message: string) => {
    setBusy(true);
    setProblem(undefined);
    try {
      await work();
      await onDone(message);
    } catch (e) {
      setProblem(e instanceof Error ? e.message : String(e));
      setBusy(false);
    }
  };
  const formProblem = mode && triageProblem(mode, note, until, today);

  return (
    <div className="flex flex-col gap-3">
      {d && (
        <Callout tone={d.superseded ? 'warn' : 'info'} title={d.superseded ? 'More severe since it was ' + (d.kind === 'Accepted' ? 'accepted' : 'snoozed') : d.kind === 'Accepted' ? 'Accepted' : 'Snoozed'}>
          {d.decidedByName} {d.kind === 'Accepted' ? 'accepted' : 'snoozed'} this on {fmtDateTime(d.decidedAt)} until {fmtDay(d.until)}
          {d.superseded ? `, when it was ${d.severity}. It is now ${r.severity}, so it shows again. Reopen it to decide again.` : '.'}
          {d.note && <span className="block mt-1 whitespace-pre-wrap">{d.note}</span>}
        </Callout>
      )}
      {problem && <Callout tone="bad" title="That didn't work">{problem}</Callout>}
      {triage && !mode && (
        <div className="flex flex-wrap gap-2">
          {!d && <Button disabled={busy} onClick={() => start('Accepted')}>Accept the risk</Button>}
          {!d && <Button disabled={busy} onClick={() => start('Snoozed')}>Snooze</Button>}
          {d && (
            <Button variant="ghost" disabled={busy} onClick={() => run(() => api.reopenRisk(d.id), 'Reopened. The risk needs attention again.')}>
              Reopen
            </Button>
          )}
        </div>
      )}
      {triage && mode && (
        <form
          className="space-y-3 border-t border-line pt-3"
          onSubmit={(e) => {
            e.preventDefault();
            if (formProblem) return;
            void run(() => api.decideRisk(r.id, mode, until, note.trim()), `${mode === 'Accepted' ? 'Accepted' : 'Snoozed'} until ${fmtDay(until)}. It shows under ${mode === 'Accepted' ? 'Accepted' : 'Snoozed'} until then, or sooner if it becomes more severe.`);
          }}
        >
          <div className="text-[13px] font-medium">
            {mode === 'Accepted' ? 'Accept the risk' : 'Snooze'}
          </div>
          <p className="text-[12.5px] text-ink-3 max-w-[62ch]">
            {mode === 'Accepted'
              ? 'Takes the risk off the list until the date, with your reason on record. It comes back sooner if it becomes more severe.'
              : 'Hides the risk for a while, for example until a planned change. It comes back sooner if it becomes more severe.'}
          </p>
          <Field label="Until" htmlFor="triage-until">
            <div className="flex flex-wrap items-center gap-2">
              {DURATIONS[mode].map((o) => (
                <Button key={o.days} size="sm" variant={until === addDays(today, o.days) ? 'primary' : 'secondary'} onClick={() => setUntil(addDays(today, o.days))}>
                  {o.label}
                </Button>
              ))}
              <input id="triage-until" type="date" className={cx(filterCls)} value={until} min={addDays(today, 1)} max={addDays(today, MAX_DAYS[mode])} onChange={(e) => setUntil(e.target.value)} />
            </div>
          </Field>
          <Field label={mode === 'Snoozed' ? 'Note (optional)' : 'Why is it acceptable (required)'} htmlFor="triage-note">
            <textarea id="triage-note" className={cx(inputCls, 'h-20 py-2')} value={note} maxLength={1000} onChange={(e) => setNote(e.target.value)}
              placeholder={mode === 'Accepted' ? 'For example: public price lists, approved by the owner' : ''} />
          </Field>
          {formProblem && (note || mode === 'Snoozed') && <p className="text-[12.5px] text-ink-3">{formProblem}</p>}
          <div className="flex gap-2">
            <Button type="submit" variant="primary" disabled={busy || !!formProblem}>
              {mode === 'Accepted' ? 'Accept' : 'Snooze'}
            </Button>
            <Button variant="ghost" disabled={busy} onClick={() => setMode(undefined)}>Cancel</Button>
          </div>
        </form>
      )}
    </div>
  );
}
