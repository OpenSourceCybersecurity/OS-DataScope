import type {
  CleanupScope,
  CleanupSummary,
  DuplicateGroup,
  DuplicateList,
  Page,
  Sensitivity,
  StaleFile,
} from '../../api/client';
import type { FileItem, Share } from '../../domain/types';

/** The thresholds people pick from; the server accepts 1 to 30, for long retention holds. */
export const YEARS = [1, 2, 3, 5, 7, 10, 15, 20, 25, 30] as const;

/** The choices for a threshold, with the server's configured one added when it isn't a usual step. */
export function yearChoices(current: number): number[] {
  return [...new Set<number>([...YEARS, current])].sort((a, b) => a - b);
}

/** What the Stale & duplicates screen opens on: a share, and whether only sensitive stale files are listed. */
export interface CleanupStart {
  shareId: string;
  sensitive: boolean;
}

/** "sh1", "sensitive" or "sensitive:sh1": the page parameter other screens link with. */
export function cleanupParam(shareId: string | undefined, sensitive: boolean): string | undefined {
  if (!sensitive) return shareId || undefined;
  return shareId ? `sensitive:${shareId}` : 'sensitive';
}

export function parseCleanupParam(param: string | undefined): CleanupStart {
  if (!param) return { shareId: '', sensitive: false };
  // Old links with a source filter open on everything.
  if (param.startsWith('source:')) return { shareId: '', sensitive: false };
  if (param === 'sensitive') return { shareId: '', sensitive: true };
  if (param.startsWith('sensitive:')) return { shareId: param.slice('sensitive:'.length), sensitive: true };
  return { shareId: param, sensitive: false };
}

export const yearsLabel = (y: number) => (y === 1 ? '1 year' : `${y} years`);

export const filesText = (n: number) => (n === 1 ? '1 file' : `${n.toLocaleString('en-US')} files`);

/** What one place is called, and in the filter for all of them. */
export const PLACE = { one: 'Share', all: 'All shares', by: 'By share' } as const;

/** Whole percent, never 0% for something that is there or 100% for something that isn't all. */
export function percent(part: number, whole: number): string {
  if (whole <= 0 || part <= 0) return '0%';
  const p = (part / whole) * 100;
  if (p < 1) return '<1%';
  if (p > 99 && part < whole) return '>99%';
  return `${Math.round(p)}%`;
}

/** What file activity can and can't tell about these files, in one sentence. */
export function activityNote(s: Pick<CleanupSummary, 'servers' | 'activityServers' | 'years'>): string {
  const since = yearsLabel(s.years);
  if (s.servers === 0) return '';
  if (s.activityServers === 0)
    return `File activity isn't collected on ${s.servers === 1 ? 'this server' : 'these servers'}, so files people only open and never change look stale too.`;
  if (s.activityServers < s.servers)
    return `File activity is collected on ${s.activityServers} of ${s.servers} servers. There, files someone opened in the last ${since} are not counted; elsewhere, files people only open look stale too.`;
  return `Files someone opened in the last ${since}, as recorded by file activity, are not counted.`;
}

/** Sensitive files among the stale ones: "1 restricted, 4 confidential". */
export function sensitiveText(restricted: number, confidential: number): string {
  return [restricted && `${restricted.toLocaleString('en-US')} restricted`, confidential && `${confidential.toLocaleString('en-US')} confidential`]
    .filter(Boolean)
    .join(', ');
}

/** Where the data comes from: the live server, or the demo company worked out in the browser. */
export interface CleanupSource {
  /** Without years, the server's configured threshold. */
  summary: (scope: CleanupScope, years?: number) => Promise<CleanupSummary>;
  staleFiles: (scope: CleanupScope & { folderId?: string; sensitive?: boolean }, years: number, sort: 'size' | 'oldest', offset: number) => Promise<Page<StaleFile>>;
  duplicates: (scope: CleanupScope, offset: number) => Promise<DuplicateList>;
}

// ---------- The demo company, worked out the same way the server does (D119) ----------

const PAGE = 50;
const MIN_DUPLICATE_BYTES = 1024;
const label = (f: FileItem): Sensitivity => f.override?.classification ?? f.policyClassification;
const folderOf = (f: FileItem) => f.path.split('/').slice(0, -1).join('\\');
const uncOf = (share: Share, folder: string) => (folder ? `${share.unc}\\${folder}` : share.unc);
/** The later of modified and created: a recent copy of an old file is not stale. */
const changed = (f: FileItem) => (f.created > f.modified ? f.created : f.modified);

export function cutoffOf(years: number, now: Date): string {
  const d = new Date(now);
  d.setUTCFullYear(d.getUTCFullYear() - years);
  return d.toISOString();
}

interface DemoData {
  files: FileItem[];
  shares: Share[];
  now?: Date;
}

function inScope(d: DemoData, scope: CleanupScope): { files: FileItem[]; shares: Share[] } {
  const shares = d.shares.filter((s) => (!scope.shareId || s.id === scope.shareId) && (!scope.serverId || s.serverId === scope.serverId));
  const ids = new Set(shares.map((s) => s.id));
  return { shares, files: d.files.filter((f) => ids.has(f.shareId)) };
}

function staleOf(files: FileItem[], cutoff: string) {
  return files.filter((f) => changed(f) < cutoff);
}

function groupsOf(files: FileItem[]): FileItem[][] {
  const groups = new Map<string, FileItem[]>();
  for (const f of files) {
    if (f.size < MIN_DUPLICATE_BYTES) continue;
    const key = `${f.name.toUpperCase()}|${f.size}`;
    groups.set(key, [...(groups.get(key) ?? []), f]);
  }
  return [...groups.values()].filter((g) => g.length > 1).sort((a, b) => (b.length - 1) * b[0].size - (a.length - 1) * a[0].size || b.length - a.length);
}

