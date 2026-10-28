import { createContext, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  Activity,
  ClipboardCheck,
  ClipboardList,
  FileSearch,
  FolderTree,
  Gauge,
  FileText,
  FolderClock,
  LogOut,
  Menu,
  Moon,
  Search,
  Server,
  SlidersHorizontal,
  ListFilter,
  Radar,
  Scale,
  ShieldAlert,
  Sun,
  UserCheck,
  UserSearch,
  UserX,
  Users,
  X,
  ScanSearch,
  TrendingDown,
  HeartPulse,
} from 'lucide-react';
import { ROLES, actions, useApp } from './api/store';
import { canManage, canReadActivity, canReadPosture, canReadReviews, canReadSettings, hasNoRole, isOwner, signOut, useBackend, type Backend } from './api/backend';
import { Logo, SignIn, SignInProblem, Starting } from './screens/SignIn';
import { computeRisks, type Risk } from './domain/risk';
import { Avatar, cx } from './ui/kit';
import Overview from './screens/Overview';
import Discovery from './screens/Discovery';
import Shares from './screens/Shares';
import AccessExplorer from './screens/AccessExplorer';
import Risks from './screens/Risks';
import Reviews from './screens/Reviews';
import ActivityScreen from './screens/Activity';
import Owners from './screens/Owners';
import MyReviews from './screens/MyReviews';
import Reports from './screens/Reports';
import Setup from './screens/Setup';
import Exclusions from './screens/Exclusions';
import Cleanup from './screens/Cleanup';
import SystemSettings from './screens/SystemSettings';
import SystemHealth from './screens/SystemHealth';
import ScanScope from './screens/ScanScope';
import ExposureMapScreen from './screens/ExposureMap';
import Compliance from './screens/Compliance';
import CustomRulesScreen from './screens/CustomRules';
import PersonSearchScreen from './screens/PersonSearch';
import ProgressScreen from './screens/ProgressScreen';
import { LiveSearch } from './screens/live/LiveSearch';

export type Route =
  | 'overview'
  | 'discovery'
  | 'cleanup'
  | 'shares'
  | 'access'
  | 'risks'
  | 'reviews'
  | 'my-reviews'
  | 'activity'
  | 'owners'
  | 'reports'
  | 'setup'
  | 'exclusions'
  | 'system-settings'
  | 'health'
  | 'scan-scope'
  | 'exposure'
  | 'compliance'
  | 'custom-rules'
  | 'person'
  | 'progress';

interface Nav {
  route: Route;
  param?: string;
  go: (r: Route, param?: string) => void;
  risks: Risk[];
  role: (typeof ROLES)[number];
  readOnly: boolean;
}
const NavCtx = createContext<Nav>(null!);
export const useNav = () => useContext(NavCtx);

