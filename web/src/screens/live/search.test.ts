import { describe, expect, it } from 'vitest';
import type { SearchHit, SearchResult } from '../../api/client';
import { looksLikePath, searchFootnote, searchRows, searchTarget, shortPath } from './search';
import { discoveryParam } from './links';
import { parseAccessParam } from './access';

const hit = (id: string, over: Partial<SearchHit> = {}): SearchHit => ({ id, title: id, detail: null, label: null, exact: false, disabled: false, ...over });
const result = (over: Partial<SearchResult> = {}): SearchResult => ({ query: 'payroll', groups: [], filesSearched: true, directoryAt: '2026-10-11T10:00:00Z', ...over });

describe('search box', () => {
  it('opens each kind of result where it is explained', () => {
    expect(searchTarget('User', hit('S-1-5-21-1'))).toEqual({ route: 'access', param: 'user:S-1-5-21-1' });
    expect(searchTarget('Group', hit('S-1-5-21-2'))).toEqual({ route: 'access', param: 'group:S-1-5-21-2' });
    expect(searchTarget('Share', hit('sh1'))).toEqual({ route: 'shares', param: 'sh1' });
    expect(searchTarget('Folder', hit('fo1'))).toEqual({ route: 'access', param: 'folder:fo1' });
    expect(searchTarget('File', hit('fi1'))).toEqual({ route: 'discovery', param: 'file:fi1' });
    expect(searchTarget('Server', hit('sv1'))).toEqual({ route: 'setup' });
  });

  it('links that the screens understand', () => {
    expect(parseAccessParam(searchTarget('Group', hit('S-1-5-21-2')).param)).toEqual({ sid: 'S-1-5-21-2', kind: 'Group' });
    expect(parseAccessParam(searchTarget('Folder', hit('fo1')).param)).toEqual({ folderId: 'fo1' });
    expect(discoveryParam(searchTarget('File', hit('fi1')).param).fileId).toBe('fi1');
  });

  it('lists each kind together, with the kind holding an exact path match first', () => {
    const rows = searchRows(
      result({
        groups: [
          { kind: 'Share', items: [hit('sh1')], more: false, timedOut: false },
          { kind: 'Folder', items: [hit('fo1'), hit('fo2', { exact: true })], more: false, timedOut: false },
        ],
      }),
    );
    expect(rows.map((r) => r.key)).toEqual(['Folder:fo2', 'Folder:fo1', 'Share:sh1']);
    expect(searchRows(undefined)).toEqual([]);
  });

  it('says what was not searched', () => {
    expect(searchFootnote(result({ filesSearched: false }))).toBe('Type 3 or more characters to search folders and files too.');
    expect(searchFootnote(result({ groups: [{ kind: 'File', items: [], more: false, timedOut: true }] }))).toBe('Searching files took too long. A longer search narrows it.');
    expect(searchFootnote(result({ groups: [{ kind: 'File', items: [hit('a')], more: true, timedOut: false }] }))).toBe('Showing the first few of each. A longer search narrows them.');
    expect(searchFootnote(result({ directoryAt: null }))).toBe('People and groups appear after the first Active Directory sync.');
    expect(searchFootnote(result({ query: '\\\\fs01\\HR', directoryAt: null }))).toBeUndefined();
    expect(searchFootnote(result())).toBeUndefined();
  });

  it('recognises a pasted path', () => {
    expect(looksLikePath('\\\\fs01\\HR')).toBe(true);
    expect(looksLikePath('  //fs01/HR')).toBe(true);
    expect(looksLikePath('HR\\Payroll')).toBe(false);
  });

  it('shortens long paths in the middle, keeping the server, the share and the nearest folders', () => {
    expect(shortPath('\\\\fs01\\HR\\Payroll')).toBe('\\\\fs01\\HR\\Payroll');
    expect(shortPath('\\\\nw-fs01\\HR\\Payroll\\2023\\Quarter one\\01-jan', 40)).toBe('\\\\nw-fs01\\HR\\…\\2023\\Quarter one\\01-jan');
    expect(shortPath('\\\\nw-fs01\\HR\\Payroll\\2023\\Quarter one\\01-jan', 30)).toBe('\\\\nw-fs01\\HR\\…\\01-jan');
    // The nearest folder is always kept, however long.
    expect(shortPath('\\\\fs01\\HR\\a\\' + 'x'.repeat(60), 30)).toBe('\\\\fs01\\HR\\…\\' + 'x'.repeat(60));
    expect(shortPath('C:\\' + 'x'.repeat(60), 30)).toBe('C:\\' + 'x'.repeat(60));
  });
});
