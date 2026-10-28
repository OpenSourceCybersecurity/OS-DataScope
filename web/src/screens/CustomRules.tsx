import { useBackend } from '../api/backend';
import { Card, PageHeader, Tag } from '../ui/kit';
import LiveCustomRules from './live/LiveCustomRules';

export default function CustomRulesScreen() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LiveCustomRules />;
  return <DemoCustomRules />;
}

/** The starting points a real server offers; the demo company has no rules of its own. */
const DEMO = [
  ['IP addresses', 'Four numbers from 0 to 255 separated by dots, such as 10.20.30.40.'],
  ['Lists of email addresses', 'Ten or more different addresses in one file.'],
  ['Confidentiality markings', 'Words such as "privileged and confidential" or "internal use only".'],
  ['Employee ID', 'Your own number format, such as EMP-204518.'],
];

function DemoCustomRules() {
  return (
    <>
      <PageHeader
        title="Custom detection"
        sub="Add your own words, phrases and patterns for data Data Scope doesn't look for on its own, such as IP addresses, project code names or your organisation's ID numbers."
      />
      <Card title="On your Data Scope server">
        <p className="text-[13px] text-ink-2 max-w-2xl">
          An administrator adds a rule with a reason, tries it on made-up text, and chooses what a match does: label the file Restricted, label it Confidential, or
          just list the match. Matches appear in Sensitive data, reports and compliance standards like the built-in ones. Every change is in the audit log. The demo
          company has none.
        </p>
        <ul className="mt-3 divide-y divide-line border-y border-line">
          {DEMO.map(([name, what]) => (
            <li key={name} className="py-2 flex flex-wrap items-center gap-2">
              <Tag>{name}</Tag>
              <span className="text-[12.5px] text-ink-2">{what}</span>
            </li>
          ))}
        </ul>
      </Card>
    </>
  );
}
