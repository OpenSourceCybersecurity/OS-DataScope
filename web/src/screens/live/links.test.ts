import { describe, expect, it } from 'vitest';
import { discoveryParam, risksParam, shareOf } from './links';

describe('deep links', () => {
  it('opens Sensitive data on one share or on files not read', () => {
    expect(discoveryParam(undefined)).toEqual({ view: 'sensitive', shareId: '', standard: '', rule: '' });
    expect(discoveryParam('share:abc')).toEqual({ view: 'sensitive', shareId: 'abc', standard: '', rule: '' });
    expect(discoveryParam('unread')).toEqual({ view: 'unread', shareId: '', standard: '', rule: '' });
    expect(discoveryParam('unread:abc')).toEqual({ view: 'unread', shareId: 'abc', standard: '', rule: '' });
    expect(discoveryParam('standard:pci-dss')).toEqual({ view: 'sensitive', shareId: '', standard: 'pci-dss', rule: '' });
    expect(discoveryParam('something')).toEqual({ view: 'sensitive', shareId: '', standard: '', rule: '' });
    expect(discoveryParam('rule:DS-CUSTOM-3')).toEqual({ view: 'sensitive', shareId: '', standard: '', rule: 'DS-CUSTOM-3' });
    expect(discoveryParam('file:f1')).toEqual({ view: 'sensitive', shareId: '', standard: '', rule: '', fileId: 'f1' });
    // Old links with a source filter land on the whole list.
    expect(discoveryParam('source:mail')).toEqual({ view: 'sensitive', shareId: '', standard: '', rule: '' });
  });

  it('opens Risks on one share', () => {
    expect(risksParam('share:abc')).toEqual({ shareId: 'abc' });
    expect(risksParam('standard:pci-dss')).toEqual({ standard: 'pci-dss' });
    expect(risksParam('abc')).toEqual({});
    expect(shareOf('\\\\fs01\\HR\\Payroll')).toBe('\\\\fs01\\HR');
    expect(shareOf('somewhere')).toBe('somewhere');
  });
});
