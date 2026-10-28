import { useState } from 'react';
import { api, type ActivityAlert } from '../../api/client';
import { Button, Card, Tag, ago, cx, fmtDateTime, fmtNum, inputCls } from '../../ui/kit';
import { unusualLine } from './activity';
import { useData } from './useData';

/** Open alerts first; acknowledged ones on request. Each says what happened and shows the events behind it (D105). */
export function AlertsCard({ alerts, triage, onShow, onSet }: {
  alerts: ActivityAlert[]; triage: boolean; onShow: (a: ActivityAlert) => void; onSet: (a: ActivityAlert, state: 'Open' | 'Acknowledged', note?: string) => void;
}) {
  const [showAll, setShowAll] = useState(false);
  const open = alerts.filter((a) => a.state === 'Open');
  const shown = showAll ? alerts : open;
  const acknowledged = alerts.length - open.length;
  return (
    <div className="mb-4">
      <Card title={open.length > 0 ? `Alerts (${fmtNum(open.length)} open)` : 'Alerts'}>
        {shown.length === 0 ? (
          <p className="text-[13px] text-ink-2">No open alerts.</p>
        ) : (
          <ul className="flex flex-col divide-y divide-line">
            {shown.map((a) => <AlertRow key={a.id} alert={a} triage={triage} onShow={() => onShow(a)} onSet={(state, note) => onSet(a, state, note)} />)}
          </ul>
        )}
        {acknowledged > 0 && (
          <button className="mt-2 text-[12.5px] text-ink-2 hover:underline" onClick={() => setShowAll(!showAll)} aria-expanded={showAll}>
            {showAll ? 'Hide' : 'Show'} {fmtNum(acknowledged)} acknowledged
          </button>
        )}
      </Card>
    </div>
  );
}

function AlertRow({ alert: a, triage, onShow, onSet }: {
  alert: ActivityAlert; triage: boolean; onShow: () => void; onSet: (state: 'Open' | 'Acknowledged', note?: string) => void;
}) {
  const [noting, setNoting] = useState(false);
  const [note, setNote] = useState('');
  return (
    <li className="py-3 first:pt-0 last:pb-0">
      <div className="flex flex-wrap items-start gap-x-3 gap-y-1.5">
        <div className="flex-1 min-w-[220px]">
          <div className="flex flex-wrap items-center gap-2">
            <Tag tone={a.severity === 'High' ? 'bad' : 'warn'}>{a.severity}</Tag>
            <span className="font-medium">{a.title}</span>
            <span className="text-[12px] text-ink-3">{ago(a.lastEventAt)}</span>
            {a.state === 'Acknowledged' && <Tag>Acknowledged</Tag>}
          </div>
          <p className="text-[13px] text-ink-2 mt-0.5">{a.description}</p>
          {a.unusual && <p className="text-[12px] text-ink-3 mt-0.5">Compared with their own activity over the last few weeks. A lead to check, not proof of wrongdoing.</p>}
          {a.state === 'Acknowledged' && (
            <p className="text-[12px] text-ink-3 mt-0.5">
              {a.acknowledgedByName ?? 'Someone'}{a.acknowledgedAt && <>, {fmtDateTime(a.acknowledgedAt)}</>}{a.note && <>: {a.note}</>}
            </p>
          )}
        </div>
        <div className="flex flex-wrap gap-2">
          {a.rule !== 'EventsMissed' && <Button size="sm" variant="ghost" onClick={onShow}>Show events</Button>}
          {triage && a.state === 'Open' && !noting && <Button size="sm" onClick={() => setNoting(true)}>Acknowledge</Button>}
          {triage && a.state === 'Acknowledged' && <Button size="sm" variant="ghost" onClick={() => onSet('Open')}>Reopen</Button>}
        </div>
      </div>
      {noting && (
        <form className="flex flex-wrap gap-2 mt-2" onSubmit={(e) => { e.preventDefault(); onSet('Acknowledged', note.trim() || undefined); setNoting(false); }}>
          <input aria-label="Note" className={cx(inputCls, 'flex-1 min-w-[200px]')} maxLength={2000} placeholder="What you found (optional)" value={note} onChange={(e) => setNote(e.target.value)} />
          <Button size="sm" type="submit" variant="primary">Acknowledge</Button>
          <Button size="sm" variant="ghost" onClick={() => setNoting(false)}>Cancel</Button>
        </form>
      )}
    </li>
  );
}

/**
 * Unusual behaviour (D166): how many people Data Scope has learned on each file server, and how many have enough
 * history to be judged. Someone still being learned raises no unusual-behaviour alerts.
 */
export function UnusualCard() {
  const data = useData(() => api.activityUnusual(), [], 5 * 60_000);
  const d = data.data;
  if (!d?.on) return null;
  const sources = d.sources;
  return (
    <div className="mb-4">
      <Card title="Unusual behaviour">
        <p className="text-[13px] text-ink-2 max-w-[80ch]">
          Data Scope learns each person's usual activity once a day from the last {d.learnDays} days, then alerts when someone has a far busier day than usual, works in
          hours they never have, or uses several folders holding sensitive data they haven't used before. Each person is compared only with their own past.
        </p>
        {sources.length === 0 ? (
          <p className="text-[13px] text-ink-3 mt-2">Nothing learned yet. Learning starts with the next collection.</p>
        ) : (
          <ul className="text-[13px] mt-2 space-y-0.5">
            {sources.map((s) => <li key={s.serverId}>{unusualLine(s, d.minActiveDays)}</li>)}
          </ul>
        )}
      </Card>
    </div>
  );
}
