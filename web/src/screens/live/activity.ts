import type { ActivityAlert, ActivityCertainty, UnusualSource, ActivityDay, ActivityEvent, ActivityFilters, ActivityGap, ActivityOperation, ActivitySource, SecurityLogHealth } from '../../api/client';

/**
 * How much each kind of event proves. The wording is the product's promise: a share access check or a request is
 * never shown as something that happened to a file.
 */
export const CERTAINTY: Record<ActivityCertainty, { label: string; tone: 'good' | 'warn' | 'neutral'; help: string }> = {
  Performed: { label: 'Recorded', tone: 'good', help: 'Windows recorded the operation on the file or folder (events 4663, 4660, 4670).' },
  Requested: { label: 'Request', tone: 'warn', help: 'Someone asked Windows to open it with these rights (4656). This does not show what they did with it.' },
  AccessCheck: { label: 'Access check only', tone: 'neutral', help: 'Windows checked access through the share (5145). This is not proof the file was opened or read.' },
  ShareSession: { label: 'Share connection', tone: 'neutral', help: 'Someone connected to the share (5140). This does not show which files they used.' },
};

/** Operations offered in the filter, in the order people look for them. */
export const OPERATION_FILTERS: { id: ActivityOperation; label: string }[] = [
  { id: 'Read', label: 'Read or list' },
  { id: 'Write', label: 'Write or create' },
  { id: 'Deleted', label: 'Deleted' },
  { id: 'DeleteOrRename', label: 'Delete or rename' },
  { id: 'PermissionChange', label: 'Permission change' },
  { id: 'OwnerChange', label: 'Owner change' },
  { id: 'HandleRequest', label: 'Open request' },
  { id: 'ShareAccessCheck', label: 'Share access check' },
  { id: 'ShareConnect', label: 'Connected to share' },
  { id: 'LogCleared', label: 'Security log cleared' },
];

/** Where the event happened, for the table: share and path, the server's own path, or why there is none. */
export function whereText(e: ActivityEvent): { path: string | null; note: string | null } {
  if (e.shareName && e.relativePath !== null) {
    const path = e.relativePath ? `${e.shareName}\\${e.relativePath}` : `${e.shareName} (the share itself)`;
    return { path, note: e.pathSource === 'Correlated' ? `path from event #${e.correlatedRecordId} on the same handle` : null };
  }
  if (e.objectPath) {
    return {
      path: e.objectPath,
      note: e.pathSource === 'Correlated' ? `path from event #${e.correlatedRecordId}; not in a scanned share` : 'not in a scanned share',
    };
  }
  if (e.operation === 'Deleted') return { path: null, note: 'Windows does not name what was deleted, and no earlier event on the same handle was collected' };
  if (e.operation === 'LogCleared') return { path: null, note: 'events before this may be missing' };
  return { path: null, note: 'no path in the event' };
}

export interface Series {
  key: 'reads' | 'writes' | 'deletes' | 'permissionChanges';
  label: string;
  color: string;
}

/** Fixed order and colours: a series keeps its colour whatever else is shown. */
export const SERIES: Series[] = [
  { key: 'reads', label: 'Reads', color: 'var(--series-1)' },
  { key: 'writes', label: 'Writes', color: 'var(--series-2)' },
  { key: 'deletes', label: 'Deletes', color: 'var(--series-3)' },
  { key: 'permissionChanges', label: 'Permission changes', color: 'var(--series-4)' },
];

export const dayTotal = (d: ActivityDay) => SERIES.reduce((n, s) => n + d[s.key], 0);

/** One line for a source's health, from the most important fact down. */
export function sourceStatus(s: ActivitySource): { text: string; tone: 'good' | 'warn' | 'bad' | 'neutral' } {
  if (!s.enabled) return { text: 'Off', tone: 'neutral' };
  if (s.state === 'Error') return { text: s.lastError ?? 'The last read failed.', tone: 'bad' };
  if (s.state === 'Waiting') return { text: 'Waiting for the first read', tone: 'warn' };
  return { text: 'Collecting', tone: 'good' };
}