const NAV: { id: Route; label: string; icon: typeof Gauge; group: string; roles: string[] }[] = [
  { id: 'overview', label: 'Overview', icon: Gauge, group: 'Posture', roles: ['admin', 'auditor', 'owner-fin'] },
  { id: 'risks', label: 'Risks', icon: ShieldAlert, group: 'Posture', roles: ['admin', 'auditor'] },
  { id: 'progress', label: 'Progress', icon: TrendingDown, group: 'Posture', roles: ['admin', 'auditor'] },
  { id: 'exposure', label: 'Exposure map', icon: Radar, group: 'Posture', roles: ['admin', 'auditor'] },
  { id: 'discovery', label: 'Sensitive data', icon: FileSearch, group: 'Data', roles: ['admin', 'auditor'] },
  { id: 'cleanup', label: 'Stale & duplicates', icon: FolderClock, group: 'Data', roles: ['admin', 'auditor'] },
  { id: 'shares', label: 'File shares', icon: FolderTree, group: 'Data', roles: ['admin', 'auditor', 'owner-fin'] },
  { id: 'access', label: 'Who has access', icon: Users, group: 'Data', roles: ['admin', 'auditor'] },
  { id: 'activity', label: 'File activity', icon: Activity, group: 'Data', roles: ['admin', 'auditor'] },
  { id: 'person', label: 'Find a person', icon: UserSearch, group: 'Data', roles: ['admin'] },
  { id: 'my-reviews', label: 'My reviews', icon: ClipboardList, group: 'Governance', roles: ['owner-fin'] },
  { id: 'reviews', label: 'Access reviews', icon: ClipboardCheck, group: 'Governance', roles: ['admin', 'auditor'] },
  { id: 'owners', label: 'Owners', icon: UserCheck, group: 'Governance', roles: ['admin', 'auditor'] },
  { id: 'reports', label: 'Reports', icon: FileText, group: 'Governance', roles: ['admin', 'auditor'] },
  { id: 'setup', label: 'Servers & scans', icon: Server, group: 'Settings', roles: ['admin'] },
  { id: 'scan-scope', label: 'Scan scope & exclusions', icon: ListFilter, group: 'Settings', roles: ['admin', 'auditor'] },
  { id: 'compliance', label: 'Compliance standards', icon: Scale, group: 'Settings', roles: ['admin', 'auditor'] },
  { id: 'custom-rules', label: 'Custom detection', icon: ScanSearch, group: 'Settings', roles: ['admin', 'auditor'] },
  { id: 'exclusions', label: 'Review exclusions', icon: UserX, group: 'Settings', roles: ['admin', 'auditor'] },
  { id: 'health', label: 'System health', icon: HeartPulse, group: 'Settings', roles: ['admin', 'auditor'] },
  { id: 'system-settings', label: 'System settings', icon: SlidersHorizontal, group: 'Settings', roles: ['admin', 'auditor'] },
];

const SCREENS: Record<Route, () => ReactNode> = {
  overview: Overview,
  discovery: Discovery,
  cleanup: Cleanup,
  shares: Shares,
  access: AccessExplorer,
  risks: Risks,
  reviews: Reviews,
  'my-reviews': MyReviews,
  activity: ActivityScreen,
  owners: Owners,
  reports: Reports,
  setup: Setup,
  exclusions: Exclusions,
  'system-settings': SystemSettings,
  health: SystemHealth,
  'scan-scope': ScanScope,
  exposure: ExposureMapScreen,
  compliance: Compliance,
  'custom-rules': CustomRulesScreen,
  person: PersonSearchScreen,
  progress: ProgressScreen,
};

function readHash(): Route {
  try {
    const h = location.hash.replace('#', '') as Route;
    return h in SCREENS ? h : 'overview';
  } catch {
    return 'overview';
  }
}

// Screens wired to the Data Scope API. The rest still run on the synthetic demo store.
const LIVE_ROUTES = new Set<Route>(['overview', 'risks', 'setup', 'shares', 'discovery', 'cleanup', 'access', 'activity', 'owners', 'reviews', 'my-reviews', 'reports', 'exclusions', 'system-settings', 'health', 'scan-scope', 'exposure', 'compliance', 'custom-rules', 'person', 'progress']);

/**
 * Which screens a signed-in person sees on a real server, from their Data Scope roles. The server enforces access;
 * this only keeps people from landing on screens that would refuse them. Owners need no role for My reviews.
 */
export function liveRoutes(backend: Backend): Set<Route> {
  if (backend.mode !== 'live') return new Set();
  const routes = new Set<Route>();
  if (canReadPosture(backend)) ['overview', 'risks', 'progress', 'exposure', 'discovery', 'cleanup', 'shares', 'access', 'owners', 'reports', 'setup', 'compliance'].forEach((r) => routes.add(r as Route));
  if (canReadActivity(backend)) routes.add('activity');
  // A person search brings together everything about someone: platform admins only (D160).
  if (canManage(backend)) routes.add('person');
  if (canReadReviews(backend)) ['reviews', 'exclusions'].forEach((r) => routes.add(r as Route));
  if (canReadSettings(backend)) ['system-settings', 'health', 'scan-scope', 'custom-rules'].forEach((r) => routes.add(r as Route));
  if (isOwner(backend.me)) routes.add('my-reviews');
  return routes;
}