const RANK: Sensitivity[] = ['Unknown', 'Public', 'Internal', 'Confidential', 'Restricted'];

export function demoSource(d: DemoData): CleanupSource {
  const now = d.now ?? new Date();
  const shareById = (id: string) => d.shares.find((s) => s.id === id)!;
  return {
    async summary(scope, picked) {
      const years = picked ?? 3;
      const { files, shares } = inScope(d, scope);
      const cutoff = cutoffOf(years, now);
      const stale = staleOf(files, cutoff);
      const count = (xs: FileItem[], l: Sensitivity) => xs.filter((f) => label(f) === l).length;
      const bytes = (xs: FileItem[]) => xs.reduce((n, f) => n + f.size, 0);
      const groups = groupsOf(files);
      const byFolder = new Map<string, FileItem[]>();
      for (const f of stale) byFolder.set(`${f.shareId}|${folderOf(f)}`, [...(byFolder.get(`${f.shareId}|${folderOf(f)}`) ?? []), f]);
      return {
        years,
        defaultYears: 3,
        cutoff,
        totals: {
          files: files.length,
          bytes: bytes(files),
          staleFiles: stale.length,
          staleBytes: bytes(stale),
          staleRestricted: count(stale, 'Restricted'),
          staleConfidential: count(stale, 'Confidential'),
          inUse: 0,
          noDate: 0,
          duplicateGroups: groups.length,
          duplicateCopies: groups.reduce((n, g) => n + g.length - 1, 0),
          duplicateBytes: groups.reduce((n, g) => n + (g.length - 1) * g[0].size, 0),
        },
        shares: shares
          .map((s) => {
            const mine = files.filter((f) => f.shareId === s.id);
            const old = stale.filter((f) => f.shareId === s.id);
            return {
              shareId: s.id,
              serverId: s.serverId,
              uncPath: s.unc,
              files: mine.length,
              bytes: bytes(mine),
              staleFiles: old.length,
              staleBytes: bytes(old),
              staleRestricted: count(old, 'Restricted'),
              staleConfidential: count(old, 'Confidential'),
              lastScannedAt: s.scan.lastScan ?? null,
            };
          })
          .sort((a, b) => b.staleBytes - a.staleBytes || a.uncPath.localeCompare(b.uncPath)),
        folders: [...byFolder.entries()]
          .map(([key, xs]) => {
            const [shareId, folder] = key.split('|');
            return {
              folderId: key,
              shareId,
              uncPath: uncOf(shareById(shareId), folder),
              staleFiles: xs.length,
              staleBytes: bytes(xs),
              restricted: count(xs, 'Restricted'),
              confidential: count(xs, 'Confidential'),
              newestChange: xs.map(changed).sort().at(-1) ?? null,
            };
          })
          .sort((a, b) => b.staleBytes - a.staleBytes || b.staleFiles - a.staleFiles)
          .slice(0, 50),
        servers: new Set(shares.map((s) => s.serverId)).size,
        activityServers: 0,
        minDuplicateBytes: MIN_DUPLICATE_BYTES,
        lastScanAt: shares.map((s) => s.scan.lastScan).filter(Boolean).sort().at(-1) ?? null,
      };
    },
    async staleFiles(scope, years, sort, offset) {
      let stale = staleOf(inScope(d, scope).files, cutoffOf(years, now));
      if (scope.folderId) stale = stale.filter((f) => `${f.shareId}|${folderOf(f)}` === scope.folderId);
      if (scope.sensitive) stale = stale.filter((f) => label(f) === 'Restricted' || label(f) === 'Confidential');
      stale.sort(sort === 'oldest' ? (a, b) => changed(a).localeCompare(changed(b)) || b.size - a.size : (a, b) => b.size - a.size || changed(a).localeCompare(changed(b)));
      return {
        items: stale.slice(offset, offset + PAGE).map((f) => ({
          fileId: f.id,
          shareId: f.shareId,
          folderId: `${f.shareId}|${folderOf(f)}`,
          path: uncOf(shareById(f.shareId), folderOf(f)),
          name: f.name,
          size: f.size,
          lastChanged: changed(f),
          label: label(f),
        })),
        limit: PAGE,
        offset,
        total: stale.length,
      };
    },
    async duplicates(scope, offset) {
      const groups = groupsOf(inScope(d, scope).files);
      const items: DuplicateGroup[] = groups.slice(offset, offset + PAGE).map((g) => ({
        name: g[0].name,
        size: g[0].size,
        copies: g.length,
        shares: new Set(g.map((f) => f.shareId)).size,
        wasted: (g.length - 1) * g[0].size,
        sameModified: g.every((f) => f.modified === g[0].modified),
        label: g.map(label).sort((a, b) => RANK.indexOf(b) - RANK.indexOf(a))[0],
        locations: g.map((f) => ({ fileId: f.id, shareId: f.shareId, path: uncOf(shareById(f.shareId), folderOf(f)), modified: f.modified, label: label(f) })),
      }));
      return {
        items,
        limit: PAGE,
        offset,
        total: groups.length,
        copies: groups.reduce((n, g) => n + g.length - 1, 0),
        wasted: groups.reduce((n, g) => n + (g.length - 1) * g[0].size, 0),
        minDuplicateBytes: MIN_DUPLICATE_BYTES,
      };
    },
  };
}
