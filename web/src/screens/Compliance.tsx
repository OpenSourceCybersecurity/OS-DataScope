import { useBackend } from '../api/backend';
import { Card, PageHeader, Tag } from '../ui/kit';
import LiveCompliance from './live/LiveCompliance';

export default function Compliance() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LiveCompliance />;
  return <DemoCompliance />;
}

/** The standards a real server offers; the demo company has none turned on. */
const DEMO = [
  ['HIPAA', 'Health information.'],
  ['PCI DSS', 'Payment card numbers.'],
  ['GLBA', 'Social Security and taxpayer numbers, bank accounts and card numbers.'],
  ['US state breach laws', 'Personal information that would need notice if exposed.'],
  ['Passwords and keys', 'Passwords and private keys in shared files.'],
];

function DemoCompliance() {
  return (
    <>
      <PageHeader
        title="Compliance standards"
        sub="Turn on the standards that apply to your organisation. Data Scope tags the files, folders and shares holding the data each one covers. It shows the evidence and the gaps; it never says you are compliant."
      />
      <Card title="On your Data Scope server">
        <p className="text-[13px] text-ink-2 max-w-2xl">
          An administrator turns standards on with a reason and can change which kinds of data count. Sensitive data and the Exposure map can then be narrowed to a
          standard. Every change is in the audit log. The demo company has none turned on.
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