export default function App() {
  const s = useApp();
  const backend = useBackend();
  const [route, setRoute] = useState<Route>(readHash);
  const [param, setParam] = useState<string | undefined>();
  const [menuOpen, setMenuOpen] = useState(false);
  const role = ROLES.find((r) => r.id === s.roleId)!;
  const live = backend.mode === 'live' ? liveRoutes(backend) : null;
  const allowed = NAV.filter((n) => (live ? live.has(n.id) : n.roles.includes(role.id)));
  const risks = useMemo(() => computeRisks({ dir: s.dir, shares: s.shares, files: s.files, owners: s.owners }), [s.dir, s.shares, s.files, s.owners]);

  const go = (r: Route, p?: string) => {
    setRoute(r);
    setParam(p);
    setMenuOpen(false);
    try {
      history.replaceState(null, '', `#${r}`);
    } catch {
      /* sandboxed */
    }
    document.getElementById('main')?.scrollTo({ top: 0 });
  };

  useEffect(() => {
    const onHash = () => {
      setRoute(readHash());
      setParam(undefined);
    };
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  }, []);

  const allowedKey = allowed.map((n) => n.id).join();
  useEffect(() => {
    if (allowed.length && !allowed.some((n) => n.id === route)) go(allowed[0].id);
  }, [allowedKey]); // eslint-disable-line react-hooks/exhaustive-deps

  const Screen = SCREENS[route];
  if (backend.mode === 'checking') return <Starting />;
  if (backend.mode === 'signed-out' || backend.mode === 'signing-in') return <SignIn busy={backend.mode === 'signing-in'} />;
  if (backend.mode === 'signin-failed' || hasNoRole(backend)) return <SignInProblem backend={backend} />;
  const openRisks = risks.filter((r) => r.severity === 'Critical' || r.severity === 'High').length;
  const pendingReviews = s.items.filter((i) => i.decision === 'Pending' && s.campaigns.find((c) => c.id === i.campaignId && c.status !== 'Completed' && (role.role !== 'Data Owner' || c.reviewerSid === role.sid))).length;
  const badge: Partial<Record<Route, number>> =
    backend.mode === 'live'
      ? { 'my-reviews': backend.me.openReviews }
      : { risks: openRisks, reviews: pendingReviews, 'my-reviews': pendingReviews, activity: s.alerts.filter((a) => a.state === 'Open').length };

  const nav = (
    <nav className="flex flex-col h-full">
      <div className="flex items-center gap-2.5 px-4 h-14 shrink-0">
        <Logo />
        <span className="font-display text-[17px] font-[650] text-rail-strong tracking-[-0.01em]">Data Scope</span>
      </div>
      <div className="flex-1 overflow-y-auto scroll-thin px-2 pb-4">
        {['Posture', 'Data', 'Governance', 'Settings'].map((g) => {
          const items = allowed.filter((n) => n.group === g);
          if (!items.length) return null;
          return (
            <div key={g} className="mt-4">
              <div className="px-2.5 mb-1 text-[10.5px] tracking-[0.1em] uppercase font-semibold text-rail-ink/60">{g}</div>
              {items.map((n) => (
                <button
                  key={n.id}
                  onClick={() => go(n.id)}
                  className={cx(
                    'w-full flex items-center gap-2.5 px-2.5 h-9 rounded-md text-[13.5px] text-left',
                    route === n.id ? 'bg-rail-active text-rail-strong font-medium' : 'text-rail-ink hover:text-rail-strong hover:bg-rail-active/60',
                  )}
                  aria-current={route === n.id ? 'page' : undefined}
                >
                  <n.icon size={17} strokeWidth={1.9} />
                  <span className="flex-1">{n.label}</span>
                  {!!badge[n.id] && (
                    <span className="min-w-5 h-5 px-1.5 rounded-full text-[11px] font-semibold grid place-items-center bg-[#ffffff1a] text-rail-strong tnum">{badge[n.id]}</span>
                  )}
                  {n.id === 'activity' && backend.mode !== 'live' && <span className="text-[10px] font-semibold uppercase tracking-wide text-rail-ink/70">Preview</span>}
                </button>
              ))}
            </div>
          );
        })}
      </div>
      <div className="px-4 py-3 text-[11.5px] text-rail-ink/70 border-t border-white/5">
        {backend.mode === 'live' ? (
          <>
            Connected as {backend.me.name}
            {backend.me.version && <> · v{backend.me.version}</>}
          </>
        ) : (
          <>Demo data · NORTHWIND.LOCAL</>
        )}
        <br />
        On-premises. No data leaves this network.
      </div>
    </nav>
  );

  return (
    <NavCtx.Provider value={{ route, param, go, risks, role, readOnly: backend.mode === 'live' ? false : role.role === 'Auditor' }}>
      <div className="h-full flex">
        <aside className="hidden lg:block w-[232px] shrink-0 bg-rail">{nav}</aside>
        {menuOpen && (
          <div className="lg:hidden fixed inset-0 z-50 flex">
            <div className="w-[260px] bg-rail h-full" style={{ paddingTop: 'env(safe-area-inset-top, 0px)' }}>{nav}</div>
            <button className="flex-1 bg-black/40" aria-label="Close menu" onClick={() => setMenuOpen(false)} />
          </div>
        )}
        <div className="flex-1 min-w-0 flex flex-col">
          <TopBar onMenu={() => setMenuOpen(true)} />
          <main id="main" className="flex-1 overflow-y-auto scroll-thin">
            {backend.mode === 'demo' && backend.chosen && (
              <div className="px-4 sm:px-8 pt-4">
                <div className="rounded-lg text-[13px] px-3.5 py-2 text-ink-2 flex flex-wrap items-center gap-x-3 gap-y-1" style={{ background: 'var(--s-medium-soft)' }}>
                  <span className="flex-1 min-w-[220px]">You're exploring a demo company. Every person, file and finding here is made up; nothing comes from your network.</span>
                  <button type="button" onClick={signOut} className="font-medium text-accent hover:underline">Sign in to your data</button>
                </div>
              </div>
            )}
            {backend.mode !== 'live' && role.role === 'Data Owner' && (
              <div className="px-4 sm:px-8 pt-4">
                <div className="rounded-lg bg-accent-soft text-[13px] px-3.5 py-2 text-ink-2">
                  You are signed in as a data owner. You see only the shares you own and the reviews assigned to you.
                </div>
              </div>
            )}
            {backend.mode !== 'live' && role.role === 'Auditor' && (
              <div className="px-4 sm:px-8 pt-4">
                <div className="rounded-lg bg-surface-2 text-[13px] px-3.5 py-2 text-ink-2">Auditor view. Everything is read-only; exports are logged.</div>
              </div>
            )}
            {backend.mode === 'live' && !LIVE_ROUTES.has(route) && (
              <div className="px-4 sm:px-8 pt-4">
                <div className="rounded-lg text-[13px] px-3.5 py-2 text-ink-2" style={{ background: 'var(--s-medium-soft)' }}>
                  Demo data. This screen still shows the synthetic Northwind company; it switches to your servers in a later milestone. Every other screen shows live data.
                </div>
              </div>
            )}
            <div className="px-4 sm:px-8 py-6 max-w-[1320px]">
              <Screen key={route + (param ?? '')} />
            </div>
          </main>
        </div>
      </div>
      <Toasts />
    </NavCtx.Provider>
  );
}

