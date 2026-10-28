import { useBackend } from '../api/backend';
import { Card, PageHeader } from '../ui/kit';
import LivePersonSearch from './live/LivePersonSearch';

export default function PersonSearchScreen() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LivePersonSearch />;
  return (
    <>
      <PageHeader
        title="Find a person"
        sub="Everything Data Scope holds about one person, for a subject access or other personal-data request."
      />
      <Card title="On your Data Scope server">
        <p className="text-[13px] text-ink-2 max-w-2xl">
          A platform admin picks someone from Active Directory, or types their names and email addresses, and gives a reason. Data Scope lists files and
          folders named after them, files where a scan saw their name, folders they own, and their file activity, with a download for the request. Every search is in the audit log. The demo company has no people to search.
        </p>
      </Card>
    </>
  );
}
