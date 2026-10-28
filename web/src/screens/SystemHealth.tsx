import { useBackend } from '../api/backend';
import { Callout, Card, PageHeader } from '../ui/kit';
import LiveHealth from './live/LiveHealth';

export default function SystemHealth() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LiveHealth />;
  return (
    <>
      <PageHeader title="System health" sub="The daily check of your Data Scope server: services, scans, Active Directory, email, backups, disk space and certificates." />
      <Card title="On your Data Scope server">
        <Callout tone="info">A real server checks here that both services are running, scans and the directory sync finish, emails go out, last night's backup was taken, disks have room, and certificates are in date. The demo company has no server to check.</Callout>
      </Card>
    </>
  );
}
