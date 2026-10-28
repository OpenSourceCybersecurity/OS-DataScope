// Custom detection rules (D133): pure helpers for the screen, kept apart so they can be unit tested.

import type { CustomRule, CustomRuleChange, CustomRuleDraft, CustomRuleEffect, CustomRuleLimits, CustomRuleTemplate } from '../../api/client';

export const EFFECT_LABEL: Record<CustomRuleEffect, string> = {
  Restricted: 'Label Restricted',
  Confidential: 'Label Confidential',
  ListOnly: 'List only',
};

export const EFFECT_HINT: Record<CustomRuleEffect, string> = {
  Restricted: 'A file with a match is labelled Restricted.',
  Confidential: 'A file with a match is labelled at least Confidential.',
  ListOnly: 'Matches are listed on the file, but its label stays as it is.',
};

export const ACTION_LABEL: Record<CustomRuleChange['action'], string> = {
  'custom-rule.add': 'Added',
  'custom-rule.change': 'Changed',
  'custom-rule.enable': 'Turned on',
  'custom-rule.disable': 'Turned off',
  'custom-rule.remove': 'Removed',
};

export const emptyDraft = (): CustomRuleDraft => ({
  name: '',
  description: '',
  kind: 'Keywords',
  pattern: '',
  keywords: [],
  caseSensitive: false,
  nearWords: [],
  minMatches: 1,
  effect: 'Confidential',
});

export const draftFromTemplate = (t: CustomRuleTemplate): CustomRuleDraft => ({
  ...emptyDraft(),
  name: t.name,
  description: t.description,
  kind: t.kind,
  pattern: t.pattern ?? '',
  keywords: [...t.keywords],
  nearWords: [...t.nearWords],
  minMatches: t.minMatches,
  effect: t.effect,
});

export const draftFromRule = (r: CustomRule): CustomRuleDraft => ({
  name: r.name,
  description: r.description ?? '',
  kind: r.kind,
  pattern: r.pattern ?? '',
  keywords: [...r.keywords],
  caseSensitive: r.caseSensitive,
  nearWords: [...r.nearWords],
  minMatches: r.minMatches,
  effect: r.effect,
});

/** Words and phrases typed one per line: trimmed, inner spacing collapsed, blanks and repeats (ignoring case) dropped. */
export function parseWords(text: string): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const line of text.split(/\r?\n/)) {
    const w = line.replace(/\s+/g, ' ').trim();
    if (w && !seen.has(w.toLowerCase())) {
      seen.add(w.toLowerCase());
      out.push(w);
    }
  }
  return out;
}

export const wordsText = (words: string[]) => words.join('\n');

/** What the server needs to try or save the rule: only the fields its kind uses. */
export function ruleBody(d: CustomRuleDraft): CustomRuleDraft {
  return {
    ...d,
    name: d.name.trim(),
    description: d.description.trim(),
    pattern: d.kind === 'Pattern' ? d.pattern.trim() : '',
    keywords: d.kind === 'Keywords' ? d.keywords : [],
  };
}

/** Why the rule can't be saved yet, by field, matching the server's rules. The server still checks the pattern itself. */
export function draftProblems(d: CustomRuleDraft, reason: string, limits?: Pick<CustomRuleLimits, 'maxNameChars' | 'maxPatternChars' | 'maxKeywords' | 'maxMinMatches'>): Record<string, string> {
  const p: Record<string, string> = {};
  const max = limits ?? { maxNameChars: 80, maxPatternChars: 500, maxKeywords: 100, maxMinMatches: 1000 };
  const name = d.name.trim();
  if (!name) p.name = 'Give the rule a name people will recognise in results.';
  else if (name.length > max.maxNameChars) p.name = `Use at most ${max.maxNameChars} characters.`;
  if (d.kind === 'Pattern') {
    if (!d.pattern.trim()) p.pattern = 'Enter a pattern, or start from one of the examples.';
    else if (d.pattern.trim().length > max.maxPatternChars) p.pattern = `Use at most ${max.maxPatternChars} characters.`;
  } else if (d.keywords.length === 0) p.keywords = 'Add at least one word or phrase, one per line.';
  else if (d.keywords.length > max.maxKeywords) p.keywords = `Use at most ${max.maxKeywords}; split the rest into another rule.`;
  if (!Number.isInteger(d.minMatches) || d.minMatches < 1 || d.minMatches > max.maxMinMatches) p.minMatches = `Use a number from 1 to ${max.maxMinMatches}.`;
  const r = reason.trim();
  if (r.length < 3) p.reason = 'Say why, so others know later.';
  else if (r.length > 500) p.reason = 'Use at most 500 characters.';
  return p;
}

/** The first message for each field the server refused. */
export const fieldErrors = (errors: Record<string, string[]>): Record<string, string> =>
  Object.fromEntries(Object.entries(errors).filter(([, v]) => v.length > 0).map(([k, v]) => [k, v[0]]));

/** "Found in 12 files", or that nothing was found yet (which is not the same as none existing). */
export const filesText = (r: Pick<CustomRule, 'files' | 'enabled'>) =>
  r.files > 0 ? `Found in ${r.files.toLocaleString('en-US')} ${r.files === 1 ? 'file' : 'files'}` : r.enabled ? 'Nothing found yet' : 'Turned off';
