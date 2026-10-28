import { useBackend } from '../api/backend';
import { Card, PageHeader } from '../ui/kit';
import LiveExclusions from './live/LiveExclusions';

export default function Exclusions() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LiveExclusions />;
  return <DemoExclusions />;
}

/** Made-up entries for the demo company; on a real server administrators keep this list. */
const DEMO = [
  { name: 'svc_datascope', kind: 'Person', reason: "Data Scope's scan account; reads shares, never changes them." },
  { name: 'Domain Admins', kind: 'Group', reason: 'IT administrators; their access is managed by IT, not by data owners.' },
  { name: 'svc_backup', kind: 'Person', reason: 'Backup service account.' },
];

function DemoExclusions() {
  return (
    <>
      <PageHeader
        title="Review exclusions"
        sub="Service accounts and IT groups whose access IT manages, such as svc-datascope or Domain Admins. Owners don't see them in access reviews, so reviews stay about people."
      />
      <Card title="Excluded users and groups">
        <p className="text-[13px] text-ink-2 max-w-2xl">
          Owners are not asked about these accounts and don't see them in their reviews. Administrators and auditors still see them, marked "Managed by IT".
          In the demo company this list can't be changed.
        </p>
        <ul className="mt-3 divide-y divide-line border-y border-line">
          {DEMO.map((x) => (
            <li key={x.name} className="py-2">
              <div className="text-[13.5px] font-medium break-words">{x.name} <span className="text-ink-3 font-normal">· {x.kind}</span></div>
              <div className="text-[12.5px] text-ink-2 break-words">{x.reason}</div>
            </li>
          ))}
        </ul>
      </Card>
    </>
  );
}
