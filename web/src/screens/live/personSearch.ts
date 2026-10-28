import type { PersonItem, PersonReason, PersonSearchResult } from '../../api/client';
import type { Route } from '../../App';
import { toCsv } from '../../domain/csv';

/** The parts of a person search (D160), in the order they're shown, with what each one does and doesn't cover. */
export const PERSON_SECTIONS: { why: PersonReason; title: string; about: string }[] = [
  { why: 'named', title: 'Files and folders named after them', about: 'Their name or email address is in the file or folder name.' },
  {
    why: 'evidence',
    title: 'Files where a scan saw their name',
    about: 'Their name or address is in the text the scan kept around something it flagged. Only text near a possible match is kept, so this finds some files that mention them, never all.',
  },
  { why: 'owner', title: 'Folders they own in Windows', about: "Windows lists them as the folder's owner, usually because they created it. Folders below one they own are not listed again." },
  { why: 'dataOwner', title: 'Folders they are the data owner of', about: 'Set on Owners. They decide who may use these folders.' },
];

export const SOURCE_LABEL: Record<PersonItem['source'], string> = { files: 'File share' };

/** Names typed one per line or separated by semicolons; a comma may be inside a name ("Bloggs, Jo"). */
export const splitNames = (text: string) =>
  text
    .split(/[\n;]+/)
    .map((s) => s.trim().replace(/\s+/g, ' '))
    .filter(Boolean);

/** Addresses separated by anything an address can't contain: spaces, commas, semicolons or new lines. */
export const splitAddresses = (text: string) => text.split(/[\s,;]+/).filter(Boolean);

/** Where an item opens in Data Scope. */
export function personItemTarget(item: Pick<PersonItem, 'kind' | 'id'>): { route: Route; param?: string } {
  switch (item.kind) {
    case 'File':
      return { route: 'discovery', param: `file:${item.id}` };
    case 'Folder':
      return { route: 'access', param: `folder:${item.id}` };
  }
}

/** Where an item is, in one line: the share, then the folder inside it. */
export function personItemWhere(item: Pick<PersonItem, 'place' | 'path'>): string {
  return item.path ? `${item.place}\\${item.path}` : item.place;
}

/** Everything found, as a spreadsheet, with what was searched and what the search can't tell. */
export function personCsv(r: PersonSearchResult, reason: string, by: string): string {
  const titles = new Map(PERSON_SECTIONS.map((s) => [s.why, s.title]));
  const who = [...r.accounts.map((a) => `${a.displayName ?? a.name} (${a.sid})`), ...r.typed.names, ...r.typed.addresses];
  const preamble = [
    'Data Scope: everything about a person',
    `Searched ${r.searchedAt} by ${by}`,
    `Searched for: ${[...new Set(who)].join('; ')}`,
    `Names and addresses searched: ${[...r.searched.names, ...r.searched.addresses].join('; ')}`,
    `Reason: ${reason}`,
    `Activity: ${r.activity.fileEvents} file share events`,
    'Limits: built from file and folder names, ownership, the directory, activity and the text scans kept around flagged data. File contents were not searched.',
    ...r.sections.filter((s) => s.more || s.timedOut).map((s) => `${titles.get(s.why)}: ${s.timedOut ? 'took too long and is left out' : `only the first ${r.limit} are listed`}`),
  ];
  const rows = PERSON_SECTIONS.flatMap((s) =>
    r.items.filter((i) => i.why === s.why).map((i) => [s.title, i.kind, SOURCE_LABEL[i.source], i.place, i.path, i.name, i.label ?? '', i.detail ?? '']),
  );
  return toCsv(['Why', 'Kind', 'Source', 'Where', 'Folder', 'Name', 'Label', 'Detail'], rows, preamble);
}

/** A file name for the download that doesn't carry the person's name: it may end up in a shared Downloads folder. */
export const personCsvName = (searchedAt: string) => `data-scope-person-search-${searchedAt.slice(0, 10)}.csv`;
