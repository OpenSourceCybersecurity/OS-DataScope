import { describe, expect, it } from 'vitest';
import { ApiError, type ScopeImpact } from '../../api/client';
import { actionLabel, excludeParam, formProblem, impactText, impactToConfirm, sensitiveText, share, shareImpactText } from './scope';

const impact = (x: Partial<ScopeImpact> = {}): ScopeImpact => ({
  files: 0, bytes: 0, folders: 0, restricted: 0, confidential: 0, totalFiles: 0, large: false, warnings: [], notes: [], ...x,
});
const form = { kind: 'Folder' as const, shareId: 's1', path: 'Archive', extension: '', reason: 'Old installers' };

describe('scan scope words', () => {
  it('says what an exclusion covers, against the whole', () => {
    expect(impactText(impact(), 'Folder')).toBe('The last scans found no files here.');
    expect(impactText(impact(), 'FileType', 'bak')).toBe('The last scans found no .bak files.');
    expect(impactText(impact({ files: 1200, bytes: 2048, folders: 3, totalFiles: 4800 }), 'Folder')).toBe(
      'The last scans found 1,200 files (2 KB) in 3 folders, 25% of the share.',
    );
    expect(impactText(impact({ files: 1, bytes: 10, totalFiles: 1000, restricted: 1 }), 'FileType', 'bak')).toBe(
      'The last scans found 1 .bak file (10 B), <1% of all scanned files. Includes 1 Restricted file.',
    );
  });

  it('names sensitive files by level', () => {
    expect(sensitiveText(2, 3)).toBe('2 Restricted and 3 Confidential files');
    expect(sensitiveText(0, 1)).toBe('1 Confidential file');
  });

  it('gives a share of a whole only when there is one', () => {
    expect(share(5, 0)).toBe('');
    expect(share(0, 10)).toBe('0%');
    expect(share(1, 1000)).toBe('<1%');
    expect(share(10, 10)).toBe('100%');
  });

  it('describes a whole share, including one never scanned', () => {
    expect(shareImpactText(0, 0, 'NotScanned')).toMatch(/not been scanned/);
    expect(shareImpactText(0, 0, 'Scanned')).toBe('The last scan found no files in it.');
    expect(shareImpactText(2, 1024, 'Scanned')).toBe('The last scan found 2 files (1 KB) in it.');
  });

  it('labels every audited change and passes unknown ones through', () => {
    for (const a of ['share.exclude', 'share.include', 'scan-exclusion.add', 'scan-exclusion.enable', 'scan-exclusion.disable', 'scan-exclusion.change', 'scan-exclusion.remove'])
      expect(actionLabel(a)).not.toBe(a);
    expect(actionLabel('something.new')).toBe('something.new');
  });

  it('reads a link asking to leave a share out', () => {
    expect(excludeParam('exclude:abc')).toBe('abc');
    expect(excludeParam('abc')).toBeUndefined();
    expect(excludeParam(undefined)).toBeUndefined();
  });
});

describe('the add form', () => {
  it('is ready only with a target and a reason', () => {
    expect(formProblem(form)).toBeUndefined();
    expect(formProblem({ ...form, shareId: '' })).toBe('Choose the share.');
    expect(formProblem({ ...form, path: ' \\/ ' })).toBe('Enter the folder inside the share.');
    expect(formProblem({ ...form, kind: 'FileType', extension: '*.' })).toBe('Enter a file type.');
    expect(formProblem({ ...form, kind: 'FileType', shareId: '', extension: '.bak' })).toBeUndefined();
    expect(formProblem({ ...form, reason: ' ok ' })).toBe('Say why.');
  });

  it('needs confirming when it hides a lot, and not otherwise', () => {
    expect(formProblem(form, impact({ large: true }))).toBe('Confirm you mean to leave this out.');
    expect(formProblem(form, impact({ large: true }), true)).toBeUndefined();
    expect(formProblem(form, impact({ files: 5 }))).toBeUndefined();
  });

  it('recognises the server asking for confirmation, and nothing else', () => {
    const i = impact({ files: 20000, large: true, warnings: ['20,000 files'] });
    expect(impactToConfirm(new ApiError(409, 'x', {}, { needsConfirm: true, impact: i }))).toEqual(i);
    expect(impactToConfirm(new ApiError(409, 'This is already excluded.'))).toBeUndefined();
    expect(impactToConfirm(new ApiError(400, 'x', {}, { needsConfirm: true, impact: i }))).toBeUndefined();
    expect(impactToConfirm(new Error('x'))).toBeUndefined();
  });
});