const mb = (bytes: number) => `${Math.max(1, Math.round(bytes / 1048576)).toLocaleString()} MB`;
const plural = (n: number, one: string, many: string) => `${n.toLocaleString()} ${n === 1 ? one : many}`;

/**
 * What the file server's Security log holds, and a warning when Data Scope could fall behind it. The log keeps all
 * kinds of events, so its span is how far back anything could be collected, not how much file activity there was.
 */
export function logHealth(log: SecurityLogHealth, now = Date.now()): { text: string; warning: string | null } {
  const parts: string[] = [];
  if (log.oldestEventAt) {
    const days = (now - new Date(log.oldestEventAt).getTime()) / 86_400_000;
    const span = days < 1 ? `${Math.max(1, Math.round(days * 24))} hours` : plural(Math.round(days), 'day', 'days');
    parts.push(`The Security log goes back about ${span}`);
  } else {
    parts.push('The Security log is empty');
  }
  if (log.sizeBytes !== null && log.maxSizeBytes) parts.push(`${mb(log.sizeBytes)} of ${mb(log.maxSizeBytes)}`);
  else if (log.sizeBytes !== null) parts.push(mb(log.sizeBytes));
  let warning: string | null = null;
  if (log.atRisk && log.behind !== null) {
    warning = `${plural(log.behind, 'log record', 'log records')} not read yet, more than half the log. It could overwrite events before Data Scope reads them.`;
  } else if (log.full) {
    warning = 'The Security log is full. Depending on its settings, Windows either overwrites the oldest events or stops logging.';
  }
  return { text: parts.join(' · '), warning };
}

/** A gap in plain words. Record ids count every event in the log, so some missing records may not be file events. */
export function gapText(g: ActivityGap): string {
  if (g.reason === 'LogReset') {
    return `The Security log started again (cleared or replaced). Any events after record ${g.firstRecordId.toLocaleString()} that had not been read are lost.`;
  }
  const n = g.records ?? 0;
  return `${plural(n, 'log record was', 'log records were')} overwritten or cleared before Data Scope read ${n === 1 ? 'it' : 'them'} (records ${g.firstRecordId.toLocaleString()} to ${(g.lastRecordId ?? g.firstRecordId).toLocaleString()}). Some may not have been file events.`;
}

/**
 * The event filters that show what an alert is about: the server, the person, the kind of event and the alert's time
 * span (a second either side). Null for missed events, which by definition have no events to show.
 */
export function alertFilters(a: ActivityAlert): { filters: ActivityFilters; person?: { sid: string; name: string } } | null {
  if (a.rule === 'EventsMissed') return null;
  const from = new Date(new Date(a.firstEventAt).getTime() - 1000).toISOString();
  const to = new Date(new Date(a.lastEventAt).getTime() + 1000).toISOString();
  return {
    // Unusual behaviour counts share access checks as touching a file, so they are shown too (D166).
    filters: { serverId: a.serverId, operation: a.operation ?? undefined, outcome: a.outcome ?? undefined, from, to, includeChecks: a.rule === 'ManyDenied' || a.unusual || undefined },
    person: a.principalSid ? { sid: a.principalSid, name: a.principalName ?? a.principalSid } : undefined,
  };
}

/**
 * How far unusual behaviour has got on one server (D166): how many people it has learned and how many have
 * enough history to be judged. A person still being learned raises no unusual-behaviour alerts, so this is coverage.
 */
export function unusualLine(s: UnusualSource, minActiveDays: number): string {
  const where = s.hostname;
  if (s.people === 0) return `${where}: no activity to learn from yet.`;
  if (s.judged === 0) return `${where}: learning ${plural(s.people, 'person', 'people')}. Each is judged once they have been active on ${minActiveDays} days.`;
  const learning = s.people - s.judged;
  return `${where}: ${s.judged.toLocaleString()} of ${plural(s.people, 'person', 'people')} judged against their own past` +
    (learning > 0 ? `; ${learning.toLocaleString()} still being learned.` : '.');
}
