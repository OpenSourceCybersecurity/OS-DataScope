import { useEffect, type ReactNode } from 'react';
import { AlertOctagon, AlertTriangle, ChevronRight, Info, ShieldAlert, X } from 'lucide-react';
import type { Classification, Confidence, Rights, Severity } from '../domain/types';
import { NOW } from '../data/seed';
import { getState } from '../api/store';

export const cx = (...xs: (string | false | null | undefined)[]) => xs.filter(Boolean).join(' ');

// ---------- formatting ----------
export function ago(iso?: string): string {
  if (!iso) return 'never';
  const now = Math.max(Date.now(), NOW.getTime());
  const s = (now - new Date(iso).getTime()) / 1000;
  if (s < 0) return inDays(iso);
  if (s < 90) return 'just now';
  if (s < 3600) return `${Math.round(s / 60)} min ago`;
  if (s < 86400) return `${Math.round(s / 3600)} h ago`;
  const d = Math.round(s / 86400);
  if (d < 45) return `${d} day${d > 1 ? 's' : ''} ago`;
  if (d < 600) return `${Math.round(d / 30)} months ago`;
  return `${(d / 365).toFixed(1)} years ago`;
}
export function inDays(iso: string): string {
  const d = Math.round((new Date(iso).getTime() - Math.max(Date.now(), NOW.getTime())) / 864e5);
  if (d === 0) return 'today';
  return d > 0 ? `in ${d} day${d > 1 ? 's' : ''}` : `${-d} day${d < -1 ? 's' : ''} overdue`;
}
export const fmtDate = (iso?: string) =>
  iso ? new Date(iso).toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' }) : '—';
