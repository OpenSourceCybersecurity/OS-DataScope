import { describe, expect, it } from 'vitest';
import { fullScanProblem, fullScanText, hourLabel, hourlyStarts, jobOrigin, kindLabel, parseTime, scheduleProblem, scheduleText, startsText, timeLabel, timeValue, windowHours } from './schedule';

describe('scan schedules', () => {
  it('shows hours on a 12-hour clock', () => {
    expect([0, 1, 11, 12, 13, 23].map(hourLabel)).toEqual(['12:00 AM', '1:00 AM', '11:00 AM', '12:00 PM', '1:00 PM', '11:00 PM']);
  });

  it('shows minutes and reads time inputs', () => {
    expect(timeLabel(1, 30)).toBe('1:30 AM');
    expect(timeLabel(22, 5)).toBe('10:05 PM');
    expect(timeValue(1, 5)).toBe('01:05');
    expect(parseTime('22:15')).toEqual({ hour: 22, minute: 15 });
    expect(parseTime('07:30:00')).toEqual({ hour: 7, minute: 30 });
    expect(parseTime('')).toBeUndefined();
    expect(parseTime('25:00')).toBeUndefined();
    expect(parseTime('10:75')).toBeUndefined();
  });

  it('lists the starts of scans every few hours, inside their window', () => {
    expect(hourlyStarts({ startHour: 1, stopHour: null, everyHours: 3 })).toEqual([1, 4, 7, 10, 13, 16, 19, 22]);
    expect(hourlyStarts({ startHour: 22, stopHour: 6, everyHours: 4 })).toEqual([22, 2]);
    expect(hourlyStarts({ startHour: 8, stopHour: 18, everyHours: 2 })).toEqual([8, 10, 12, 14, 16]);
    expect(startsText({ startHour: 22, stopHour: 6, startMinute: 15, everyHours: 4 })).toBe('10:15 PM and 2:15 AM');
    expect(startsText({ startHour: 0, stopHour: null, everyHours: 6 })).toBe('12:00 AM, 6:00 AM, 12:00 PM and 6:00 PM');
    expect(startsText({ startHour: 0, stopHour: null, everyHours: 1 })).toBe('24 times a day');
  });

  it('describes a schedule in plain words', () => {
    expect(scheduleText({ frequency: 'Hourly', startHour: 22, startMinute: 15, everyHours: 4, day: null, stopHour: 6 })).toBe('Every 4 hours from 10:15 PM until 6:00 AM');
    expect(scheduleText({ frequency: 'Hourly', startHour: 0, startMinute: 30, everyHours: 1, day: null, stopHour: null })).toBe('Every hour from 12:30 AM, around the clock');
    expect(scheduleText({ frequency: 'Daily', startHour: 1, startMinute: 30, day: null, stopHour: null })).toBe('Every day at 1:30 AM, running until done');
    expect(scheduleText({ frequency: 'Daily', startHour: 1, day: null, stopHour: 7 })).toBe('Every day at 1:00 AM, pausing at 7:00 AM if not finished');
    expect(scheduleText({ frequency: 'Weekly', startHour: 22, day: 'Saturday', stopHour: null })).toBe('Every Saturday at 10:00 PM, running until done');
    expect(scheduleText({ frequency: 'Off', startHour: 1, day: null, stopHour: 7 })).toBe('Only when started by hand');
  });

  it('refuses the same settings the server refuses', () => {
    expect(scheduleProblem({ frequency: 'Daily', startHour: 1, day: null, stopHour: 7 })).toBeUndefined();
    expect(scheduleProblem({ frequency: 'Off', startHour: 1, day: null, stopHour: 1 })).toBeUndefined();
    expect(scheduleProblem({ frequency: 'Weekly', startHour: 1, day: null, stopHour: null })).toMatch(/day/);
    expect(scheduleProblem({ frequency: 'Daily', startHour: 3, day: null, stopHour: 3 })).toMatch(/differ/);
    expect(scheduleProblem({ frequency: 'Daily', startHour: 24, day: null, stopHour: null })).toMatch(/start/);
    expect(scheduleProblem({ frequency: 'Daily', startHour: -1, day: null, stopHour: null })).toMatch(/start/);
    expect(scheduleProblem({ frequency: 'Daily', startHour: 1, startMinute: 60, day: null, stopHour: null })).toMatch(/start/);
    expect(scheduleProblem({ frequency: 'Hourly', startHour: 0, startMinute: 15, everyHours: 4, day: null, stopHour: null })).toBeUndefined();
    expect(scheduleProblem({ frequency: 'Hourly', startHour: 0, everyHours: 5, day: null, stopHour: null })).toMatch(/hours apart/);
    expect(scheduleProblem({ frequency: 'Hourly', startHour: 0, everyHours: null, day: null, stopHour: null })).toMatch(/hours apart/);
    expect(scheduleProblem({ frequency: 'Hourly', startHour: 6, everyHours: 2, day: null, stopHour: 6 })).toMatch(/window/);
  });

  it('counts the hours a scan may run, across midnight', () => {
    expect(windowHours(1, 7)).toBe(6);
    expect(windowHours(22, 6)).toBe(8);
    expect(windowHours(1, null)).toBeNull();
  });

  it('says when a scan was scheduled and why one is paused', () => {
    expect(jobOrigin({ scheduled: true, keepToWindow: true, state: 'Running' })).toBe('Scheduled');
    expect(jobOrigin({ scheduled: true, keepToWindow: true, state: 'Paused' })).toMatch(/next scheduled time/);
    expect(jobOrigin({ scheduled: true, keepToWindow: false, state: 'Paused' })).toBe('Scheduled');
    expect(jobOrigin({ scheduled: false, keepToWindow: false, state: 'Completed' })).toBeUndefined();
  });

  it('describes full scans and tells them from quick scans', () => {
    const at = { startHour: 1, startMinute: 30 };
    expect(fullScanText({ fullScan: 'Weekly', fullScanDay: 'Saturday' }, at)).toBe('Full scan every Saturday at 1:30 AM');
    expect(fullScanText({ fullScan: 'Monthly', fullScanDay: 'Sunday' }, { startHour: 22 })).toBe('Full scan on the first Sunday of each month at 10:00 PM');
    expect(fullScanText({ fullScan: 'Off', fullScanDay: 'Saturday' }, at)).toBe('Full scans only when started by hand');
    expect(fullScanText({}, at)).toBe('Full scans only when started by hand');
    expect(fullScanProblem({ fullScan: 'Weekly', fullScanDay: null })).toBe('Choose the day of the full scan.');
    expect(fullScanProblem({ fullScan: 'Off', fullScanDay: null })).toBeUndefined();
    expect(fullScanProblem({ fullScan: 'Monthly', fullScanDay: 'Friday' })).toBeUndefined();
    expect(kindLabel({ kind: 'Full' })).toBe('Full scan');
    expect(kindLabel({ kind: 'Quick' })).toBe('Quick scan');
  });
});
