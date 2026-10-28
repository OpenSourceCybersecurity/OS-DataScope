// Detects whether the UI is served by a Data Scope server (live data) or running
// standalone (the prototype on synthetic data). Checked once per page load.

import { useSyncExternalStore } from 'react';
import { ApiError, api, type ContentState, type Me } from './client';

/**
 * checking: finding out whether a Data Scope server is behind this page.
 * signed-out: on a server, waiting for the person to choose Sign in (or the demo).
 * signing-in: Windows sign-in in progress.
 * demo: synthetic data. `chosen` when someone on a real server opened it from the sign-in page.
 * signin-failed: the server refused or could not complete sign-in (`unavailable` when the server itself failed).
 */
export type Backend =
  | { mode: 'checking' }
  | { mode: 'signed-out' }
  | { mode: 'signing-in' }
  | { mode: 'demo'; chosen?: boolean }
  | { mode: 'live'; me: Me }
  | { mode: 'signin-failed'; unavailable?: boolean };

/**
 * A Data Scope server answers /me with 401 when Windows sign-in didn't work. That
 * must never fall back to demo data, or someone could mistake the synthetic
 * company for their own. Anything else that fails (no server, a static host)
 * means the UI is running as the standalone prototype.
 */
export function backendFromProbeError(error: unknown): Backend {
  return error instanceof ApiError && (error.status === 401 || error.status === 403) ? { mode: 'signin-failed' } : { mode: 'demo' };
}

/**
 * Signed in, but in none of the AD groups mapped to a Data Scope role, and not a data owner either. Owners need no
 * role: they see their own reviews.
 */
export const hasNoRole = (b: Backend) => b.mode === 'live' && b.me.roles.length === 0 && !isOwner(b.me);

/** Owns or backs up something, or has a review waiting. */
export const isOwner = (me: Me) => (me.owns ?? 0) > 0 || (me.openReviews ?? 0) > 0;

const hasAny = (b: Backend, roles: string[]) => b.mode === 'live' && b.me.roles.some((r) => roles.includes(r));

/** Matches the server's policies. The server enforces them; these only decide what to show. */
export const canReadPosture = (b: Backend) => hasAny(b, ['PlatformAdmin', 'SecurityAnalyst', 'Auditor', 'Viewer']);
/** Redacted evidence snippets. Viewers see counts only. */
export const canReadEvidence = (b: Backend) => hasAny(b, ['PlatformAdmin', 'SecurityAnalyst', 'Auditor']);
export const canManageReviews = (b: Backend) => hasAny(b, ['PlatformAdmin']);
export const canReadReviews = (b: Backend) => hasAny(b, ['PlatformAdmin', 'SecurityAnalyst', 'Auditor']);
/** Matches the server's TriageRisks policy: accept, snooze and send risks to IT. The server enforces it; this only hides controls. */
export const canTriageRisks = (b: Backend) => hasAny(b, ['PlatformAdmin', 'SecurityAnalyst']);
/** File activity names people and what they touched: not for Viewers (D101). */
export const canReadActivity = (b: Backend) => hasAny(b, ['PlatformAdmin', 'SecurityAnalyst', 'Auditor']);
/** System settings (D123): administrators change them, auditors see them. */
export const canReadSettings = (b: Backend) => hasAny(b, ['PlatformAdmin', 'Auditor']);
/** Acknowledging or reopening an activity alert (D105). Auditors can read alerts but not change them. */
export const canTriageActivity = (b: Backend) => hasAny(b, ['PlatformAdmin', 'SecurityAnalyst']);

let state: Backend = { mode: 'checking' };
const listeners = new Set<() => void>();
let started = false;

function set(next: Backend) {
  state = next;
  listeners.forEach((l) => l());
}

/** Remembered per browser once sign-in worked, so the next visit signs in straight away. Never holds a credential. */
const AUTO_KEY = 'datascope.signIn';

function remember(on: boolean) {
  try {
    if (on) localStorage.setItem(AUTO_KEY, 'auto');
    else localStorage.removeItem(AUTO_KEY);
  } catch {
    /* storage blocked: the person just sees the sign-in page each time */
  }
}

function remembered(): boolean {
  try {
    return localStorage.getItem(AUTO_KEY) === 'auto';
  } catch {
    return false;
  }
}

interface Probes {
  info: () => Promise<unknown>;
  me: () => Promise<Me>;
}
let probes: Probes = { info: () => api.signInInfo(), me: () => api.me() };

