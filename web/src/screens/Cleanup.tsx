import { useMemo } from 'react';
import { useApp } from '../api/store';
import { useBackend } from '../api/backend';
import { NOW } from '../data/seed';
import LiveCleanup, { CleanupView } from './live/LiveCleanup';
import { demoSource } from './live/cleanup';

export default function Cleanup() {
  const backend = useBackend();
  if (backend.mode === 'live') return <LiveCleanup />;
  return <DemoCleanup />;
}

/** The demo company, worked out in the browser the same way the server does. */
function DemoCleanup() {
  const s = useApp();
  const source = useMemo(() => demoSource({ files: s.files, shares: s.shares, now: NOW }), [s.files, s.shares]);
  return <CleanupView source={source} demo />;
}
