import { describe, expect, it } from 'vitest';
import { classify, isValidAba, isValidSsn, luhn, scanText } from './detectors';

describe('SSN detector', () => {
  it('finds a formatted SSN next to a keyword with high confidence and masks it', () => {
    const { findings } = scanText('Employee SSN: 512-48-3307 on file');
    expect(findings).toHaveLength(1);
    expect(findings[0]).toMatchObject({ ruleId: 'DS-SSN-01', confidence: 'high' });
    expect(findings[0].evidence[0]).toContain('***-**-3307');
    expect(findings[0].evidence.join()).not.toContain('512-48');
  });
  it('rejects invalid SSA ranges', () => {
    expect(isValidSsn('000', '12', '3456')).toBe(false);
    expect(isValidSsn('666', '12', '3456')).toBe(false);
    expect(isValidSsn('912', '12', '3456')).toBe(false);
    expect(isValidSsn('512', '00', '3456')).toBe(false);
    expect(isValidSsn('512', '12', '0000')).toBe(false);
    expect(isValidSsn('078', '05', '1120')).toBe(false);
  });
  it('ignores bare nine-digit numbers without SSN context', () => {
    expect(scanText('Invoice 482913374 paid. Order 7781203345 shipped.').findings).toHaveLength(0);
  });
  it('reports formatted lookalikes without context at medium confidence (a known false positive)', () => {
    const { findings } = scanText('Part# 411-22-3333 conveyor bearing');
    expect(findings[0]?.confidence).toBe('medium');
  });
});

describe('payment card detector', () => {
  it('validates Luhn', () => {
    expect(luhn('4111111111111111')).toBe(true);
    expect(luhn('4111111111111112')).toBe(false);
  });
  it('finds test card numbers and masks all but last four', () => {
    const { findings } = scanText('Visa 4111 1111 1111 1111 exp 04/28; Amex 3782 822463 10005');
    expect(findings[0]).toMatchObject({ category: 'Payment card', count: 2, confidence: 'high' });
    expect(findings[0].evidence.join()).not.toMatch(/4111 1111 1111 1111/);
  });
  it('ignores digit runs with an unknown issuer prefix', () => {
    expect(scanText('Tracking 9999 1111 1111 1111').findings).toHaveLength(0);
  });
});

describe('bank, credentials, health', () => {
  it('requires a checksum-valid routing number', () => {
    expect(isValidAba('011000015')).toBe(true);
    expect(isValidAba('011000016')).toBe(false);
    const f = scanText('Routing number: 011000015\nAccount number: 44001239').findings[0];
    expect(f).toMatchObject({ category: 'Bank account', confidence: 'high' });
  });
  it('finds private keys and passwords, skipping placeholders', () => {
    const f = scanText('-----BEGIN RSA PRIVATE KEY-----\nabc\npassword = changeme\npwd: Winter2026!').findings;
    expect(f.find((x) => x.ruleId === 'DS-CRED-01')?.confidence).toBe('high');
    expect(f.find((x) => x.ruleId === 'DS-CRED-02')?.count).toBe(1);
  });
  it('needs at least two health signals', () => {
    expect(scanText('The patient portal is down').findings).toHaveLength(0);
    const f = scanText('Employee SSN 512-48-3307. Physician statement: diagnosis M54.5, treatment plan attached.').findings;
    expect(f.find((x) => x.category === 'Health information')?.confidence).toBe('high');
  });
  it('caps input size and reports truncation', () => {
    expect(scanText('a'.repeat(1_000_001)).truncated).toBe(true);
  });
});

describe('classification policy', () => {
  it('maps findings to labels', () => {
    expect(classify([])).toBe('Internal');
    expect(classify([], true)).toBe('Public');
    expect(classify(scanText('SSN 512-48-3307').findings)).toBe('Restricted');
    expect(classify(scanText('Part# 411-22-3333').findings)).toBe('Confidential');
  });
});