export const fmtDateTime = (iso?: string) =>
  iso ? new Date(iso).toLocaleString('en-US', { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' }) : '—';
export const fmtNum = (n: number) => n.toLocaleString('en-US');
export function fmtBytes(b: number): string {
  if (b < 1024) return `${b} B`;
  if (b < 1024 ** 2) return `${(b / 1024).toFixed(0)} KB`;
  if (b < 1024 ** 3) return `${(b / 1024 ** 2).toFixed(1)} MB`;
  return `${(b / 1024 ** 3).toFixed(2)} GB`;
}
export const rightsLabel = (r: Rights) => (r === 'FullControl' ? 'Full control' : r === 'None' ? 'No access' : r);
export const nameOf = (sid: string) => (sid === 'system' ? 'Data Scope' : getState().dir.get(sid)?.displayName ?? 'Unknown account');

// ---------- primitives ----------
type BtnProps = {
  children: ReactNode;
  onClick?: () => void;
  variant?: 'primary' | 'secondary' | 'ghost' | 'danger';
  size?: 'sm' | 'md';
  disabled?: boolean;
  title?: string;
  type?: 'button' | 'submit';
  className?: string;
};
const buttonCls = (variant: BtnProps['variant'], size: BtnProps['size'], className?: string) =>
  cx(
    'inline-flex items-center justify-center gap-1.5 rounded-md font-medium transition-colors whitespace-nowrap disabled:opacity-45 disabled:cursor-not-allowed',
    size === 'sm' ? 'h-7 px-2.5 text-[13px]' : 'h-9 px-3.5 text-sm',
    variant === 'primary' && 'bg-accent text-accent-ink hover:brightness-110',
    variant === 'secondary' && 'bg-surface border border-line text-ink hover:bg-surface-2',
    variant === 'ghost' && 'text-ink-2 hover:bg-surface-2 hover:text-ink',
    variant === 'danger' && 'bg-[var(--s-critical)] text-white hover:brightness-110',
    className,
  );

export function Button({ children, onClick, variant = 'secondary', size = 'md', disabled, title, type = 'button', className }: BtnProps) {
  return (
    <button type={type} title={title} disabled={disabled} onClick={onClick} className={buttonCls(variant, size, className)}>
      {children}
    </button>
  );
}

/** A link that looks like a button, for downloads and pages the browser opens itself. */
export function ButtonLink({ children, href, variant = 'secondary', size = 'md', newTab, download, className }: Omit<BtnProps, 'onClick' | 'type' | 'disabled' | 'title'> & { href: string; newTab?: boolean; download?: boolean }) {
  return (
    <a href={href} className={buttonCls(variant, size, className)} {...(newTab ? { target: '_blank', rel: 'noopener noreferrer' } : {})} {...(download ? { download: '' } : {})}>
      {children}
    </a>
  );
}

export function Card({ children, className, title, action, pad = true, id }: { children: ReactNode; className?: string; title?: ReactNode; action?: ReactNode; pad?: boolean; id?: string }) {
  return (
    <section id={id} className={cx('bg-surface border border-line rounded-xl min-w-0 scroll-mt-4', className)}>
      {title && (
        <header className="flex flex-wrap items-center justify-between gap-3 px-4 pt-3.5 pb-2">
          <h2 className="text-[13px] font-semibold text-ink">{title}</h2>
          {action}
        </header>
      )}
      <div className={cx(pad && 'px-4 pb-4', !title && pad && 'pt-4')}>{children}</div>
    </section>
  );
}

export function PageHeader({ title, sub, actions, eyebrow }: { title: string; sub?: ReactNode; actions?: ReactNode; eyebrow?: string }) {
  return (
    <div className="flex flex-wrap items-end justify-between gap-4 mb-5">
      <div className="min-w-0">
        {eyebrow && <div className="eyebrow mb-1">{eyebrow}</div>}
        <h1 className="font-display text-[26px] leading-tight font-[650] tracking-[-0.01em]">{title}</h1>
        {sub && <p className="text-ink-2 mt-1 max-w-[68ch]">{sub}</p>}
      </div>
      {actions && <div className="flex flex-wrap gap-2">{actions}</div>}
    </div>
  );
}

const CLASS_VAR: Record<Classification, string> = {
  Unknown: 'var(--c-unknown)',
  Public: 'var(--c-public)',
  Internal: 'var(--c-internal)',
  Confidential: 'var(--c-confidential)',
  Restricted: 'var(--c-restricted)',
};
export const classColor = (c: Classification) => CLASS_VAR[c];

export function ClassPill({ c, overridden }: { c: Classification; overridden?: boolean }) {
  return (
    <span
      className="inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-[12px] font-medium border whitespace-nowrap"
      style={{ borderColor: `color-mix(in oklab, ${CLASS_VAR[c]} 45%, transparent)`, color: 'var(--ink)', background: `color-mix(in oklab, ${CLASS_VAR[c]} 12%, transparent)` }}
      title={overridden ? 'Set by a reviewer' : 'Set by classification policy'}
    >
      <span
        className="size-2 rounded-full"
        style={c === 'Unknown' ? { border: `1.5px dashed ${CLASS_VAR[c]}` } : { background: CLASS_VAR[c] }}
      />
      {c}
      {overridden && <span className="text-ink-3">· reviewed</span>}
    </span>
  );
}

const SEV: Record<Severity, { fg: string; bg: string; Icon: typeof Info }> = {
  Critical: { fg: 'var(--s-critical)', bg: 'var(--s-critical-soft)', Icon: AlertOctagon },
  High: { fg: 'var(--s-high)', bg: 'var(--s-high-soft)', Icon: ShieldAlert },
  Medium: { fg: 'var(--s-medium)', bg: 'var(--s-medium-soft)', Icon: AlertTriangle },
  Low: { fg: 'var(--s-low)', bg: 'var(--s-low-soft)', Icon: Info },
};
export const sevColor = (s: Severity) => SEV[s].fg;
export function SeverityPill({ s }: { s: Severity }) {
  const { fg, bg, Icon } = SEV[s];
  return (
    <span className="inline-flex items-center gap-1 rounded-md px-1.5 py-0.5 text-[12px] font-semibold whitespace-nowrap" style={{ color: fg, background: bg }}>
      <Icon size={13} strokeWidth={2.4} />
      {s}
    </span>
  );
}

export function ConfidencePill({ c }: { c: Confidence }) {
  const bars = c === 'high' ? 3 : c === 'medium' ? 2 : 1;
  return (
    <span className="inline-flex items-center gap-1.5 text-[12px] text-ink-2 whitespace-nowrap" title={`${c} confidence`}>
      <span className="inline-flex items-end gap-[2px] h-3">
        {[1, 2, 3].map((i) => (
          <span key={i} className="w-[3px] rounded-sm" style={{ height: `${i * 4}px`, background: i <= bars ? 'var(--ink-2)' : 'var(--line)' }} />
        ))}
      </span>
      {c[0].toUpperCase() + c.slice(1)}
    </span>
  );
}

export function Tag({ children, tone = 'neutral', title }: { children: ReactNode; tone?: 'neutral' | 'warn' | 'bad' | 'good' | 'accent'; title?: string }) {
  const map = {
    neutral: ['var(--ink-2)', 'var(--surface-2)'],
    warn: ['var(--s-medium)', 'var(--s-medium-soft)'],
    bad: ['var(--s-critical)', 'var(--s-critical-soft)'],
    good: ['var(--s-good)', 'var(--s-good-soft)'],
    accent: ['var(--accent)', 'var(--accent-soft)'],
  }[tone];
  return (
    <span title={title} className="inline-flex items-center gap-1 rounded px-1.5 py-[1px] text-[11.5px] font-medium whitespace-nowrap" style={{ color: map[0], background: map[1] }}>
      {children}
    </span>
  );
}

export function Avatar({ sid, size = 26 }: { sid: string; size?: number }) {
  const p = getState().dir.get(sid);
  const name = p?.displayName ?? '?';
  const initials = p?.type === 'user' ? name.split(' ').map((x) => x[0]).slice(0, 2).join('') : p ? name.replace(/[^A-Za-z]/g, '').slice(0, 2) : '?';
  let h = 5381;
  for (const c of sid) h = ((h * 33) ^ c.charCodeAt(0)) >>> 0;
  const hue = h % 360;
  const isGroup = p?.type !== 'user';
  return (
    <span
      className={cx('inline-grid place-items-center shrink-0 font-semibold text-[11px]', isGroup ? 'rounded-md' : 'rounded-full')}
      style={{
        width: size,
        height: size,
        fontSize: size * 0.4,
        color: isGroup ? 'var(--ink-2)' : 'var(--ink)',
        background: isGroup ? 'var(--surface-3)' : `color-mix(in oklab, hsl(${hue} 55% 60%) 28%, var(--surface))`,
        border: !p ? '1.5px dashed var(--ink-3)' : undefined,
      }}
      aria-hidden
    >
      {initials}
    </span>
  );
}

export function Person({ sid, sub, size }: { sid: string; sub?: ReactNode; size?: number }) {
  const p = getState().dir.get(sid);
  return (
    <span className="inline-flex items-center gap-2 min-w-0">
      <Avatar sid={sid} size={size} />
      <span className="min-w-0">
        <span className={cx('block truncate', !p && 'font-mono text-[12px]')}>
          {p ? p.displayName : sid}
          {p && !p.enabled && <span className="ml-1.5"><Tag tone="warn">Disabled</Tag></span>}
        </span>
        {(sub ?? p?.title) && <span className="block text-[12px] text-ink-3 truncate">{sub ?? (p?.type === 'user' ? `${p.title} · ${p.department}` : p?.title)}</span>}
      </span>
    </span>
  );
}

export function Meter({ value, tone = 'accent', label }: { value: number; tone?: 'accent' | 'warn' | 'good'; label?: string }) {
  const color = tone === 'accent' ? 'var(--accent)' : tone === 'warn' ? 'var(--s-medium)' : 'var(--s-good)';
  return (
    <div className="h-1.5 rounded-full bg-surface-3 overflow-hidden" role="progressbar" aria-valuenow={Math.round(value * 100)} aria-label={label}>
      <div className="h-full rounded-full transition-[width] duration-300" style={{ width: `${Math.max(0, Math.min(1, value)) * 100}%`, background: color }} />
    </div>
  );
}

export function Drawer({ open, onClose, title, sub, children, width = 560 }: { open: boolean; onClose: () => void; title: ReactNode; sub?: ReactNode; children: ReactNode; width?: number }) {
  useEffect(() => {
    if (!open) return;
    const h = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', h);
    return () => window.removeEventListener('keydown', h);
  }, [open, onClose]);
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-40 flex justify-end">
      <div className="absolute inset-0 bg-[rgb(8_20_24/0.35)]" onClick={onClose} />
      <aside
        className="drawer-in relative h-full bg-surface border-l border-line flex flex-col"
        style={{ width: `min(${width}px, 100vw)`, boxShadow: 'var(--shadow-pop)', paddingTop: 'env(safe-area-inset-top, 0px)' }}
        role="dialog"
        aria-modal
      >
        <header className="flex items-start justify-between gap-3 px-5 py-4 border-b border-line">
          <div className="min-w-0">
            <h2 className="font-display text-lg font-[650] leading-snug break-words">{title}</h2>
            {sub && <div className="text-ink-2 text-[13px] mt-0.5">{sub}</div>}
          </div>
          <button onClick={onClose} className="p-1.5 rounded-md hover:bg-surface-2 text-ink-2" aria-label="Close">
            <X size={18} />
          </button>
        </header>
        <div className="flex-1 overflow-y-auto scroll-thin px-5 py-4">{children}</div>
      </aside>
    </div>
  );
}

export function Tabs<T extends string>({ value, onChange, tabs }: { value: T; onChange: (t: T) => void; tabs: { id: T; label: ReactNode }[] }) {
  return (
    <div className="flex gap-1 border-b border-line mb-4 overflow-x-auto scroll-thin" role="tablist">
      {tabs.map((t) => (
        <button
          key={t.id}
          role="tab"
          aria-selected={value === t.id}
          onClick={() => onChange(t.id)}
          className={cx(
            'px-3 py-2 text-sm font-medium border-b-2 -mb-px whitespace-nowrap',
            value === t.id ? 'border-accent text-ink' : 'border-transparent text-ink-3 hover:text-ink',
          )}
        >
          {t.label}
        </button>
      ))}
    </div>
  );
}

export function Segmented<T extends string>({ value, onChange, options }: { value: T; onChange: (t: T) => void; options: { id: T; label: ReactNode }[] }) {
  return (
    <div className="inline-flex flex-wrap max-w-full rounded-md border border-line bg-surface-2 p-0.5">
      {options.map((o) => (
        <button
          key={o.id}
          type="button"
          aria-pressed={value === o.id}
          onClick={() => onChange(o.id)}
          className={cx('px-2.5 h-7 text-[13px] rounded-[5px] font-medium whitespace-nowrap', value === o.id ? 'bg-surface text-ink shadow-sm' : 'text-ink-3 hover:text-ink')}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

export function Field({ label, children, hint, htmlFor }: { label: string; children: ReactNode; hint?: ReactNode; htmlFor?: string }) {
  return (
    <label className="block" htmlFor={htmlFor}>
      <span className="block text-[13px] font-medium mb-1">{label}</span>
      {children}
      {hint && <span className="block text-[12px] text-ink-3 mt-1">{hint}</span>}
    </label>
  );
}

const fieldBase = 'h-9 px-3 rounded-md bg-surface border border-line text-ink placeholder:text-ink-3 focus:border-accent outline-none';
export const inputCls = `w-full ${fieldBase}`;
/** Inline filter control that sizes to its content. */
export const filterCls = `w-auto max-w-full ${fieldBase}`;

export function Empty({ icon, title, children }: { icon?: ReactNode; title: string; children?: ReactNode }) {
  return (
    <div className="text-center py-10 px-4">
      {icon && <div className="mx-auto mb-3 size-10 grid place-items-center rounded-full bg-surface-2 text-ink-3">{icon}</div>}
      <div className="font-medium">{title}</div>
      {children && <div className="text-ink-3 text-[13px] mt-1 max-w-[46ch] mx-auto">{children}</div>}
    </div>
  );
}

export function Row({ children, onClick, className }: { children: ReactNode; onClick?: () => void; className?: string }) {
  return (
    <tr onClick={onClick} className={cx('border-t border-line', onClick && 'cursor-pointer hover:bg-surface-2', className)}>
      {children}
    </tr>
  );
}

export function Th({ children, className }: { children?: ReactNode; className?: string }) {
  return <th className={cx('text-left font-medium text-[12px] text-ink-3 px-3 py-2 whitespace-nowrap', className)}>{children}</th>;
}
export function Td({ children, className }: { children?: ReactNode; className?: string }) {
  return <td className={cx('px-3 py-2.5 align-middle', className)}>{children}</td>;
}

export function Callout({ tone = 'info', children, title }: { tone?: 'info' | 'warn' | 'bad' | 'good'; children: ReactNode; title?: string }) {
  const c = {
    info: ['var(--accent)', 'var(--accent-soft)'],
    warn: ['var(--s-medium)', 'var(--s-medium-soft)'],
    bad: ['var(--s-critical)', 'var(--s-critical-soft)'],
    good: ['var(--s-good)', 'var(--s-good-soft)'],
  }[tone];
  return (
    <div className="rounded-lg px-3.5 py-2.5 text-[13px]" style={{ background: c[1], color: 'var(--ink)' }}>
      {title && <div className="font-semibold mb-0.5" style={{ color: c[0] }}>{title}</div>}
      <div className="text-ink-2">{children}</div>
    </div>
  );
}

export function LinkArrow({ children, onClick }: { children: ReactNode; onClick: () => void }) {
  return (
    <button onClick={onClick} className="inline-flex items-center gap-0.5 text-[13px] font-medium text-accent hover:underline">
      {children}
      <ChevronRight size={14} />
    </button>
  );
}

export function Unc({ children }: { children: ReactNode }) {
  return <span className="font-mono text-[12.5px] break-all">{children}</span>;
}
