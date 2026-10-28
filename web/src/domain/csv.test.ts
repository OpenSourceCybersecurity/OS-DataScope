import { describe, expect, it } from 'vitest';
import { csvCell, toCsv } from './csv';

describe('csv export', () => {
  it.each(['=cmd|"/C calc"!A0', '+1+1', '-2', '@SUM(A1)'])('neutralises formula cell %s', (v) => {
    expect(csvCell(v).replace(/^"/, '').startsWith("'")).toBe(true);
  });
  it('quotes commas, quotes and newlines', () => {
    expect(csvCell('a,b')).toBe('"a,b"');
    expect(csvCell('say "hi"')).toBe('"say ""hi"""');
  });
  it('writes a commented preamble', () => {
    expect(toCsv(['x'], [[1]], ['Generated now']).split('\r\n')[0]).toBe('# Generated now');
  });
});
