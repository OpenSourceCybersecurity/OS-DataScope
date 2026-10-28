import { useBackend } from '../api/backend';
import { Callout, PageHeader } from '../ui/kit';
import LiveProgress from './live/LiveProgress';

/** Progress over time (D165). The demo keeps no history, so it says what the screen shows. */
export default function ProgressScreen() {
  if (useBackend().mode === 'live') return <LiveProgress />;
  return (
    <>
      <PageHeader title="Progress over time" />
      <Callout title="Built up day by day">
        On a real server, this screen shows open risks, exposed and unlabelled sensitive files, files not read and shares with no owner day by day, with what
        changed over the last 30 days, 90 days or year. The demo keeps no history.
      </Callout>
    </>
  );
}
