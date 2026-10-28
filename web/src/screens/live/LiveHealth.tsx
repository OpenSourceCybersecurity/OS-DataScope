import { RefreshCw } from 'lucide-react';
import { api } from '../../api/client';
import { useNav, type Route } from '../../App';
import { Button, Callout, Card, LinkArrow, PageHeader, Tag, fmtDateTime } from '../../ui/kit';
import { STATUS_LABEL, byUrgency, headline, statusTone } from './health';
import { useData } from './useData';

/**
 * System health: whether the services run, scans finish, the directory sync is current, emails go out, backups were
 * taken, disks have room, and certificates are in date. Refreshed every minute while open.
 */
export default function LiveHealth() {
  const { go } = useNav();
  const report = useData(() => api.systemHealth(), [], 60_000);
  const data = report.data;
  return (
    <>
      <PageHeader
        title="System health"
        sub="The daily check of your Data Scope server: services, scans, Active Directory, email, backups, disk space and certificates."
        actions={<Button onClick={() => void report.reload()}><RefreshCw size={14} /> Check again</Button>}
      />
      {report.error && <Callout tone="bad" title="Can't check the server">{report.error}</Callout>}
      {data && (
        <div className="grid gap-4">
          <Callout tone={data.overall === 'Ok' ? 'good' : data.overall === 'Warning' ? 'warn' : 'bad'} title={headline(data)}>
            Checked {fmtDateTime(data.checkedAt)}. Nothing here changes anything; it reads what the services record.
          </Callout>
          {byUrgency(data.checks).map((c) => (
            <Card key={c.id} title={<span className="flex flex-wrap items-center gap-2">{c.title} <Tag tone={statusTone(c.status)}>{STATUS_LABEL[c.status]}</Tag></span>}>
              <p className="text-[13.5px] max-w-3xl">{c.detail}</p>
              {c.fix && <p className="mt-1 text-[13px] text-ink-2 max-w-3xl"><span className="font-medium text-ink">What to do: </span>{c.fix}</p>}
              {c.items && c.items.length > 0 && (
                <ul className="mt-3 divide-y divide-line border-y border-line">
                  {c.items.map((i) => (
                    <li key={i.name} className="py-2 flex flex-wrap items-start gap-x-3 gap-y-1 text-[13px]">
                      <span className="w-[110px] shrink-0"><Tag tone={statusTone(i.status)}>{STATUS_LABEL[i.status]}</Tag></span>
                      <span className="font-medium min-w-[140px] break-words">{i.name}</span>
                      <span className="text-ink-2 flex-1 min-w-[200px] break-words">{i.detail}</span>
                    </li>
                  ))}
                </ul>
              )}
              {c.route && <div className="mt-2"><LinkArrow onClick={() => go(c.route as Route)}>Open</LinkArrow></div>}
            </Card>
          ))}
        </div>
      )}
    </>
  );
}
