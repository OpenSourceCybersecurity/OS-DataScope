import { describe, expect, it } from 'vitest';
import type { ExposureMapFolder, ExposureMapShare } from '../../api/client';
import { MAX_TILES_PER_SHARE, breadthOf, filesText, folderLabel, layoutMap, peopleText, squarify, uncertain } from './exposure';

const folder = (x: Partial<ExposureMapFolder> = {}): ExposureMapFolder => ({
  folderId: 'f1', shareId: 's1', serverId: 'v1', hostname: 'fs01', shareName: 'HR', uncPath: '\\\\fs01\\HR\\Payroll', restricted: 1, confidential: 0,
  status: 'Limited', anyDomainUser: { rights: 'No access', upTo: 'No access', certainty: 'Likely', notes: [] }, people: 3, peoplePossible: 0, grants: [], ...x,
});
const share = (x: Partial<ExposureMapShare> = {}): ExposureMapShare => ({
  shareId: 's1', serverId: 'v1', hostname: 'fs01', name: 'HR', uncPath: '\\\\fs01\\HR', included: true, state: 'Scanned', sharePermissions: 'Read',
  restricted: 1, confidential: 0, sensitiveFolders: 1, openFolders: 0, possiblyOpenFolders: 0, unknownFolders: 0, people: 3, peoplePossible: 0, filesNotRead: 0, ...x,
});

describe('exposure breadth', () => {
  it('ranks open, then by everyone who can or might read, and never hides uncertainty', () => {
    expect(breadthOf(folder({ status: 'Open', people: 2 }))).toBe('open');
    expect(breadthOf(folder({ status: 'PossiblyOpen', people: 0 }))).toBe('open');
    expect(breadthOf(folder({ status: 'Unknown', people: 0 }))).toBe('unknown');
    expect(breadthOf(folder({ people: 30 }))).toBe('many');
    expect(breadthOf(folder({ people: 6 }))).toBe('some');
    expect(breadthOf(folder({ people: 5 }))).toBe('few');
    // Two certain and 40 possible is drawn as many people, striped, not as a few.
    expect(breadthOf(folder({ people: 2, peoplePossible: 40 }))).toBe('many');
    expect(uncertain(folder({ people: 2, peoplePossible: 40 }))).toBe(true);
    expect(breadthOf(folder({ people: 0, peoplePossible: 0 }))).toBe('none');
    expect(uncertain(folder())).toBe(false);
    expect(uncertain(folder({ status: 'PossiblyOpen' }))).toBe(true);
    expect(uncertain(folder({ status: 'Unknown' }))).toBe(true);
  });

  it('says people and files plainly', () => {
    expect(peopleText(0, 0)).toBe('No one listed');
    expect(peopleText(1, 0)).toBe('1 person');
    expect(peopleText(0, 3)).toBe('Possibly 3 people');
    expect(peopleText(1200, 4)).toBe('1,200 people, 4 more possibly');
    expect(filesText(2, 0)).toBe('2 Restricted');
    expect(filesText(2, 5)).toBe('2 Restricted, 5 Confidential');
    expect(filesText(0, 0)).toBe('None');
  });

  it('labels a folder by its path inside the share', () => {
    expect(folderLabel(folder())).toBe('Payroll');
    expect(folderLabel(folder({ uncPath: '\\\\FS01\\hr' }))).toBe('HR');
  });
});

describe('treemap', () => {
  const area = (r: { w: number; h: number }) => r.w * r.h;

  it('gives each value an area in proportion, filling the box without overlap', () => {
    const values = [6, 6, 4, 3, 2, 2, 1];
    const rects = squarify(values, { x: 0, y: 0, w: 600, h: 400 });
    const total = values.reduce((a, b) => a + b, 0);
    rects.forEach((r, i) => expect(area(r)).toBeCloseTo((values[i] / total) * 600 * 400, 3));
    for (const r of rects) {
      expect(r.x).toBeGreaterThanOrEqual(-1e-9);
      expect(r.y).toBeGreaterThanOrEqual(-1e-9);
      expect(r.x + r.w).toBeLessThanOrEqual(600 + 1e-9);
      expect(r.y + r.h).toBeLessThanOrEqual(400 + 1e-9);
    }
    for (let i = 0; i < rects.length; i++)
      for (let j = i + 1; j < rects.length; j++) {
        const a = rects[i], b = rects[j];
        const overlap = Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x)) * Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
        expect(overlap).toBeLessThan(1e-6);
      }
  });

  it('keeps the order given and leaves zero values empty', () => {
    const rects = squarify([0, 5, 0], { x: 10, y: 10, w: 100, h: 50 });
    expect(area(rects[0])).toBe(0);
    expect(area(rects[1])).toBeCloseTo(5000);
    expect(squarify([], { x: 0, y: 0, w: 10, h: 10 })).toEqual([]);
    expect(area(squarify([3], { x: 0, y: 0, w: 0, h: 10 })[0])).toBe(0);
  });

  it('lays out shares with sensitive files, groups smaller folders by breadth and keeps unlisted ones apart', () => {
    const big = Array.from({ length: MAX_TILES_PER_SHARE }, (_, i) => folder({ folderId: `b${i}`, restricted: 3 }));
    const small = [
      folder({ folderId: 's1', restricted: 1, status: 'Open' }),
      folder({ folderId: 's2', restricted: 1, status: 'Open' }),
      folder({ folderId: 's3', restricted: 1, people: 2, peoplePossible: 1 }),
    ];
    const total = 3 * MAX_TILES_PER_SHARE + 3 + 4;
    const blocks = layoutMap(
      [share({ restricted: total, sensitiveFolders: MAX_TILES_PER_SHARE + 3 + 2 }), share({ shareId: 's2', restricted: 0, confidential: 0, sensitiveFolders: 0 })],
      [...small, ...big],
      800,
      400,
    );
    expect(blocks).toHaveLength(1);
    const tiles = blocks[0].tiles;
    expect(tiles.filter((t) => t.folder)).toHaveLength(MAX_TILES_PER_SHARE);
    expect(tiles.filter((t) => t.more).map((t) => t.more)).toEqual([
      { folders: 2, files: 2, breadth: 'open', uncertain: false },
      { folders: 1, files: 1, breadth: 'few', uncertain: true },
      { folders: 2, files: 4 },
    ]);
    expect(blocks[0].header).toBe(true);
  });
});