/**
 * On a Data Scope server, shows the sign-in page first instead of starting Windows sign-in on page load, which makes
 * some browsers pop up a password box. Once someone has signed in on this browser, later visits sign in directly.
 * Without a server (a static host), the UI is the standalone demo.
 */
export function startBackendCheck(p: Partial<Probes> = {}) {
  if (started) return;
  started = true;
  probes = { ...probes, ...p };
  probes.info().then(
    () => (remembered() ? signIn() : set({ mode: 'signed-out' })),
    () => set({ mode: 'demo' }),
  );
}

/** Windows sign-in: the browser sends the person's domain sign-in with this request. */
export function signIn(): Promise<void> {
  set({ mode: 'signing-in' });
  return probes.me().then(
    (me) => {
      remember(true);
      set({ mode: 'live', me });
    },
    (error) => {
      remember(false);
      const refused = error instanceof ApiError && (error.status === 401 || error.status === 403);
      set({ mode: 'signin-failed', unavailable: !refused });
    },
  );
}

/** The synthetic Northwind company, opened from the sign-in page. Never mixed with real data. */
export function exploreDemo() {
  set({ mode: 'demo', chosen: true });
}

/**
 * Back to the sign-in page, and no automatic sign-in on the next visit. Windows sign-in has no real sign-out: the
 * browser keeps the person's Windows identity, so signing in again needs no password.
 */
export function signOut() {
  remember(false);
  set({ mode: 'signed-out' });
}

/** The current state, for code outside React. */
export const currentBackend = () => state;

/** Tests only. */
export function resetBackendForTests() {
  started = false;
  state = { mode: 'checking' };
  probes = { info: () => api.signInInfo(), me: () => api.me() };
}

export function useBackend(): Backend {
  startBackendCheck();
  return useSyncExternalStore(
    (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    () => state,
  );
}

export const canManage = (b: Backend) => b.mode === 'live' && b.me.roles.includes('PlatformAdmin');

/** Matches the server's ReviewClassification policy. The server enforces it; this only hides controls. */
export const canReviewClassification = (b: Backend) => b.mode === 'live' && b.me.roles.some((r) => r === 'PlatformAdmin' || r === 'SecurityAnalyst');

/** Plain-language reasons a file's contents were not (fully) read. Not read is never the same as clean. */
export const CONTENT_STATE_LABEL: Record<ContentState, string> = {
  NotRead: 'Not read yet',
  Scanned: 'Read in full',
  Partial: 'Partly read',
  Unsupported: 'File type not read',
  TooLarge: 'Too large to read',
  Offline: 'Offline or in the cloud',
  AccessDenied: 'Access denied',
  NotFound: 'Moved or deleted',
  Encrypted: 'Password-protected',
  Unreadable: 'Damaged or unsafe',
  TimedOut: 'Took too long',
  IoError: 'Read error',
  Excluded: 'Excluded by an administrator',
};

/** Plain-language names for coverage gaps, written for the people who will act on them. */
export const GAP_LABEL: Record<string, { title: string; fix: string }> = {
  ShareUnavailable: { title: 'Share could not be opened', fix: 'Check the share still exists and the scan account has Read on it.' },
  AccessDenied: { title: 'Access denied', fix: 'Give the scan account Read (List folder) on this folder, or accept it as a known gap.' },
  NotFound: { title: 'Moved or deleted during the scan', fix: 'Usually harmless. The next scan will pick up where it lives now.' },
  PathTooLong: { title: 'Path too long', fix: 'Shorten deeply nested folder names; Windows refused the path.' },
  ReparsePointSkipped: { title: 'Link not followed', fix: 'Junctions and symbolic links are skipped to avoid loops. Scan the target share directly.' },
  DepthLimit: { title: 'Too deep', fix: 'Folders below the depth limit were not listed. Raise Scanner:MaxDepth if this is real data.' },
  FolderEntryLimit: { title: 'Folder too large', fix: 'This folder holds more items than the per-folder limit. Raise Scanner:MaxEntriesPerFolder if needed.' },
  JobEntryLimit: { title: 'Scan size limit reached', fix: 'The scan stopped at its item limit. Raise Scanner:MaxEntriesPerJob or split the server into smaller scans.' },
  UnrepresentableName: { title: 'Unusual file name', fix: 'The name was stored with replacement characters or a case-only duplicate was skipped.' },
  IoError: { title: 'Read error', fix: 'Windows reported an I/O error. Check the server event log and rescan.' },
  Excluded: { title: 'Excluded by an administrator', fix: 'Left out on purpose on Scan scope & exclusions. Turn the exclusion off there to scan it again.' },
};
