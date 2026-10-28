import { describe, expect, it } from 'vitest';
import type { CustomRuleTemplate } from '../../api/client';
import { draftFromTemplate, draftProblems, emptyDraft, fieldErrors, filesText, parseWords, ruleBody } from './customRules';
import { discoveryParam } from './links';
import { filtersFor } from './classification';

const ip: CustomRuleTemplate = {
  id: 'ipv4',
  name: 'IP addresses',
  description: 'Four numbers…',
  kind: 'Pattern',
  pattern: '\\b(?:[0-9]{1,3}\\.){3}[0-9]{1,3}\\b',
  keywords: [],
  nearWords: [],
  minMatches: 1,
  effect: 'Confidential',
  example: 'Allow 192.0.2.10',
};

describe('custom detection helpers', () => {
  it('reads one word or phrase per line, without blanks or repeats', () => {
    expect(parseWords('  Project   Falcon \n\nosprey\r\nOSPREY\n ')).toEqual(['Project Falcon', 'osprey']);
  });

  it('starts from a template', () => {
    const d = draftFromTemplate(ip);
    expect(d.kind).toBe('Pattern');
    expect(d.pattern).toBe(ip.pattern);
    expect(d.effect).toBe('Confidential');
  });

  it('sends only the fields a rule of its kind uses', () => {
    const d = { ...draftFromTemplate(ip), keywords: ['left over'] };
    expect(ruleBody(d).keywords).toEqual([]);
    const w = { ...emptyDraft(), name: ' Falcon ', keywords: ['falcon'], pattern: 'left over' };
    expect(ruleBody(w)).toMatchObject({ name: 'Falcon', pattern: '', keywords: ['falcon'] });
  });

  it('says what is missing before saving', () => {
    expect(draftProblems(emptyDraft(), '')).toEqual({
      name: expect.any(String),
      keywords: expect.any(String),
      reason: expect.any(String),
    });
    expect(draftProblems({ ...emptyDraft(), kind: 'Pattern', name: 'x', minMatches: 0 }, 'ok then')).toEqual({
      pattern: expect.any(String),
      minMatches: expect.any(String),
    });
    expect(draftProblems({ ...draftFromTemplate(ip) }, 'Server addresses are confidential.')).toEqual({});
  });

  it('keeps the first server message per field', () => {
    expect(fieldErrors({ pattern: ['bad', 'worse'], name: [] })).toEqual({ pattern: 'bad' });
  });

  it('says nothing found is not the same as off', () => {
    expect(filesText({ files: 0, enabled: true })).toBe('Nothing found yet');
    expect(filesText({ files: 0, enabled: false })).toBe('Turned off');
    expect(filesText({ files: 1200, enabled: true })).toBe('Found in 1,200 files');
  });

  it('links to one rule\'s files, whatever their label', () => {
    expect(discoveryParam('rule:DS-CUSTOM-3').rule).toBe('DS-CUSTOM-3');
    expect(filtersFor({ view: 'sensitive', shareId: '', label: '', minConfidence: '', rule: 'DS-CUSTOM-3' })).toEqual({
      shareId: undefined,
      category: undefined,
      minConfidence: undefined,
      standard: undefined,
      rule: 'DS-CUSTOM-3',
    });
  });
});
