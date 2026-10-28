// Sensitive Data Exposure Map (D125): pure helpers for the screen, kept apart so they can be unit tested.

import type { ExposureMapFolder, ExposureMapShare, ExposureMapStatus } from '../../api/client';
import { fmtNum } from '../../ui/kit';

/** How broadly a folder's sensitive files can be read, worst first. The map colours folders by this. */
export type Breadth = 'open' | 'many' | 'some' | 'few' | 'none' | 'unknown';

export const MANY = 25;
export const FEW = 5;

export const BREADTH: Record<Breadth, { label: string; color: string }> = {
  open: { label: 'Anyone in the domain', color: 'var(--s-critical)' },
  many: { label: `More than ${MANY} people`, color: 'var(--s-high)' },
  some: { label: `${FEW + 1} to ${MANY} people`, color: 'var(--s-medium)' },
  few: { label: `1 to ${FEW} people`, color: 'var(--s-low)' },
  none: { label: 'No one listed', color: 'var(--s-good)' },
  unknown: { label: 'Unknown: permissions not read', color: 'var(--c-unknown)' },
};

export const BREADTH_ORDER: Breadth[] = ['open', 'many', 'some', 'few', 'none', 'unknown'];

type Reach = Pick<ExposureMapFolder, 'status' | 'people' | 'peoplePossible'>;

/**
 * Breadth at worst: people who can read plus people who might, so an uncertain folder is never drawn as narrower
 * than it could be ({@link uncertain} marks it striped). "Possibly open to everyone" counts as open.
 */
export function breadthOf(f: Reach): Breadth {
  if (f.status === 'Unknown') return 'unknown';
  if (f.status === 'Open' || f.status === 'PossiblyOpen') return 'open';
  const n = f.people + f.peoplePossible;
  if (n > MANY) return 'many';
  if (n > FEW) return 'some';
  return n > 0 ? 'few' : 'none';
}

/** Whether some of a folder's access is only possible, so the map draws it striped. */
export const uncertain = (f: Reach) => f.status === 'PossiblyOpen' || f.status === 'Unknown' || f.peoplePossible > 0;

export const STATUS: Record<ExposureMapStatus, { label: string; tone: 'bad' | 'warn' | 'neutral' | 'good' }> = {
  Open: { label: 'Open to everyone', tone: 'bad' },
  PossiblyOpen: { label: 'Possibly open to everyone', tone: 'warn' },
  Unknown: { label: 'Unknown', tone: 'neutral' },
  Limited: { label: 'Only some people', tone: 'good' },
};

/** "12 people", "12 people, 3 more possibly", "possibly 3 people", "no one listed". */
export function peopleText(people: number, possible: number): string {
  const n = (x: number) => `${fmtNum(x)} ${x === 1 ? 'person' : 'people'}`;
  if (people === 0 && possible === 0) return 'No one listed';
  if (people === 0) return `Possibly ${n(possible)}`;
  return possible > 0 ? `${n(people)}, ${fmtNum(possible)} more possibly` : n(people);
}

/** "3 Restricted, 12 Confidential" with zero parts left out. */
export function filesText(restricted: number, confidential: number): string {
  const parts = [restricted ? `${fmtNum(restricted)} Restricted` : '', confidential ? `${fmtNum(confidential)} Confidential` : ''].filter(Boolean);
  return parts.length ? parts.join(', ') : 'None';
}

/** The folder's path inside its share, or the share name for its top folder. */
export function folderLabel(f: Pick<ExposureMapFolder, 'uncPath' | 'hostname' | 'shareName'>): string {
  const prefix = `\\\\${f.hostname}\\${f.shareName}`;
  const rest = f.uncPath.toLowerCase().startsWith(prefix.toLowerCase()) ? f.uncPath.slice(prefix.length).replace(/^\\/, '') : f.uncPath;
  return rest || f.shareName;
}

// ---------- Treemap layout ----------

export interface Rect {
  x: number;
  y: number;
  w: number;
  h: number;
}

/**
 * Squarified treemap (Bruls, Huizing and van Wijk): lays out values, largest first, in rows that keep tiles close to
 * square so their areas are easy to compare. Returns one rectangle per value, in the order given. Zero and negative
 * values get an empty rectangle.
 */