function TopBar({ onMenu }: { onMenu: () => void }) {
  const s = useApp();
  const backend = useBackend();
  const role = ROLES.find((r) => r.id === s.roleId)!;
  const [theme, setTheme] = useState<'light' | 'dark' | null>(null);
  const toggleTheme = () => {
    const dark = theme ? theme === 'dark' : matchMedia('(prefers-color-scheme: dark)').matches;
    const next = dark ? 'light' : 'dark';
    document.documentElement.dataset.theme = next;
    setTheme(next);
  };
  const isDark = theme ? theme === 'dark' : typeof matchMedia !== 'undefined' && matchMedia('(prefers-color-scheme: dark)').matches;
  return (
    <header
      className="sticky z-30 bg-bg/90 backdrop-blur border-b border-line flex items-center gap-3 px-4 sm:px-8 h-14 shrink-0"
      style={{ top: 'env(safe-area-inset-top, 0px)' }}
    >
      <button className="lg:hidden p-1.5 -ml-1.5 rounded-md hover:bg-surface-2" onClick={onMenu} aria-label="Open menu">
        <Menu size={20} />
      </button>
      {backend.mode === 'live' ? canReadPosture(backend) && <LiveSearch /> : <GlobalSearch />}
      <div className={cx('flex-1', backend.mode === 'live' && canReadPosture(backend) && 'hidden sm:block')} />
      <button onClick={toggleTheme} className="p-2 rounded-md text-ink-2 hover:bg-surface-2" aria-label="Toggle theme" title="Toggle light/dark">
        {isDark ? <Sun size={17} /> : <Moon size={17} />}
      </button>
      {backend.mode === 'live' ? (
        <span className="pl-3 border-l border-line flex items-center gap-2">
          <span className="leading-tight hidden sm:block" title={backend.me.name}>
            <span className="block text-[13px] font-medium">{backend.me.name}</span>
            <span className="block text-[11.5px] text-ink-3">{backend.me.roles.length ? backend.me.roles.join(', ') : 'Data owner'}</span>
          </span>
          <button onClick={signOut} className="p-2 rounded-md text-ink-2 hover:bg-surface-2" aria-label="Sign out" title="Sign out">
            <LogOut size={16} />
          </button>
        </span>
      ) : (
      <label className="flex items-center gap-2 pl-2 border-l border-line" title="Switch persona to see role-based scoping">
        <Avatar sid={role.sid} size={28} />
        <span className="hidden md:block leading-tight">
          <span className="block text-[13px] font-medium">{role.label}</span>
          <span className="block text-[11.5px] text-ink-3">{role.role}</span>
        </span>
        <select
          id="persona"
          aria-label="Viewing as"
          value={s.roleId}
          onChange={(e) => actions.setRole(e.target.value)}
          className="h-8 rounded-md bg-surface border border-line text-[12.5px] px-1.5 max-w-[118px]"
        >
          {ROLES.map((r) => (
            <option key={r.id} value={r.id}>
              {r.label} ({r.role})
            </option>
          ))}
        </select>
      </label>
      )}
    </header>
  );
}

