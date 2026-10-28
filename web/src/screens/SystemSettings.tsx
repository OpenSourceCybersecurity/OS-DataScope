import { useBackend } from '../api/backend';
import { Card, PageHeader } from '../ui/kit';
import LiveSystemSettings from './live/LiveSystemSettings';

export default function SystemSettings() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LiveSystemSettings />;
  return <DemoSystemSettings />;
}

/** The groups a real server shows; the demo company has no settings to change. */
const DEMO = [
  ['Scanning', 'How hard scans may work a file server: folders listed and files read per second.'],
  ['New servers', 'The schedule a file server gets when it is added.'],
  ['File activity and alerts', 'How often Security logs are read, alert thresholds and who is emailed.'],
  ['Email', 'Your own mail server for review emails, reminders and alerts, with a test email.'],
  ['Stale and duplicate data', 'When files count as stale (1 to 30 years) and the smallest copy counted.'],
  ['History kept', 'How long removed files, permission changes, scan history and file activity are kept.'],
  ['Active Directory', 'How often users and groups are read.'],
];

function DemoSystemSettings() {
  return (
    <>
      <PageHeader title="System settings" sub="How Data Scope scans, alerts, emails and keeps history. Changes are recorded in the audit log and apply without restarting anything." />
      <Card title="On your Data Scope server">
        <p className="text-[13px] text-ink-2 max-w-2xl">Administrators change these on a real server; auditors can see them. The demo company has nothing to change.</p>
        <ul className="mt-3 divide-y divide-line border-y border-line">
          {DEMO.map(([title, about]) => (
            <li key={title} className="py-2">
              <div className="text-[13.5px] font-medium">{title}</div>
              <div className="text-[12.5px] text-ink-2">{about}</div>
            </li>
          ))}
        </ul>
      </Card>
    </>
  );
}
