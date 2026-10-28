import type { SearchHit, SearchKind, SearchResult } from '../../api/client';
import type { Route } from '../../App';

/** Searches shorter than this aren't sent; the server refuses them too. */
export const MIN_SEARCH = 2;

/** File and folder names need this many characters (the server matches them by trigrams). */
export const MIN_FILE_SEARCH = 3;

export const SEARCH_KIND_LABEL: Record<SearchKind, string> = {
  User: 'People',
  Group: 'Groups',
  Share: 'Shares',
  Server: 'File servers',
  Folder: 'Folders',
  File: 'Files',
};

/** Where picking a result goes: the screen and the deep link it understands. */
export function searchTarget(kind: SearchKind, hit: Pick<SearchHit, 'id'>): { route: Route; param?: string } {
  switch (kind) {
    case 'User':
      return { route: 'access', param: `user:${hit.id}` };
    case 'Group':
      return { route: 'access', param: `group:${hit.id}` };
    case 'Share':
      return { route: 'shares', param: hit.id };
    case 'Folder':
      return { route: 'access', param: `folder:${hit.id}` };
    case 'File':
      return { route: 'discovery', param: `file:${hit.id}` };
    case 'Server':
      return { route: 'setup' };
  }
}

export interface SearchRow {
  key: string;
  kind: SearchKind;
  hit: SearchHit;
}

/**
 * Results in the order they're shown, grouped by kind, for moving through them with the arrow keys. A kind holding an
 * exact match for a pasted path comes first, with that match at its top.
 */
export function searchRows(result: SearchResult | undefined): SearchRow[] {
  if (!result) return [];
  const exactFirst = <T,>(xs: T[], exact: (x: T) => boolean) => [...xs.filter(exact), ...xs.filter((x) => !exact(x))];
  return exactFirst(result.groups, (g) => g.items.some((h) => h.exact)).flatMap((g) =>
    exactFirst(g.items, (h) => h.exact).map((hit) => ({ key: `${g.kind}:${hit.id}`, kind: g.kind, hit })),
  );
}

/**
 * A long \\server\share\path cut in the middle, keeping the server, the share and the folders nearest the item,
 * which are what tell two copies of the same name apart.
 */
export function shortPath(path: string, max = 44): string {
  if (path.length <= max || !path.startsWith('\\\\')) return path;
  const parts = path.slice(2).split('\\');
  if (parts.length <= 3) return path;
  const head = `\\\\${parts[0]}\\${parts[1]}\\…`;
  let tail = '';
  for (let i = parts.length - 1; i >= 2; i--) {
    const next = `\\${parts[i]}${tail}`;
    if (tail && head.length + next.length > max) break;
    tail = next;
  }
  return head + tail;
}

/** Whether the search looks like a pasted \\server\share path. */
export const looksLikePath = (q: string) => /^(\\\\|\/\/)/.test(q.trim());

/** The line under the results that says what wasn't searched or didn't finish, if anything. */
export function searchFootnote(result: SearchResult | undefined): string | undefined {
  if (!result) return undefined;
  const slow = result.groups.filter((g) => g.timedOut).map((g) => SEARCH_KIND_LABEL[g.kind].toLowerCase());
  if (slow.length) return `Searching ${slow.join(' and ')} took too long. A longer search narrows it.`;
  if (!result.filesSearched) return `Type ${MIN_FILE_SEARCH} or more characters to search folders and files too.`;
  if (result.groups.some((g) => g.more)) return 'Showing the first few of each. A longer search narrows them.';
  if (!looksLikePath(result.query) && result.directoryAt === null) return 'People and groups appear after the first Active Directory sync.';
  return undefined;
}