function GlobalSearch() {
  const s = useApp();
  const { go, role } = useNav();
  const [q, setQ] = useState('');
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const h = (e: MouseEvent) => !ref.current?.contains(e.target as Node) && setOpen(false);
    const k = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key === 'k') {
        e.preventDefault();
        ref.current?.querySelector('input')?.focus();
      }
    };
    document.addEventListener('mousedown', h);
    document.addEventListener('keydown', k);
    return () => {
      document.removeEventListener('mousedown', h);
      document.removeEventListener('keydown', k);
    };
  }, []);
  const term = q.trim().toLowerCase();
  const results = useMemo(() => {
    if (term.length < 2) return null;
    const people = [...s.dir.values()].filter((p) => (p.displayName + ' ' + p.sam).toLowerCase().includes(term)).slice(0, 5);
    const shares = s.shares.filter((x) => (x.name + ' ' + x.unc).toLowerCase().includes(term)).slice(0, 4);
    const files = s.files.filter((f) => f.path.toLowerCase().includes(term)).slice(0, 6);
    return { people, shares, files };
  }, [term, s.dir, s.shares, s.files]);
  if (role.role === 'Data Owner') return <div />;
  const pickItem = (r: Route, p: string) => {
    go(r, p);
    setOpen(false);
    setQ('');
  };
  return (
    <div ref={ref} className="relative w-full max-w-[440px] hidden sm:block">
      <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-ink-3" />
      <input
        id="global-search"
        value={q}
        onChange={(e) => {
          setQ(e.target.value);
          setOpen(true);
        }}
        onFocus={() => setOpen(true)}
        placeholder="Search people, groups, shares, paths…"
        className="w-full h-9 pl-9 pr-14 rounded-lg bg-surface border border-line text-[13.5px] outline-none focus:border-accent"
      />
      {q ? (
        <button className="absolute right-2 top-1/2 -translate-y-1/2 p-1 text-ink-3" onClick={() => setQ('')} aria-label="Clear search">
          <X size={14} />
        </button>
      ) : (
        <kbd className="hidden sm:block absolute right-2.5 top-1/2 -translate-y-1/2 text-[11px] text-ink-3 font-mono border border-line rounded px-1">Ctrl K</kbd>
      )}
      {open && results && (
        <div className="absolute left-0 right-0 top-11 bg-surface border border-line rounded-xl overflow-hidden z-40" style={{ boxShadow: 'var(--shadow-pop)' }}>
          {!results.people.length && !results.shares.length && !results.files.length && <div className="px-4 py-3 text-ink-3 text-[13px]">No matches for “{q}”.</div>}
          {!!results.people.length && <SearchGroup label="People and groups">{results.people.map((p) => (
            <SearchItem key={p.sid} onClick={() => pickItem('access', p.sid)} title={p.displayName} sub={p.type === 'user' ? `${p.title} · ${p.department}` : 'Group'} />
          ))}</SearchGroup>}
          {!!results.shares.length && <SearchGroup label="Shares">{results.shares.map((x) => (
            <SearchItem key={x.id} onClick={() => pickItem('shares', x.id)} title={x.name} sub={x.unc} mono />
          ))}</SearchGroup>}
          {!!results.files.length && <SearchGroup label="Files">{results.files.map((f) => (
            <SearchItem key={f.id} onClick={() => pickItem('discovery', f.id)} title={f.name} sub={`${s.shares.find((x) => x.id === f.shareId)?.name}\\${f.path.replace(/\//g, '\\')}`} mono />
          ))}</SearchGroup>}
        </div>
      )}
    </div>
  );
}
function SearchGroup({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="py-1.5 border-b border-line last:border-0">
      <div className="eyebrow px-4 py-1">{label}</div>
      {children}
    </div>
  );
}
function SearchItem({ title, sub, onClick, mono }: { title: string; sub: string; onClick: () => void; mono?: boolean }) {
  return (
    <button onClick={onClick} className="w-full text-left px-4 py-1.5 hover:bg-surface-2 block">
      <span className="block text-[13.5px] truncate">{title}</span>
      <span className={cx('block text-[12px] text-ink-3 truncate', mono && 'font-mono')}>{sub}</span>
    </button>
  );
}

function Toasts() {
  const { toasts } = useApp();
  return (
    <div className="fixed z-[60] bottom-4 right-4 left-4 sm:left-auto flex flex-col gap-2 items-end pointer-events-none" style={{ paddingBottom: 'env(safe-area-inset-bottom, 0px)' }} aria-live="polite">
      {toasts.map((t) => (
        <div key={t.id} className="pointer-events-auto max-w-[420px] rounded-lg px-4 py-2.5 text-[13.5px] bg-ink text-bg flex gap-2" style={{ boxShadow: 'var(--shadow-pop)' }}>
          <span className="mt-1 size-2 rounded-full shrink-0" style={{ background: t.tone === 'ok' ? 'var(--s-good)' : 'var(--s-medium)' }} />
          {t.text}
        </div>
      ))}
    </div>
  );
}
