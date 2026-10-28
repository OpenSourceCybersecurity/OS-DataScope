import { useEffect, useState } from 'react';
import { api, type PersonRef } from '../../api/client';
import { inputCls } from '../../ui/kit';

/** People search for administrators, from the directory sync. Owners use the narrower /my/people search instead. */
export const searchDirectoryUsers = async (q: string): Promise<PersonRef[]> =>
  (await api.principals(q, 'User', 0, 20)).items
    .filter((p) => p.enabled)
    .map((p) => ({ sid: p.sid, name: p.displayName ? `${p.displayName} (${p.name})` : p.name, department: p.department, title: p.title }));

/** Type-ahead for choosing one enabled person from Active Directory. */
export function PersonPicker({
  id,
  value,
  onChange,
  search,
  placeholder = 'Type a name',
  exclude = [],
  emptyText = 'No enabled people match yet.',
}: {
  id: string;
  value: PersonRef | null;
  onChange: (p: PersonRef | null) => void;
  search: (q: string) => Promise<PersonRef[]>;
  placeholder?: string;
  exclude?: string[];
  emptyText?: string;
}) {
  const [q, setQ] = useState('');
  const [results, setResults] = useState<PersonRef[]>([]);
  const [error, setError] = useState<string>();

  useEffect(() => {
    const term = q.trim();
    if (term.length < 2) {
      setResults([]);
      return;
    }
    let live = true;
    const t = setTimeout(() => {
      search(term).then(
        (r) => live && (setResults(r.filter((p) => !exclude.includes(p.sid))), setError(undefined)),
        (e) => live && setError(e instanceof Error ? e.message : String(e)),
      );
    }, 250);
    return () => {
      live = false;
      clearTimeout(t);
    };
  }, [q]); // eslint-disable-line react-hooks/exhaustive-deps

  if (value) {
    return (
      <div className="flex items-center justify-between gap-2 h-9 px-3 rounded-md border border-line bg-surface-2">
        <span className="truncate text-[13.5px]">
          {value.name}
          {value.department && <span className="text-ink-3"> · {value.department}</span>}
        </span>
        <button type="button" className="text-[12.5px] text-accent hover:underline shrink-0" onClick={() => onChange(null)}>
          Change
        </button>
      </div>
    );
  }
  return (
    <div className="relative">
      <input id={id} className={inputCls} value={q} placeholder={placeholder} onChange={(e) => setQ(e.target.value)} autoComplete="off" />
      {error && <div className="text-[12px] mt-1" style={{ color: 'var(--s-critical)' }}>{error}</div>}
      {results.length > 0 && (
        <ul className="absolute z-10 left-0 right-0 mt-1 max-h-60 overflow-y-auto scroll-thin bg-surface border border-line rounded-lg" style={{ boxShadow: 'var(--shadow-pop)' }}>
          {results.map((p) => (
            <li key={p.sid}>
              <button
                type="button"
                className="w-full text-left px-3 py-1.5 hover:bg-surface-2"
                onClick={() => {
                  onChange(p);
                  setQ('');
                  setResults([]);
                }}
              >
                <span className="block text-[13.5px]">{p.name}</span>
                {(p.department || p.title) && <span className="block text-[12px] text-ink-3">{[p.title, p.department].filter(Boolean).join(' · ')}</span>}
              </button>
            </li>
          ))}
        </ul>
      )}
      {q.trim().length >= 2 && results.length === 0 && !error && <div className="text-[12px] text-ink-3 mt-1">{emptyText}</div>}
    </div>
  );
}
