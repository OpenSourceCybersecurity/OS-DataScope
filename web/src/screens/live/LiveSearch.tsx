import { useEffect, useId, useRef, useState, type KeyboardEvent as ReactKeyboardEvent } from 'react';
import { File, Folder, FolderTree, Loader2, Search, Server, User, Users, X } from 'lucide-react';
import { api, type SearchKind, type SearchResult } from '../../api/client';
import { useNav } from '../../App';
import { ClassPill, cx } from '../../ui/kit';
import { MIN_SEARCH, SEARCH_KIND_LABEL, searchFootnote, searchRows, searchTarget, shortPath, type SearchRow } from './search';

const ICON: Record<SearchKind, typeof User> = { User, Group: Users, Share: FolderTree, Server, Folder, File };

/**
 * The search box at the top of every page (D128): people, groups, shares, folders, files and file servers by name, or a
 * pasted \\server\share\path. Ctrl+K (⌘K on a Mac) jumps to it; arrow keys and Enter pick a result.
 */
export function LiveSearch() {
  const { go } = useNav();
  const [q, setQ] = useState('');
  const [open, setOpen] = useState(false);
  const [result, setResult] = useState<SearchResult>();
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string>();
  const [active, setActive] = useState(0);
  const box = useRef<HTMLDivElement>(null);
  const input = useRef<HTMLInputElement>(null);
  const listId = useId();

  useEffect(() => {
    const outside = (e: MouseEvent) => !box.current?.contains(e.target as Node) && setOpen(false);
    const shortcut = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        input.current?.focus();
        input.current?.select();
        setOpen(true);
      }
    };
    document.addEventListener('mousedown', outside);
    document.addEventListener('keydown', shortcut);
    return () => {
      document.removeEventListener('mousedown', outside);
      document.removeEventListener('keydown', shortcut);
    };
  }, []);

  // Search as the person types, a moment after they pause; a newer search cancels the one in flight.
  const term = q.trim();
  useEffect(() => {
    if (term.length < MIN_SEARCH) {
      setResult(undefined);
      setProblem(undefined);
      setBusy(false);
      return;
    }
    const abort = new AbortController();
    setBusy(true);
    const t = setTimeout(() => {
      api.search(term, { signal: abort.signal }).then(
        (r) => {
          setResult(r);
          setProblem(undefined);
          setActive(0);
          setBusy(false);
        },
        (e) => {
          if (abort.signal.aborted) return;
          setProblem(e instanceof Error ? e.message : String(e));
          setBusy(false);
        },
      );
    }, 200);
    return () => {
      clearTimeout(t);
      abort.abort();
    };
  }, [term]);

  // Results for an older search stay on screen while the next one runs, but only if they still apply.
  const current = result && result.query === term ? result : busy ? result : undefined;
  const rows = searchRows(current);
  const pick = (row: SearchRow) => {
    const target = searchTarget(row.kind, row.hit);
    go(target.route, target.param);
    setOpen(false);
    setQ('');
    input.current?.blur();
  };
  const onKey = (e: ReactKeyboardEvent) => {
    if (e.key === 'Escape') {
      if (q) setQ('');
      else input.current?.blur();
      setOpen(false);
    } else if (e.key === 'ArrowDown' && rows.length) {
      e.preventDefault();
      setOpen(true);
      setActive((a) => (a + 1) % rows.length);
    } else if (e.key === 'ArrowUp' && rows.length) {
      e.preventDefault();
      setActive((a) => (a - 1 + rows.length) % rows.length);
    } else if (e.key === 'Enter' && rows[active]) {
      e.preventDefault();
      pick(rows[active]);
    }
  };

  const showPanel = open && term.length >= MIN_SEARCH;
  const footnote = searchFootnote(current);
  const kinds = [...new Set(rows.map((r) => r.kind))];

  return (
    // On a phone the results drop down across the whole top bar (the nearest positioned box), not just the narrow field.
    <div ref={box} className="sm:relative flex-1 min-w-0 max-w-[480px]">
      <div className="relative">
      <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-ink-3 pointer-events-none" />
      <input
        ref={input}
        id="global-search"
        type="search"
        role="combobox"
        aria-expanded={showPanel}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={showPanel && rows[active] ? `${listId}-${active}` : undefined}
        aria-label="Search people, groups, shares, folders and files"
        autoComplete="off"
        spellCheck={false}
        maxLength={200}
        value={q}
        onChange={(e) => {
          setQ(e.target.value);
          setOpen(true);
        }}
        onFocus={() => setOpen(true)}
        onKeyDown={onKey}
        placeholder="Search people, groups, shares, paths…"
        className="w-full h-9 pl-9 pr-9 sm:pr-16 rounded-lg bg-surface border border-line text-[13.5px] outline-none focus:border-accent [&::-webkit-search-cancel-button]:hidden"
      />
      {q ? (
        <button
          type="button"
          className="absolute right-2 top-1/2 -translate-y-1/2 p-1 rounded text-ink-3 hover:text-ink"
          onClick={() => {
            setQ('');
            input.current?.focus();
          }}
          aria-label="Clear search"
        >
          {busy ? <Loader2 size={14} className="animate-spin" /> : <X size={14} />}
        </button>
      ) : (
        <kbd className="hidden sm:block absolute right-2.5 top-1/2 -translate-y-1/2 text-[11px] text-ink-3 font-mono border border-line rounded px-1 pointer-events-none">
          Ctrl K
        </kbd>
      )}
      </div>

      {showPanel && (
        <div
          className="absolute left-4 right-4 top-[52px] sm:left-0 sm:right-0 sm:top-11 bg-surface border border-line rounded-xl overflow-hidden z-40 sm:min-w-[420px]"
          style={{ boxShadow: 'var(--shadow-pop)' }}
        >
          <ul id={listId} role="listbox" aria-label="Search results" className="max-h-[min(70vh,560px)] overflow-y-auto scroll-thin">
            {kinds.map((kind) => (
              <li key={kind} role="presentation" className="py-1.5 border-b border-line last:border-0">
                <div className="eyebrow px-4 py-1" aria-hidden>{SEARCH_KIND_LABEL[kind]}</div>
                <ul role="group" aria-label={SEARCH_KIND_LABEL[kind]}>
                  {rows.map((row, i) => row.kind === kind && (
                    <ResultRow key={row.key} id={`${listId}-${i}`} row={row} active={i === active} onHover={() => setActive(i)} onPick={() => pick(row)} />
                  ))}
                </ul>
              </li>
            ))}
          </ul>
          {problem && <div className="px-4 py-3 text-[13px]" style={{ color: 'var(--s-critical)' }}>Search didn't work: {problem}</div>}
          {!problem && current && !rows.length && (
            <div className="px-4 py-3 text-[13px] text-ink-3">
              Nothing matches “{current.query}”.
              {!current.query.startsWith('\\') && <> To jump to a folder, paste its path, like <span className="font-mono">\\fs01\HR\Payroll</span>.</>}
            </div>
          )}
          {!problem && !current && busy && <div className="px-4 py-3 text-[13px] text-ink-3">Searching…</div>}
          {!problem && footnote && <div className="px-4 py-2 text-[12px] text-ink-3 border-t border-line bg-surface-2">{footnote}</div>}
        </div>
      )}
    </div>
  );
}

