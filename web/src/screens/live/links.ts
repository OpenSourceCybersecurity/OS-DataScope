// Deep links other screens accept, so a drill-down lands on the right slice instead of the whole list.

/**
 * Sensitive data: "share:<id>" for one share's sensitive files, "unread" or "unread:<shareId>" for files not read,
 * "standard:<id>" for the files under one compliance standard, "rule:<id>" for one rule's files (such as a custom
 * rule's), and "file:<id>" to open one file.
 */
export function discoveryParam(param?: string): {
  view: 'sensitive' | 'unread';
  shareId: string;
  standard: string;
  rule: string;
  fileId?: string;
} {
  const none = { view: 'sensitive' as const, shareId: '', standard: '', rule: '' };
  if (!param) return none;
  if (param.startsWith('file:')) return { ...none, fileId: param.slice('file:'.length) };
  if (param === 'unread') return { ...none, view: 'unread' };
  if (param.startsWith('unread:')) return { ...none, view: 'unread', shareId: param.slice('unread:'.length) };
  if (param.startsWith('share:')) return { ...none, shareId: param.slice('share:'.length) };
  if (param.startsWith('standard:')) return { ...none, standard: param.slice('standard:'.length) };
  if (param.startsWith('rule:')) return { ...none, rule: param.slice('rule:'.length) };
  return none;
}

/** Risks: "share:<id>" for one share's risks. */
export function risksParam(param?: string): { shareId?: string; standard?: string } {
  if (param?.startsWith('share:')) return { shareId: param.slice('share:'.length) };
  if (param?.startsWith('standard:')) return { standard: param.slice('standard:'.length) };
  return {};
}

/** The \\server\share part of a risk's location. */
export function shareOf(where: string): string {
  const parts = where.split('\\');
  return parts.length >= 4 ? `\\\\${parts[2]}\\${parts[3]}` : where;
}
