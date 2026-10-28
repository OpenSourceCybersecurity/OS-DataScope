import { describe, expect, it } from 'vitest';
import { filtersFor, sharesFor, topConfidence, uncOf } from './classification';

describe('sensitive data screen helpers', () => {
  it('builds a UNC path', () => {
    expect(uncOf({ hostname: 'fs01', shareName: 'HR', path: 'Payroll\\staff.csv' })).toBe('\\\\fs01\\HR\\Payroll\\staff.csv');
  });

  it('picks the strongest confidence', () => {
    expect(topConfidence({ categories: [] })).toBeUndefined();
    expect(
      topConfidence({
        categories: [
          { category: 'Date of birth', confidence: 'medium', matches: 1 },
          { category: 'US SSN', confidence: 'high', matches: 2 },
          { category: 'Bank account', confidence: 'low', matches: 1 },
        ],
      }),
    ).toBe('high');
  });

  it('offers only disk shares in the share filter', () => {
    const shares = [
      { id: 'hr', kind: 'Disk' as const },
      { id: 'ipc', kind: 'Special' as const },
      { id: 'other', kind: 'Other' as const },
    ];
    expect(sharesFor(shares).map((s) => s.id)).toEqual(['hr']);
  });

  it('shows Confidential and above by default', () => {
    expect(filtersFor({ view: 'sensitive', shareId: '', label: '', minConfidence: '' })).toEqual({
      shareId: undefined,
      category: undefined,
      minConfidence: undefined,
      minSensitivity: 'Confidential',
    });
  });

  it('narrows to one label, category and confidence', () => {
    expect(filtersFor({ view: 'sensitive', shareId: 's1', label: 'Internal', category: 'US SSN', minConfidence: 'High' })).toEqual({
      shareId: 's1',
      category: 'US SSN',
      minConfidence: 'High',
      sensitivity: 'Internal',
    });
  });

  it('lists unread files without finding filters', () => {
    expect(filtersFor({ view: 'unread', shareId: 's1', label: 'Restricted', category: 'US SSN', minConfidence: 'High' })).toEqual({ notRead: true, shareId: 's1' });
  });
});
