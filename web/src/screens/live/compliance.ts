// Compliance standards (D126): pure helpers for the screens, kept apart so they can be unit tested.

import type { ComplianceChange, ComplianceStandard } from '../../api/client';
import type { FindingCategory } from '../../domain/types';
import { fmtNum } from '../../ui/kit';

/** Other screens link with "standard:<id>" to open already narrowed to a standard. */
export const standardParam = (param?: string): string => (param?.startsWith('standard:') ? param.slice('standard:'.length) : '');

/** Short names of the turned-on standards a file falls under, in the order given. Unknown ids are shown as they are. */
export function standardNames(ids: string[], standards: Pick<ComplianceStandard, 'id' | 'name'>[] | undefined): string[] {
  return ids.map((id) => standards?.find((s) => s.id === id)?.name ?? id);
}

/** Why a change can't be saved yet, or undefined when it can. Matches the server's rules. */
export function changeProblem(change: { categories: FindingCategory[]; reason: string }): string | undefined {
  if (change.categories.length === 0) return 'Choose at least one kind of data, or turn the standard off.';
  const reason = change.reason.trim();
  if (reason.length < 3) return 'Say why, so others know later.';
  if (reason.length > 500) return 'Use at most 500 characters.';
  return undefined;
}

/** Whether what counts differs from the standard's defaults, so the screen can say so. */
export function customised(s: Pick<ComplianceStandard, 'categories' | 'defaultCategories' | 'minConfidence'>): boolean {
  const a = [...s.categories].sort();
  const b = [...s.defaultCategories].sort();
  return s.minConfidence !== 'Medium' || a.length !== b.length || a.some((c, i) => c !== b[i]);
}

/** "12 files in 3 folders on 2 shares", or that nothing was found (which is not the same as none existing). */
export function countsText(c: ComplianceStandard['counts']): string {
  if (!c) return 'Turned off';
  if (c.files === 0) return 'No files found yet';
  const n = (x: number, one: string, many: string) => `${fmtNum(x)} ${x === 1 ? one : many}`;
  return `${n(c.files, 'file', 'files')} in ${n(c.folders, 'folder', 'folders')} on ${n(c.shares, 'share', 'shares')}`;
}

export const ACTION_LABEL: Record<ComplianceChange['action'], string> = {
  'compliance.enable': 'Turned on',
  'compliance.disable': 'Turned off',
  'compliance.change': 'Changed',
};
