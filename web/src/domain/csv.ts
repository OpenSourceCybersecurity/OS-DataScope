// CSV export with spreadsheet formula-injection protection (spec A19).

const FORMULA_START = /^[=+\-@\t\r]/;

export function csvCell(value: unknown): string {
  let s = value == null ? '' : String(value);
  if (FORMULA_START.test(s)) s = `'${s}`; // neutralise =cmd|' /C calc'!A0 and friends
  if (/[",\n\r]/.test(s) || s !== s.trim()) s = `"${s.replace(/"/g, '""')}"`;
  return s;
}

export function toCsv(header: string[], rows: unknown[][], preamble: string[] = []): string {
  const lines = preamble.map((l) => `# ${l.replace(/[\r\n]+/g, ' ')}`);
  lines.push(header.map(csvCell).join(','));
  for (const r of rows) lines.push(r.map(csvCell).join(','));
  return lines.join('\r\n');
}
