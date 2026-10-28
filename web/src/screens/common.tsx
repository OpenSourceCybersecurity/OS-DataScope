import type { AppState } from '../api/store';
import { whoHasAccess } from '../domain/access';
import { effectiveClass } from '../domain/risk';
import type { Classification, FileItem, Share } from '../domain/types';

export interface ShareStats {
  share: Share;
  files: FileItem[];
  byClass: Record<Classification, number>;
  sensitive: number;
  unscanned: number;
  peopleWithAccess: number;
  bytes: number;
}

const cache = new WeakMap<object, Map<string, ShareStats>>();

/** Cached per state version + share so screens can call it freely. */
export function shareStats(s: AppState, shareId: string): ShareStats {
  let m = cache.get(s);
  if (!m) cache.set(s, (m = new Map()));
  const hit = m.get(shareId);
  if (hit) return hit;
  const share = s.shares.find((x) => x.id === shareId)!;
  const files = s.files.filter((f) => f.shareId === shareId);
  const byClass: Record<Classification, number> = { Unknown: 0, Public: 0, Internal: 0, Confidential: 0, Restricted: 0 };
  for (const f of files) byClass[effectiveClass(f)]++;
  const stats: ShareStats = {
    share,
    files,
    byClass,
    sensitive: byClass.Confidential + byClass.Restricted,
    unscanned: files.filter((f) => f.scanStatus !== 'scanned').length,
    peopleWithAccess: whoHasAccess(s.dir, share).filter((a) => s.dir.get(a.userSid)?.enabled).length,
    bytes: files.reduce((a, f) => a + f.size, 0),
  };
  m.set(shareId, stats);
  return stats;
}

export const activeUsers = (s: AppState) => [...s.dir.values()].filter((p) => p.type === 'user' && p.enabled).length;

export function sharePath(s: AppState, f: FileItem) {
  const share = s.shares.find((x) => x.id === f.shareId)!;
  return `${share.unc}\\${f.path.replace(/\//g, '\\')}`;
}

export function ownedShareIds(s: AppState, sid: string) {
  return s.owners.filter((o) => o.confirmed && (o.ownerSid === sid || o.backupOwnerSid === sid)).map((o) => o.shareId);
}
