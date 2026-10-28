// Sensitive data detectors. Every hit is a *possible* match with a confidence
// and redacted evidence. Nothing here stores or returns an unmasked value.

import type { Classification, Confidence, Finding, FindingCategory } from './types';

export const POLICY_VERSION = '2026.10-r3';
const MAX_SCAN_CHARS = 1_000_000; // bounded input; larger files are truncated and flagged by the caller
const MAX_EVIDENCE = 3;
const CONTEXT_WINDOW = 60;

export interface DetectorRule {
  id: string;
  name: string;
  category: FindingCategory;
  description: string;
}

export const RULES: DetectorRule[] = [
  { id: 'DS-SSN-01', name: 'US Social Security number', category: 'US SSN', description: 'NNN-NN-NNNN with SSA range checks; unformatted 9-digit numbers only count next to SSN keywords.' },
  { id: 'DS-PCI-01', name: 'Payment card number', category: 'Payment card', description: '13-19 digits with a known issuer prefix that pass the Luhn checksum.' },
  { id: 'DS-BANK-01', name: 'Bank routing + account', category: 'Bank account', description: 'ABA routing number (checksum-valid) near an account number.' },
  { id: 'DS-CRED-01', name: 'Private key material', category: 'Credentials', description: 'PEM private key blocks.' },
  { id: 'DS-CRED-02', name: 'Password or secret in text', category: 'Credentials', description: 'password=, pwd:, connection-string passwords, AWS access key IDs.' },
  { id: 'DS-PHI-01', name: 'Health information', category: 'Health information', description: 'Two or more clinical terms or ICD-10 codes, scored higher next to a personal identifier.' },
  { id: 'DS-DOB-01', name: 'Date of birth', category: 'Date of birth', description: 'A date next to "DOB" or "date of birth".' },
];

const ruleCategory = (id: string) => RULES.find((r) => r.id === id)!.category;

function snippet(text: string, index: number, length: number, masked: string): string {
  const start = Math.max(0, index - CONTEXT_WINDOW / 2);
  const end = Math.min(text.length, index + length + CONTEXT_WINDOW / 2);
  const before = text.slice(start, index).replace(/\s+/g, ' ');
  const after = text.slice(index + length, end).replace(/\s+/g, ' ');
  return `${start > 0 ? '…' : ''}${before}${masked}${after}${end < text.length ? '…' : ''}`;
}

function hasContext(text: string, index: number, words: RegExp): boolean {
  const start = Math.max(0, index - 80);
  return words.test(text.slice(start, index + 80));
}

// ---------- SSN ----------
export function isValidSsn(area: string, group: string, serial: string): boolean {
  const a = Number(area);
  if (a === 0 || a === 666 || a >= 900) return false;
  if (group === '00' || serial === '0000') return false;
  // Well-known advertising/test SSNs are never real people.
  if (`${area}${group}${serial}` === '078051120' || `${area}${group}${serial}` === '219099999') return false;
  return true;
}

