import { describe, expect, it } from 'vitest';
import { changeProblem, countsText, customised, standardNames, standardParam } from './compliance';

describe('compliance helpers', () => {
  it('reads a standard from a deep link', () => {
    expect(standardParam('standard:pci-dss')).toBe('pci-dss');
    expect(standardParam('share:abc')).toBe('');
    expect(standardParam(undefined)).toBe('');
  });

  it('names standards, keeping ids it does not know', () => {
    const list = [{ id: 'pci-dss', name: 'PCI DSS' }, { id: 'glba', name: 'GLBA' }];
    expect(standardNames(['glba', 'pci-dss', 'sox'], list)).toEqual(['GLBA', 'PCI DSS', 'sox']);
    expect(standardNames(['glba'], undefined)).toEqual(['glba']);
  });

  it('needs a kind of data and a reason before saving', () => {
    expect(changeProblem({ categories: [], reason: 'We take cards.' })).toMatch(/at least one/);
    expect(changeProblem({ categories: ['Payment card'], reason: '  ' })).toMatch(/Say why/);
    expect(changeProblem({ categories: ['Payment card'], reason: 'x'.repeat(501) })).toMatch(/500/);
    expect(changeProblem({ categories: ['Payment card'], reason: 'We take cards by phone.' })).toBeUndefined();
  });

  it('notices when what counts was changed from the defaults', () => {
    const base = { categories: ['US SSN', 'Bank account'], defaultCategories: ['Bank account', 'US SSN'], minConfidence: 'Medium' } as const;
    expect(customised({ ...base, categories: [...base.categories], defaultCategories: [...base.defaultCategories] })).toBe(false);
    expect(customised({ ...base, categories: ['US SSN'], defaultCategories: [...base.defaultCategories] })).toBe(true);
    expect(customised({ ...base, categories: [...base.categories], defaultCategories: [...base.defaultCategories], minConfidence: 'High' })).toBe(true);
  });

  it('says how much falls under a standard, and never that nothing exists', () => {
    expect(countsText(null)).toBe('Turned off');
    expect(countsText({ files: 0, restricted: 0, confidential: 0, folders: 0, shares: 0 })).toBe('No files found yet');
    expect(countsText({ files: 1, restricted: 1, confidential: 0, folders: 1, shares: 1 })).toBe('1 file in 1 folder on 1 share');
    expect(countsText({ files: 1200, restricted: 1, confidential: 0, folders: 3, shares: 2 })).toBe('1,200 files in 3 folders on 2 shares');
  });
});
