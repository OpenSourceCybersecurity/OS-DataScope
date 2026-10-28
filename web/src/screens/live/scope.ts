import { ApiError, type ExclusionEffect, type ExclusionKind, type ScopeImpact } from '../../api/client';
import { fmtBytes, fmtNum } from '../../ui/kit';

/** Scan scope and exclusions (D124): words and small rules the screen shares with its tests. */

export const EFFECT_LABEL: Record<ExclusionEffect, { title: string; about: string }> = {
  Skip: { title: 'Not scanned', about: 'Not listed or read: files, permissions and sensitive data there are not seen.' },
  ListOnly: { title: 'Listed, contents not read', about: 'Files and permissions are listed; contents are never opened, so sensitive data there is not found.' },
};

const ACTIONS: Record<string, string> = {
  'share.exclude': 'Left a share out',
  'share.include': 'Scanned a share again',
  'scan-exclusion.add': 'Added an exclusion',
  'scan-exclusion.enable': 'Turned an exclusion on',
  'scan-exclusion.disable': 'Turned an exclusion off',
  'scan-exclusion.change': 'Changed why',
  'scan-exclusion.remove': 'Removed an exclusion',
};

export const actionLabel = (action: string) => ACTIONS[action] ?? action;

/** "12%", "<1%", or "" when there is nothing to compare with. */
export function share(part: number, whole: number): string {
  if (whole <= 0) return '';
  const p = (100 * part) / whole;
  return p > 0 && p < 1 ? '<1%' : `${Math.round(p)}%`;
}

/** What the last scans found in what an exclusion covers, in one sentence. */
export function impactText(i: ScopeImpact, kind: ExclusionKind, extension?: string | null): string {
  if (i.files === 0) return kind === 'Folder' ? 'The last scans found no files here.' : `The last scans found no .${extension ?? ''} files.`;
  const what = kind === 'FileType' && extension ? `.${extension} file` : 'file';
  const files = `${fmtNum(i.files)} ${what}${i.files === 1 ? '' : 's'}`;
  const folders = kind === 'Folder' && i.folders > 1 ? ` in ${fmtNum(i.folders)} folders` : '';
  const pct = share(i.files, i.totalFiles);
  const of = pct ? `, ${pct} of ${kind === 'Folder' ? 'the share' : 'all scanned files'}` : '';
  const sensitive = i.restricted + i.confidential > 0 ? ` Includes ${sensitiveText(i.restricted, i.confidential)}.` : '';
  return `The last scans found ${files} (${fmtBytes(i.bytes)})${folders}${of}.${sensitive}`;
}

export function sensitiveText(restricted: number, confidential: number): string {
  const parts = [restricted ? `${fmtNum(restricted)} Restricted` : '', confidential ? `${fmtNum(confidential)} Confidential` : ''].filter(Boolean);
  return parts.join(' and ') + (restricted + confidential === 1 ? ' file' : ' files');
}

/** A share link from another screen ("exclude:<id>") asking to leave that share out. */
export function excludeParam(param?: string): string | undefined {
  return param?.startsWith('exclude:') ? param.slice('exclude:'.length) : undefined;
}

/** Why the add form can't be sent yet, or undefined. The server checks again. */
export function formProblem(f: { kind: ExclusionKind; shareId: string; path: string; extension: string; reason: string }, impact?: ScopeImpact, confirmed?: boolean): string | undefined {
  if (f.kind === 'Folder' && !f.shareId) return 'Choose the share.';
  if (f.kind === 'Folder' && f.path.trim().replace(/[\\/]/g, '') === '') return 'Enter the folder inside the share.';
  if (f.kind === 'FileType' && f.extension.trim().replace(/^\*?\./, '') === '') return 'Enter a file type.';
  if (f.reason.trim().length < 3) return 'Say why.';
  if (impact?.large && !confirmed) return 'Confirm you mean to leave this out.';
  return undefined;
}

/** What the server says a change would hide when it wants that confirmed (409 with needsConfirm), or undefined. */
export function impactToConfirm(e: unknown): ScopeImpact | undefined {
  if (!(e instanceof ApiError) || e.status !== 409 || e.details.needsConfirm !== true) return undefined;
  const impact = e.details.impact;
  return impact && typeof impact === 'object' ? (impact as ScopeImpact) : undefined;
}

/** Leaving a whole share out: what it held at the last scan, in one sentence. */
export function shareImpactText(fileCount: number, totalBytes: number, state: string): string {
  if (state === 'NotScanned') return 'It has not been scanned yet, so nothing is known about what it holds.';
  if (fileCount === 0) return 'The last scan found no files in it.';
  return `The last scan found ${fmtNum(fileCount)} ${fileCount === 1 ? 'file' : 'files'} (${fmtBytes(totalBytes)}) in it.`;
}
