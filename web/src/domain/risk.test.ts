import { describe, expect, it } from 'vitest';
import { getState } from '../api/store';
import { computeRisks } from './risk';

describe('risk rules on the synthetic lab', () => {
  const s = getState();
  const risks = computeRisks({ dir: s.dir, shares: s.shares, files: s.files, owners: s.owners });
  const ids = risks.map((r) => r.id);
  it('flags HR Benefits opened to Domain Users as critical', () => {
    expect(risks.find((r) => r.id === 'R-open-sh-hr-Benefits')?.severity).toBe('Critical');
  });
  it('flags SSNs in the Public share', () => {
    expect(ids).toContain('R-open-sh-public-root');
  });
  it('does not flag Finance root as open (NTFS restricts it)', () => {
    expect(ids).not.toContain('R-open-sh-fin-root');
  });
  it('finds credentials, orphans, disabled accounts, nesting loop and coverage gaps', () => {
    for (const id of ['R-creds-sh-ops', 'R-creds-sh-eng', 'R-orphan-sh-fin', 'R-disabled-sh-fin', 'R-coverage-sh-scans']) {
      expect(ids).toContain(id);
    }
    expect(risks.some((r) => r.rule === 'Group nesting loop')).toBe(true);
    expect(risks.some((r) => r.rule === 'Write access through a nested group' && r.shareId === 'sh-fin')).toBe(true);
  });
});
