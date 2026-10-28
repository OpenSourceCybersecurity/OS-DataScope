import { useCallback, useEffect, useRef, useState } from 'react';

/** Loads data, optionally re-polling while `pollMs` is set. Exposes `reload` for after a change. */
export function useData<T>(load: () => Promise<T>, deps: unknown[], pollMs?: number) {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<string>();
  const loadRef = useRef(load);
  loadRef.current = load;

  const reload = useCallback(async () => {
    try {
      setData(await loadRef.current());
      setError(undefined);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    void reload();
  }, deps); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!pollMs) return;
    const t = setInterval(() => void reload(), pollMs);
    return () => clearInterval(t);
  }, [pollMs, reload]);

  return { data, error, reload };
}
