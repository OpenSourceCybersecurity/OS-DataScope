import { describe, expect, it } from 'vitest';
import type { PersonSearchResult } from '../../api/client';
import { PERSON_SECTIONS, personCsv, personCsvName, personItemTarget, personItemWhere, splitAddresses, splitNames } from './personSearch';

// Every name and address here is made up.
const result: PersonSearchResult = {
  searchedAt: '2026-10-20T11:30:00Z',
  typed: { sid: 'S-1-5-21-1-2-3-1101', names: [], addresses: [] },
  searched: { sid: 'S-1-5-21-1-2-3-1101', names: ['Jo Bloggs', 'jbloggs'], addresses: ['jo@contoso.test'] },
  accounts: [
    { sid: 'S-1-5-21-1-2-3-1101', name: 'jbloggs', displayName: 'Jo Bloggs', userPrincipalName: 'jo@contoso.test', mail: 'jo@contoso.test', enabled: true, title: null, department: 'HR', lastLogon: null, groups: ['HR_Staff'] },
  ],
  directoryAt: '2026-10-20T01:00:00Z',
  sections: [
    { why: 'named', count: 1, more: true, timedOut: false },
    { why: 'evidence', count: 0, more: false, timedOut: true },
  ],
  items: [
    { why: 'named', kind: 'File', id: 'f1', shareId: 's1', source: 'files', place: '\\\\fs01\\HR', path: 'Reviews', name: '=Jo Bloggs review.docx', label: 'Confidential', detail: 'Name contains Jo Bloggs' },
  ],
  activity: { fileEvents: 3, fileFirst: null, fileLast: null, fileShares: [], timedOut: false },
  limit: 500,
};

describe('person search', () => {
  it('splits names on lines and semicolons only, so "Bloggs, Jo" stays one name', () => {
    expect(splitNames('Jo  Bloggs\nBloggs, Jo; Joanna Bloggs\n\n')).toEqual(['Jo Bloggs', 'Bloggs, Jo', 'Joanna Bloggs']);
  });

  it('splits addresses on anything an address cannot contain', () => {
    expect(splitAddresses(' jo@contoso.test, jo.b@home.test;\nx@y.test ')).toEqual(['jo@contoso.test', 'jo.b@home.test', 'x@y.test']);
  });

  it('opens files on Sensitive data and folders on Who has access', () => {
    expect(personItemTarget({ kind: 'File', id: 'f' })).toEqual({ route: 'discovery', param: 'file:f' });
    expect(personItemTarget({ kind: 'Folder', id: 'd' })).toEqual({ route: 'access', param: 'folder:d' });
  });

  it('shows where an item is as a UNC path', () => {
    expect(personItemWhere({ place: '\\\\fs01\\HR', path: 'Reviews\\2026' })).toBe('\\\\fs01\\HR\\Reviews\\2026');
    expect(personItemWhere({ place: '\\\\fs01\\HR', path: '' })).toBe('\\\\fs01\\HR');
  });

  it('downloads every item with what was searched, the reason and what was left out, without formula injection', () => {
    const csv = personCsv(result, 'SAR-2026-014', 'NW\\admin');
    expect(csv).toContain('# Reason: SAR-2026-014');
    expect(csv).toContain('# Searched for: Jo Bloggs (S-1-5-21-1-2-3-1101)');
    expect(csv).toContain('# Names and addresses searched: Jo Bloggs; jbloggs; jo@contoso.test');
    expect(csv).toContain('# Files and folders named after them: only the first 500 are listed');
    expect(csv).toContain('# Files where a scan saw their name: took too long and is left out');
    expect(csv).toContain("'=Jo Bloggs review.docx");
    expect(csv.split('\r\n').filter((l) => !l.startsWith('#'))).toHaveLength(2);
  });

  it('names the download without the person in it', () => {
    expect(personCsvName(result.searchedAt)).toBe('data-scope-person-search-2026-10-20.csv');
  });

  it('covers every reason the server gives', () => {
    expect(PERSON_SECTIONS.map((s) => s.why)).toEqual(['named', 'evidence', 'owner', 'dataOwner']);
  });
});
