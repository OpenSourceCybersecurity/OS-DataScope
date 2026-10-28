import { describe, expect, it } from 'vitest';
import type { ReviewSummary } from '../../api/client';
import { FLAGS, decisionProblem, dueText, progress, splitReviews } from './reviews';

const TODAY = '2026-10-09';

describe('decision rules', () => {
  it('lets a keep through without a reason', () => {
    expect(decisionProblem('Keep', undefined, undefined, TODAY)).toBeNull();
  });

  it('asks for a reason before removing or investigating', () => {
    expect(decisionProblem('Remove', ' short ', undefined, TODAY)).toMatch(/should be removed/);
    expect(decisionProblem('Investigate', '', undefined, TODAY)).toMatch(/looked into/);
    expect(decisionProblem('Remove', 'Left the company in June.', undefined, TODAY)).toBeNull();
  });

  it('needs an exception to end after today and within a year', () => {
    const why = 'Auditors need it until year end.';
    expect(decisionProblem('Exception', why, undefined, TODAY)).toMatch(/Choose when/);
    expect(decisionProblem('Exception', why, TODAY, TODAY)).toMatch(/after today/);
    expect(decisionProblem('Exception', why, '2027-10-11', TODAY)).toMatch(/within a year/);
    expect(decisionProblem('Exception', why, '2027-10-10', TODAY)).toBeNull();
  });
});

describe('review display', () => {
  it('explains every flag the server can set', () => {
    const flags = ['Broad', 'DirectUser', 'DeletedAccount', 'OtherDomain', 'DisabledAccounts', 'UnresolvedMembers', 'CanChangePermissions', 'Conditional', 'NoMembers', 'NoDecisionNeeded'];
    for (const f of flags) expect(FLAGS[f as keyof typeof FLAGS].help).toBeTruthy();
  });

  it('counts a review with nothing to decide as complete', () => {
    expect(progress({ decided: 0, decisionsRequired: 0 })).toBe(1);
    expect(progress({ decided: 1, decisionsRequired: 4 })).toBe(0.25);
  });

  it('says how long until or since the due date', () => {
    expect(dueText('2026-10-09', TODAY)).toBe('Due today');
    expect(dueText('2026-10-10', TODAY)).toBe('Due tomorrow');
    expect(dueText('2026-10-19', TODAY)).toBe('Due in 10 days');
    expect(dueText('2026-10-08', TODAY)).toBe('1 day overdue');
    expect(dueText('2026-10-01', TODAY)).toBe('8 days overdue');
  });

  it('puts open reviews first, soonest due first', () => {
    const r = (id: string, dueOn: string, state: ReviewSummary['state'], campaignState: ReviewSummary['campaignState'] = 'Open') =>
      ({ id, dueOn, state, campaignState, resource: id, submittedAt: null }) as ReviewSummary;
    const { todo, done } = splitReviews([r('a', '2026-12-01', 'Pending'), r('b', '2026-11-01', 'InProgress'), r('c', '2026-10-01', 'Submitted'), r('d', '2026-10-01', 'Pending', 'Closed')]);
    expect(todo.map((x) => x.id)).toEqual(['b', 'a']);
    expect(done.map((x) => x.id).sort()).toEqual(['c', 'd']);
  });
});