export function squarify(values: number[], box: Rect): Rect[] {
  const out: Rect[] = values.map(() => ({ x: box.x, y: box.y, w: 0, h: 0 }));
  const order = values.map((v, i) => ({ v: Math.max(0, v), i })).filter((x) => x.v > 0).sort((a, b) => b.v - a.v);
  const total = order.reduce((a, x) => a + x.v, 0);
  if (!total || box.w <= 0 || box.h <= 0) return out;
  const scale = (box.w * box.h) / total;
  let rest = { ...box };
  let row: { a: number; i: number }[] = [];
  const worst = (r: { a: number }[], side: number) => {
    const sum = r.reduce((s, x) => s + x.a, 0);
    const max = Math.max(...r.map((x) => x.a));
    const min = Math.min(...r.map((x) => x.a));
    return Math.max((side * side * max) / (sum * sum), (sum * sum) / (side * side * min));
  };
  const place = (r: { a: number; i: number }[]) => {
    const sum = r.reduce((s, x) => s + x.a, 0);
    if (rest.w >= rest.h) {
      const w = sum / rest.h;
      let y = rest.y;
      for (const x of r) {
        const h = x.a / w;
        out[x.i] = { x: rest.x, y, w, h };
        y += h;
      }
      rest = { x: rest.x + w, y: rest.y, w: rest.w - w, h: rest.h };
    } else {
      const h = sum / rest.w;
      let x0 = rest.x;
      for (const x of r) {
        const w = x.a / h;
        out[x.i] = { x: x0, y: rest.y, w, h };
        x0 += w;
      }
      rest = { x: rest.x, y: rest.y + h, w: rest.w, h: rest.h - h };
    }
  };
  for (const item of order) {
    const a = item.v * scale;
    const side = Math.min(rest.w, rest.h);
    if (row.length === 0 || worst([...row, { a }], side) <= worst(row, side)) {
      row.push({ a, i: item.i });
    } else {
      place(row);
      row = [{ a, i: item.i }];
    }
  }
  if (row.length) place(row);
  return out;
}

export interface MapTile {
  key: string;
  rect: Rect;
  /** A folder, or several of the share's smaller folders taken together. */
  folder?: ExposureMapFolder;
  /** Smaller folders with the same breadth, or (without breadth) folders not in the list sent. */
  more?: { folders: number; files: number; breadth?: Breadth; uncertain?: boolean };
}
export interface MapBlock {
  share: ExposureMapShare;
  rect: Rect;
  header: boolean;
  tiles: MapTile[];
}

export const MAX_TILES_PER_SHARE = 20;
const HEADER = 22;
const GAP = 2;

/**
 * The map: one block per share with sensitive files, sized by its sensitive files, holding its folders the same way.
 * Past the biggest {@link MAX_TILES_PER_SHARE} folders, the rest are taken together by breadth (so the colours still
 * mean the same), and folders not in the list sent share one grey tile, so every block adds up to the share's total.
 */
export function layoutMap(shares: ExposureMapShare[], folders: ExposureMapFolder[], width: number, height: number): MapBlock[] {
  const withData = shares.filter((s) => s.restricted + s.confidential > 0);
  const blocks = squarify(withData.map((s) => s.restricted + s.confidential), { x: 0, y: 0, w: width, h: height });
  const size = (f: ExposureMapFolder) => f.restricted + f.confidential;
  return withData.map((share, i) => {
    const r = blocks[i];
    const inner = { x: r.x + GAP, y: r.y + GAP, w: Math.max(0, r.w - 2 * GAP), h: Math.max(0, r.h - 2 * GAP) };
    const header = inner.h > 70 && inner.w > 90;
    const area = header ? { ...inner, y: inner.y + HEADER, h: inner.h - HEADER } : inner;
    // Biggest first; among equals, the most exposed, so the tiles drawn on their own are the ones that matter most.
    const own = folders
      .filter((f) => f.shareId === share.shareId)
      .sort((a, b) => size(b) - size(a) || BREADTH_ORDER.indexOf(breadthOf(a)) - BREADTH_ORDER.indexOf(breadthOf(b)) || b.people - a.people);
    const shown = own.slice(0, MAX_TILES_PER_SHARE);
    const groups = new Map<string, { folders: number; files: number; breadth: Breadth; uncertain: boolean }>();
    for (const f of own.slice(MAX_TILES_PER_SHARE)) {
      const b = breadthOf(f);
      const u = uncertain(f);
      const g = groups.get(`${b}:${u}`) ?? { folders: 0, files: 0, breadth: b, uncertain: u };
      g.folders++;
      g.files += size(f);
      groups.set(`${b}:${u}`, g);
    }
    const grouped = [...groups.values()].sort((a, b) => BREADTH_ORDER.indexOf(a.breadth) - BREADTH_ORDER.indexOf(b.breadth) || Number(a.uncertain) - Number(b.uncertain));
    const listedFiles = own.reduce((a, f) => a + size(f), 0);
    const unlisted = { folders: Math.max(0, share.sensitiveFolders - own.length), files: Math.max(0, share.restricted + share.confidential - listedFiles) };
    const more = unlisted.files > 0 ? [...grouped, unlisted] : grouped;
    const rects = squarify([...shown.map(size), ...more.map((g) => g.files)], area);
    const tiles: MapTile[] = shown.map((f, j) => ({ key: f.folderId, rect: inset(rects[j]), folder: f }));
    more.forEach((g, j) => tiles.push({ key: `${share.shareId}:more:${j}`, rect: inset(rects[shown.length + j]), more: g }));
    return { share, rect: r, header, tiles };
  });
}

const inset = (r: Rect): Rect => ({ x: r.x + GAP / 2, y: r.y + GAP / 2, w: Math.max(0, r.w - GAP), h: Math.max(0, r.h - GAP) });
