import { useId, useState, type PointerEvent } from 'react';
import { fmtNum } from '../../ui/kit';
import { fmtDay, niceMax, plot } from './reporting';

const W = 300;
const H = 96;
const PAD = { top: 8, right: 10, bottom: 22, left: 44 };

/**
 * One measure over time: a thin line from 0, the latest value labelled, and a value for whichever day the pointer is on.
 * Any series of days will do: scan totals on Overview, the daily posture on Progress.
 */
export function TrendChart<T extends { day: string }>({ title, points, value, format = fmtNum, top: fixedTop }: {
  title: string;
  points: T[];
  value: (p: T) => number;
  format?: (n: number) => string;
  /** The top of the scale, such as 100 for a percentage; otherwise from the highest value. */
  top?: number;
}) {
  const [hover, setHover] = useState<number | null>(null);
  const id = useId();
  const top = fixedTop ?? niceMax(Math.max(...points.map(value)));
  const pts = plot(points, value, W, H, top);
  const last = pts[pts.length - 1];
  const shown = hover === null ? null : pts[hover];
  const line = pts.map((p, i) => `${i ? 'L' : 'M'}${p.x.toFixed(1)},${p.y.toFixed(1)}`).join('');

  const onMove = (e: PointerEvent<SVGRectElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    const x = ((e.clientX - box.left) / box.width) * W;
    let best = 0;
    pts.forEach((p, i) => {
      if (Math.abs(p.x - x) < Math.abs(pts[best].x - x)) best = i;
    });
    setHover(best);
  };

  return (
    <figure className="min-w-0">
      <figcaption id={id} className="flex items-baseline justify-between gap-2 text-[13px]">
        <span className="text-ink-2 font-medium">{title}</span>
        <span className="tnum text-ink">{format(shown?.value ?? last.value)}</span>
      </figcaption>
      <div className="text-[11.5px] text-ink-3 tnum h-4">{shown ? fmtDay(shown.day) : `latest, ${fmtDay(last.day)}`}</div>
      <svg viewBox={`0 0 ${W + PAD.left + PAD.right} ${H + PAD.top + PAD.bottom}`} className="w-full h-auto mt-1" role="img" aria-labelledby={id}>
        <g transform={`translate(${PAD.left},${PAD.top})`}>
          {[0, 0.5, 1].map((f) => (
            <g key={f}>
              <line x1={0} x2={W} y1={H - f * H} y2={H - f * H} stroke="var(--line)" strokeWidth={1} />
              <text x={-6} y={H - f * H} dy="0.32em" textAnchor="end" fontSize={10} fill="var(--ink-3)" className="tnum">
                {format(top * f)}
              </text>
            </g>
          ))}
          <text x={0} y={H + 15} fontSize={10} fill="var(--ink-3)">{fmtDay(points[0].day)}</text>
          <text x={W} y={H + 15} fontSize={10} fill="var(--ink-3)" textAnchor="end">{fmtDay(last.day)}</text>
          <path d={line} fill="none" stroke="var(--accent)" strokeWidth={2} strokeLinejoin="round" strokeLinecap="round" />
          {shown && <line x1={shown.x} x2={shown.x} y1={0} y2={H} stroke="var(--ink-3)" strokeWidth={1} strokeDasharray="2 3" />}
          <circle cx={(shown ?? last).x} cy={(shown ?? last).y} r={4} fill="var(--accent)" stroke="var(--surface)" strokeWidth={2} />
          <rect x={0} y={0} width={W} height={H} fill="transparent" onPointerMove={onMove} onPointerLeave={() => setHover(null)} />
        </g>
      </svg>
      <table className="sr-only">
        <caption>{title} by day</caption>
        <tbody>
          {pts.map((p) => (
            <tr key={p.day}>
              <th scope="row">{fmtDay(p.day)}</th>
              <td>{format(p.value)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </figure>
  );
}