function ResultRow({ id, row, active, onHover, onPick }: { id: string; row: SearchRow; active: boolean; onHover: () => void; onPick: () => void }) {
  const Icon = ICON[row.kind];
  const mono = row.kind === 'Share' || row.kind === 'Folder' || row.kind === 'File';
  const { hit } = row;
  return (
    <li
      id={id}
      role="option"
      aria-selected={active}
      onMouseEnter={onHover}
      onMouseDown={(e) => e.preventDefault()}
      onClick={onPick}
      className={cx('flex items-start gap-3 px-4 py-1.5 cursor-pointer', active && 'bg-surface-2')}
    >
      <Icon size={16} strokeWidth={1.9} className="mt-0.5 shrink-0 text-ink-3" />
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-2 min-w-0">
          <span className={cx('truncate text-[13.5px]', hit.disabled && 'text-ink-3')}>{hit.title}</span>
          {hit.disabled && <span className="text-[11.5px] text-ink-3 shrink-0">Disabled</span>}
          {hit.exact && <span className="text-[11.5px] font-medium text-accent shrink-0">Exact path</span>}
        </span>
        {hit.detail && <span className={cx('block truncate text-[12px] text-ink-3', mono && 'font-mono')} title={hit.detail}>{mono ? shortPath(hit.detail) : hit.detail}</span>}
      </span>
      {hit.label && hit.label !== 'Unknown' && <span className="shrink-0 mt-0.5"><ClassPill c={hit.label} /></span>}
    </li>
  );
}
