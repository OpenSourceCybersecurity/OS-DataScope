import { useBackend } from '../api/backend';
import { Card, PageHeader, Tag } from '../ui/kit';
import LiveScanScope from './live/LiveScanScope';
import { EFFECT_LABEL } from './live/scope';

export default function ScanScope() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LiveScanScope />;
  return <DemoScanScope />;
}

/** The demo company has nothing left out; this shows what a real server offers. */
function DemoScanScope() {
  return (
    <>
      <PageHeader title="Scan scope & exclusions" sub="What scans cover, and what an administrator has left out and why. Anything left out is a coverage gap, never clean data." />
      <Card title="On your Data Scope server">
        <p className="text-[13px] text-ink-2 max-w-2xl">
          Administrators can leave out whole shares, single folders or file types, each with a reason. Every change is in the audit log, and anything left out
          shows as a coverage gap in Shares, Risks and reports. The demo company scans everything.
        </p>
        <ul className="mt-3 divide-y divide-line border-y border-line">
          {(['Skip', 'ListOnly'] as const).map((e) => (
            <li key={e} className="py-2">
              <Tag tone="warn">{EFFECT_LABEL[e].title}</Tag>
              <div className="text-[12.5px] text-ink-2 mt-1">{EFFECT_LABEL[e].about}</div>
            </li>
          ))}
        </ul>
      </Card>
    </>
  );
}
