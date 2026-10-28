import { useBackend } from '../api/backend';
import { Card, PageHeader } from '../ui/kit';
import LiveExposure from './live/LiveExposure';
import { BREADTH, BREADTH_ORDER } from './live/exposure';

export default function ExposureMapScreen() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LiveExposure />;
  return <DemoExposure />;
}

/** The demo company's data lives in the browser; the map needs a real server's access engine. */
function DemoExposure() {
  return (
    <>
      <PageHeader title="Exposure map" sub="Where Restricted and Confidential files are, who can read them and through which groups, from the latest scans and directory sync." />
      <Card title="On your Data Scope server">
        <p className="text-[13px] text-ink-2 max-w-2xl">
          The map shows each share as a box and each folder with sensitive files as a tile, sized by its sensitive files and coloured by how many people can read
          them. Select a folder, group or person to see why they have access. Anything Data Scope is not sure of is striped, and what it could not see is listed.
        </p>
        <ul className="mt-3 flex flex-wrap gap-x-4 gap-y-1.5 text-[12.5px] text-ink-2">
          {BREADTH_ORDER.map((k) => (
            <li key={k} className="inline-flex items-center gap-1.5">
              <span className="size-3 rounded-sm" style={{ background: BREADTH[k].color }} />
              {BREADTH[k].label}
            </li>
          ))}
        </ul>
      </Card>
    </>
  );
}
