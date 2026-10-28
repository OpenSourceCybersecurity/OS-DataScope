import type { FullScanFrequency, ScanJob, ScanSchedule, ScanScheduleInput, Weekday } from '../../api/client';

export const WEEKDAYS: Weekday[] = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

/** How far apart scans every few hours may be: the steps that divide a day evenly. Mirrors ScanSchedule.HourSteps. */
export const HOUR_STEPS = [1, 2, 3, 4, 6, 8, 12];

/** 0 → "12:00 AM", 13 → "1:00 PM". */
export function hourLabel(hour: number): string {
  return timeLabel(hour, 0);
}

/** (1, 30) → "1:30 AM". */
export function timeLabel(hour: number, minute: number): string {
  const h = ((hour % 24) + 24) % 24;
  return `${h % 12 === 0 ? 12 : h % 12}:${String(minute).padStart(2, '0')} ${h < 12 ? 'AM' : 'PM'}`;
}

/** "01:30" for a time input. */
export function timeValue(hour: number, minute: number): string {
  return `${String(hour).padStart(2, '0')}:${String(minute).padStart(2, '0')}`;
}

/** The hour and minute from a time input's "HH:MM", or undefined when it is empty or not a time. */
export function parseTime(value: string): { hour: number; minute: number } | undefined {
  const m = /^(\d{1,2}):(\d{2})/.exec(value);
  if (!m) return undefined;
  const hour = Number(m[1]);
  const minute = Number(m[2]);
  return hour <= 23 && minute <= 59 ? { hour, minute } : undefined;
}

type Times = Pick<ScanSchedule, 'startHour' | 'stopHour'> & { startMinute?: number; everyHours?: number | null };

/** The hours a scan every few hours starts, in the order they come from the start hour. Mirrors ScanSchedule.Starts. */
export function hourlyStarts(s: Times): number[] {
  const every = s.everyHours && HOUR_STEPS.includes(s.everyHours) ? s.everyHours : 24;
  const hours: number[] = [];
  for (let k = 0; k < 24 / every; k++) {
    const hour = (s.startHour + k * every) % 24;
    if (inWindow(s, hour)) hours.push(hour);
  }
  return hours;
}

function inWindow(s: Times, hour: number): boolean {
  const stop = s.stopHour;
  if (stop == null) return true;
  return s.startHour < stop ? hour >= s.startHour && hour < stop : hour >= s.startHour || hour < stop;
}

/** "Every day at 1:00 AM, pausing at 7:00 AM if not finished", "Every 4 hours from 10:15 PM until 6:00 AM", or "Only when started by hand". */
export function scheduleText(s: Pick<ScanSchedule, 'frequency' | 'startHour' | 'day' | 'stopHour'> & { startMinute?: number; everyHours?: number | null }): string {
  if (s.frequency === 'Off') return 'Only when started by hand';
  const start = timeLabel(s.startHour, s.startMinute ?? 0);
  if (s.frequency === 'Hourly') {
    const every = s.everyHours === 1 ? 'Every hour' : `Every ${s.everyHours ?? '?'} hours`;
    return s.stopHour == null ? `${every} from ${start}, around the clock` : `${every} from ${start} until ${hourLabel(s.stopHour)}`;
  }
  const when = s.frequency === 'Weekly' ? `Every ${s.day ?? 'week'} at ${start}` : `Every day at ${start}`;
  return s.stopHour == null ? `${when}, running until done` : `${when}, pausing at ${hourLabel(s.stopHour)} if not finished`;
}

/** "10:15 PM and 2:15 AM", or "6 times a day" when there are too many to list. */
export function startsText(s: Times): string {
  const starts = hourlyStarts(s);
  if (starts.length > 6) return `${starts.length} times a day`;
  const labels = starts.map((h) => timeLabel(h, s.startMinute ?? 0));
  return labels.length <= 2 ? labels.join(' and ') : `${labels.slice(0, -1).join(', ')} and ${labels[labels.length - 1]}`;
}

/** Why the server would refuse these settings, or undefined. Mirrors ScanSchedule.Problem. */
export function scheduleProblem(s: ScanScheduleInput): string | undefined {
  if (s.frequency === 'Off') return undefined;
  if (!Number.isInteger(s.startHour) || s.startHour < 0 || s.startHour > 23) return 'Choose a start time.';
  const minute = s.startMinute ?? 0;
  if (!Number.isInteger(minute) || minute < 0 || minute > 59) return 'Choose a start time.';
  if (s.frequency === 'Hourly' && !(s.everyHours != null && HOUR_STEPS.includes(s.everyHours))) return 'Choose how many hours apart.';
  if (s.frequency === 'Weekly' && !s.day) return 'Choose the day of the week.';
  if (s.stopHour != null && (!Number.isInteger(s.stopHour) || s.stopHour < 0 || s.stopHour > 23 || s.stopHour === s.startHour))
    return s.frequency === 'Hourly' ? 'The end of the window must differ from the start hour.' : 'The pause time must differ from the start time.';
  return undefined;
}

/** How many hours a scheduled scan may run each time, or null when it runs until done. */
export function windowHours(startHour: number, stopHour: number | null): number | null {
  if (stopHour == null) return null;
  return (stopHour - startHour + 24) % 24;
}

/** The status line for a scan: scheduled or by hand, and why a scheduled one is paused. */
export function jobOrigin(job: Pick<ScanJob, 'scheduled' | 'keepToWindow' | 'state'>): string | undefined {
  if (job.state === 'Paused' && job.keepToWindow) return 'Paused for the day; continues at the next scheduled time';
  return job.scheduled ? 'Scheduled' : undefined;
}

/** "Full scan" or "Quick scan". */
export function kindLabel(job: Pick<ScanJob, 'kind'>): string {
  return job.kind === 'Full' ? 'Full scan' : 'Quick scan';
}

/** "Full scan every Saturday at 1:00 AM", "Full scan on the first Saturday of each month at 1:00 AM", or "Full scans only when started by hand". */
export function fullScanText(f: { fullScan?: FullScanFrequency; fullScanDay?: Weekday | null }, s: { startHour: number; startMinute?: number }): string {
  if (!f.fullScan || f.fullScan === 'Off' || !f.fullScanDay) return 'Full scans only when started by hand';
  const at = timeLabel(Math.max(0, s.startHour), Math.max(0, s.startMinute ?? 0));
  return f.fullScan === 'Monthly' ? `Full scan on the first ${f.fullScanDay} of each month at ${at}` : `Full scan every ${f.fullScanDay} at ${at}`;
}

/** Why the server would refuse the full scan settings, or undefined. Mirrors ScanSchedule.Problem(FullScanSettings). */
export function fullScanProblem(f: { fullScan?: FullScanFrequency; fullScanDay?: Weekday | null }): string | undefined {
  if (f.fullScan && f.fullScan !== 'Off' && !f.fullScanDay) return 'Choose the day of the full scan.';
  return undefined;
}
