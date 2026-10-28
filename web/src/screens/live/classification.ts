// Pure helpers for the live Sensitive data screen, kept apart from the components so they can be unit tested.

import type { ClassifiedFile, FileFilters, Sensitivity, Share } from '../../api/client';
import type { Confidence, FindingCategory } from '../../domain/types';

export const uncOf = (f: Pick<ClassifiedFile, 'hostname' | 'shareName' | 'path'>) => `\\\\${f.hostname}\\${f.shareName}\\${f.path}`;

/** The shares the share filter offers: disk shares only. */
export function sharesFor<T extends Pick<Share, 'kind'>>(shares: T[]): T[] {
  return shares.filter((s) => s.kind === 'Disk');
}

const RANK: Record<Confidence, number> = { high: 3, medium: 2, low: 1 };

/** The strongest finding on a file, for the table's confidence column. */
export function topConfidence(f: Pick<ClassifiedFile, 'categories'>): Confidence | undefined {
  return f.categories.reduce<Confidence | undefined>((best, c) => (!best || RANK[c.confidence] > RANK[best] ? c.confidence : best), undefined);
}

export interface DiscoveryView {
  view: 'sensitive' | 'unread';
  shareId: string;
  category?: FindingCategory;
  label: Sensitivity | '';
  minConfidence: '' | 'Medium' | 'High';
  /** A turned-on compliance standard's id, or '' for any (D126). */
  standard?: string;
  /** One rule's files, such as a custom rule's DS-CUSTOM-3 (D133), or '' for any. */
  rule?: string;
}

/**
 * The API filters for what the screen shows. "Sensitive files" means Confidential or above unless one label or one
 * rule is picked; "Not read" ignores the finding filters, since unread files have no findings.
 */
export function filtersFor(v: DiscoveryView): FileFilters {
  const shareId = v.shareId || undefined;
  if (v.view === 'unread') return { notRead: true, shareId };
  return {
    shareId,
    category: v.category,
    minConfidence: v.minConfidence || undefined,
    standard: v.standard || undefined,
    rule: v.rule || undefined,
    // One rule's files are shown whatever their label: a custom rule may list matches without changing it.
    ...(v.label ? { sensitivity: v.label } : v.rule ? {} : { minSensitivity: 'Confidential' as const }),
  };
}
