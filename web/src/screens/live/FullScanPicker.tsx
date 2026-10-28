import type { FullScanFrequency, Weekday } from '../../api/client';
import { Segmented, filterCls } from '../../ui/kit';
import { WEEKDAYS } from './schedule';

/**
 * How often a server gets a full scan (D130), which reads every file again with more time per file. It starts at the
 * scan start time on the chosen day and takes the place of that day's quick scan.
 */
export function FullScanPicker({ id, frequency, day, onChange }: {
  id: string;
  frequency: FullScanFrequency;
  day: Weekday | null;
  onChange: (frequency: FullScanFrequency, day: Weekday | null) => void;
}) {
  return (
    <div className="space-y-2" role="group" aria-label="Full scans">
      <div className="font-medium">Full scans</div>
      <div className="flex flex-wrap items-center gap-3">
        <Segmented<FullScanFrequency>
          value={frequency}
          onChange={(f) => onChange(f, f === 'Off' ? day : day ?? 'Saturday')}
          options={[
            { id: 'Weekly', label: 'Weekly' },
            { id: 'Monthly', label: 'Monthly' },
            { id: 'Off', label: 'By hand' },
          ]}
        />
        {frequency !== 'Off' && (
          <label className="flex items-center gap-2" htmlFor={id}>
            <span>{frequency === 'Monthly' ? 'First' : 'Every'}</span>
            <select id={id} className={filterCls} value={day ?? ''} onChange={(e) => onChange(frequency, e.target.value as Weekday)}>
              {day == null && <option value="">Choose</option>}
              {WEEKDAYS.map((d) => <option key={d} value={d}>{d}</option>)}
            </select>
            {frequency === 'Monthly' && <span>of the month</span>}
          </label>
        )}
      </div>
      <p className="text-[12px] text-ink-3">
        A full scan reads every file again, changed or not, with more time for each one, so long scanned documents and large files are read further.
        It starts at the scan start time and takes the place of that day's quick scan. Quick scans read only new and changed files.
      </p>
    </div>
  );
}