const SSN_CONTEXT = /\b(ssn|social security|soc\.? ?sec|ss#|taxpayer id|tin)\b/i;

function detectSsn(text: string, out: Hits) {
  const formatted = /\b(\d{3})-(\d{2})-(\d{4})\b/g;
  for (const m of text.matchAll(formatted)) {
    if (!isValidSsn(m[1], m[2], m[3])) continue;
    const ctx = hasContext(text, m.index!, SSN_CONTEXT);
    out.add('DS-SSN-01', ctx ? 'high' : 'medium', snippet(text, m.index!, m[0].length, `***-**-${m[3]}`));
  }
  const bare = /\b(\d{3})(\d{2})(\d{4})\b/g;
  for (const m of text.matchAll(bare)) {
    if (!isValidSsn(m[1], m[2], m[3])) continue;
    if (!hasContext(text, m.index!, SSN_CONTEXT)) continue; // a lone 9-digit number is not an SSN
    out.add('DS-SSN-01', 'medium', snippet(text, m.index!, m[0].length, `*****${m[3]}`));
  }
}

// ---------- Payment cards ----------
export function luhn(digits: string): boolean {
  let sum = 0;
  let dbl = false;
  for (let i = digits.length - 1; i >= 0; i--) {
    let d = digits.charCodeAt(i) - 48;
    if (dbl) {
      d *= 2;
      if (d > 9) d -= 9;
    }
    sum += d;
    dbl = !dbl;
  }
  return sum % 10 === 0;
}

function knownIssuer(d: string): boolean {
  if (/^4/.test(d) && (d.length === 13 || d.length === 16 || d.length === 19)) return true; // Visa
  if (/^(5[1-5]|2(2[2-9]|[3-6]\d|7[01]|720))/.test(d) && d.length === 16) return true; // Mastercard
  if (/^3[47]/.test(d) && d.length === 15) return true; // Amex
  if (/^(6011|65|64[4-9])/.test(d) && d.length >= 16) return true; // Discover
  return false;
}

const CARD_CONTEXT = /\b(card|visa|mastercard|amex|discover|cc|credit|exp(iry|iration)?|cvv)\b/i;

function detectCards(text: string, out: Hits) {
  const re = /\b\d(?:[ -]?\d){12,18}\b/g;
  for (const m of text.matchAll(re)) {
    const digits = m[0].replace(/\D/g, '');
    if (!knownIssuer(digits) || !luhn(digits)) continue;
    if (/^(\d)\1+$/.test(digits)) continue;
    const ctx = hasContext(text, m.index!, CARD_CONTEXT);
    out.add('DS-PCI-01', ctx ? 'high' : 'medium', snippet(text, m.index!, m[0].length, `**** **** **** ${digits.slice(-4)}`));
  }
}

// ---------- Bank routing + account ----------
export function isValidAba(n: string): boolean {
  if (!/^\d{9}$/.test(n)) return false;
  const d = n.split('').map(Number);
  const sum = 3 * (d[0] + d[3] + d[6]) + 7 * (d[1] + d[4] + d[7]) + (d[2] + d[5] + d[8]);
  return sum % 10 === 0 && n !== '000000000';
}

function detectBank(text: string, out: Hits) {
  const routing = /\b(?:routing|aba|rtn)\b[^\d\n]{0,20}(\d{9})\b/gi;
  for (const m of text.matchAll(routing)) {
    if (!isValidAba(m[1])) continue;
    const near = text.slice(m.index!, m.index! + 160);
    const acct = /\b(?:account|acct)\b[^\d\n]{0,20}(\d{6,17})\b/i.exec(near);
    const masked = `routing *****${m[1].slice(-4)}${acct ? `, account ****${acct[1].slice(-4)}` : ''}`;
    out.add('DS-BANK-01', acct ? 'high' : 'low', snippet(text, m.index!, m[0].length, masked));
  }
}

// ---------- Credentials ----------
function detectCredentials(text: string, out: Hits) {
  const pem = /-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----/g;
  const pemCount = [...text.matchAll(pem)].length;
  for (let i = 0; i < pemCount; i++) {
    out.add('DS-CRED-01', 'high', '-----BEGIN PRIVATE KEY----- [key body redacted]');
  }
  const pw = /\b(password|passwd|pwd|secret|api[_-]?key)\s*[:=]\s*("[^"\n]{4,}"|'[^'\n]{4,}'|[^\s;,"']{4,})/gi;
  for (const m of text.matchAll(pw)) {
    if (/^(\*+|<.*>|changeme|xxxx+|\{\{.*\}\})$/i.test(m[2].replace(/["']/g, ''))) continue;
    out.add('DS-CRED-02', 'medium', snippet(text, m.index!, m[0].length, `${m[1]}=••••••`));
  }
  const aws = /\bAKIA[0-9A-Z]{16}\b/g;
  for (const m of text.matchAll(aws)) {
    out.add('DS-CRED-02', 'high', snippet(text, m.index!, m[0].length, `AKIA************${m[0].slice(-4)}`));
  }
}

// ---------- Health information ----------
const HEALTH_TERMS = /\b(diagnos[ie]s|prescription|medication|patient|medical record|mrn|treatment plan|icd-10|hipaa|lab result|physician|clinic(al)?|fmla|disability claim)\b/gi;
const ICD10 = /\b[A-TV-Z]\d{2}\.\d{1,4}\b/g;

function detectHealth(text: string, out: Hits, hasIdentifier: boolean) {
  const terms = new Set([...text.matchAll(HEALTH_TERMS)].map((m) => m[1].toLowerCase()));
  const codes = [...text.matchAll(ICD10)];
  const signal = terms.size + Math.min(codes.length, 2);
  if (signal < 2) return;
  const confidence: Confidence = hasIdentifier && signal >= 3 ? 'high' : hasIdentifier || signal >= 3 ? 'medium' : 'low';
  const sample = `Terms: ${[...terms].slice(0, 4).join(', ')}${codes.length ? `; ${codes.length} ICD-10 code(s)` : ''}`;
  out.add('DS-PHI-01', confidence, sample);
}

// ---------- Date of birth ----------
function detectDob(text: string, out: Hits) {
  const re = /\b(?:dob|d\.o\.b\.|date of birth|birth ?date)\b[^\d\n]{0,8}(\d{1,2})[/-](\d{1,2})[/-](\d{2,4})/gi;
  for (const m of text.matchAll(re)) {
    out.add('DS-DOB-01', 'medium', snippet(text, m.index!, m[0].length, 'DOB **/**/' + m[3].slice(-2)));
  }
}

// ---------- engine ----------
class Hits {
  private map = new Map<string, Finding>();
  add(ruleId: string, confidence: Confidence, evidence: string) {
    const existing = this.map.get(ruleId);
    if (!existing) {
      this.map.set(ruleId, { ruleId, category: ruleCategory(ruleId), confidence, count: 1, evidence: [evidence] });
      return;
    }
    existing.count++;
    if (rank(confidence) > rank(existing.confidence)) existing.confidence = confidence;
    if (existing.evidence.length < MAX_EVIDENCE) existing.evidence.push(evidence);
  }
  list() {
    return [...this.map.values()];
  }
}

const rank = (c: Confidence) => (c === 'high' ? 3 : c === 'medium' ? 2 : 1);

export interface ScanResult {
  findings: Finding[];
  truncated: boolean;
}

export function scanText(input: string): ScanResult {
  const truncated = input.length > MAX_SCAN_CHARS;
  const text = truncated ? input.slice(0, MAX_SCAN_CHARS) : input;
  const hits = new Hits();
  detectSsn(text, hits);
  detectCards(text, hits);
  detectBank(text, hits);
  detectCredentials(text, hits);
  detectDob(text, hits);
  const identified = hits.list().some((f) => f.category === 'US SSN' || f.category === 'Date of birth') || /\bpatient name\b|\bemployee\b|\bname:/i.test(text);
  detectHealth(text, hits, identified);
  return { findings: hits.list(), truncated };
}

/** Classification policy: rule + confidence -> label. Versioned with POLICY_VERSION. */
export function classify(findings: Finding[], intendedPublic = false): Classification {
  let level: Classification = intendedPublic ? 'Public' : 'Internal';
  for (const f of findings) {
    const restricted =
      (f.confidence === 'high' && f.category !== 'Date of birth') ||
      (f.category === 'Credentials' && f.confidence !== 'low') ||
      (f.category === 'Health information' && f.confidence !== 'low');
    if (restricted) return 'Restricted';
    if (f.confidence !== 'low') level = 'Confidential';
  }
  return level;
}

export const CONFIDENCE_LABEL: Record<Confidence, string> = {
  high: 'High confidence',
  medium: 'Medium confidence',
  low: 'Low confidence',
};
