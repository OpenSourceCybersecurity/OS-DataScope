import type { ReactNode } from 'react';
import { ArrowRight, FileSearch, KeyRound, Loader2, ShieldCheck, Users } from 'lucide-react';
import { exploreDemo, signIn, signOut, type Backend } from '../api/backend';

/** The brand mark: a scope ring with a sensitivity wedge. */
export function Logo({ size = 26 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 26 26" aria-hidden>
      <circle cx="13" cy="13" r="11" fill="none" stroke="var(--rail-ink)" strokeWidth="2" />
      <path d="M13 13 L13 2 A11 11 0 0 1 23.5 9.6 Z" fill="var(--c-restricted)" />
      <circle cx="13" cy="13" r="4" fill="var(--rail-ink-strong)" />
    </svg>
  );
}

const POINTS = [
  { icon: FileSearch, text: 'Find files that may hold SSNs, card numbers, health records and passwords.' },
  { icon: Users, text: 'See who can open them, and why, down to the group and permission entry.' },
  { icon: ShieldCheck, text: 'Have the people who own the data confirm who should keep access.' },
];

/** Two panels: the product on the left (on top on a phone), the action on the right. */
function Frame({ children }: { children: ReactNode }) {
  return (
    <div className="min-h-full flex flex-col lg:flex-row bg-bg">
      <section className="bg-rail text-rail-ink lg:w-[44%] lg:min-h-full px-6 sm:px-10 py-8 lg:py-14 flex flex-col">
        <div className="flex items-center gap-2.5">
          <Logo size={30} />
          <span className="font-display text-[19px] font-[650] text-rail-strong tracking-[-0.01em]">Data Scope</span>
        </div>
        <div className="mt-8 lg:mt-auto max-w-[440px]">
          <h1 className="font-display text-rail-strong text-[26px] sm:text-[32px] leading-[1.15] font-[650] tracking-[-0.015em] [text-wrap:balance]">
            Know where sensitive data lives and who can reach it.
          </h1>
          <ul className="mt-6 space-y-3.5 hidden sm:block">
            {POINTS.map((p) => (
              <li key={p.text} className="flex gap-3 text-[14px] leading-relaxed">
                <p.icon size={18} className="shrink-0 mt-0.5 text-rail-strong/80" strokeWidth={1.8} />
                <span>{p.text}</span>
              </li>
            ))}
          </ul>
        </div>
        <p className="mt-8 lg:mt-auto text-[12px] text-rail-ink/70">Runs on your own server. Files, findings and sign-ins never leave this network.</p>
      </section>
      <main className="flex-1 flex justify-center items-start lg:items-center px-4 sm:px-10 py-10">
        <div className="w-full max-w-[400px]">{children}</div>
      </main>
    </div>
  );
}

function DemoLink() {
  return (
    <div className="mt-8 pt-6 border-t border-line">
      <div className="text-[13px] text-ink-2">Just looking around?</div>
      <button
        type="button"
        onClick={exploreDemo}
        className="mt-1.5 inline-flex items-center gap-1.5 text-[14px] font-medium text-accent hover:underline"
      >
        Explore the demo company <ArrowRight size={15} />
      </button>
      <p className="mt-1 text-[12.5px] text-ink-3">Made-up people and files. Nothing from your network is shown.</p>
    </div>
  );
}

function PrimaryButton({ children, onClick, busy }: { children: ReactNode; onClick: () => void; busy?: boolean }) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={busy}
      className="w-full h-11 rounded-lg bg-accent text-accent-ink text-[14.5px] font-medium inline-flex items-center justify-center gap-2 hover:brightness-110 disabled:opacity-70"
    >
      {busy ? <Loader2 size={17} className="animate-spin" /> : <KeyRound size={17} />}
      {children}
    </button>
  );
}

/** The first page on a Data Scope server. Windows sign-in starts only when the person chooses it. */
export function SignIn({ busy }: { busy?: boolean }) {
  return (
    <Frame>
      <h2 className="font-display text-[24px] font-[650] text-ink tracking-[-0.01em]">Sign in</h2>
      <p className="mt-2 text-[14px] text-ink-2 leading-relaxed">Use the Windows account you signed in to this computer with. There's no separate password.</p>
      <div className="mt-6">
        <PrimaryButton onClick={() => void signIn()} busy={busy}>{busy ? 'Signing in…' : 'Sign in with Windows'}</PrimaryButton>
      </div>
      <p className="mt-3 text-[12.5px] text-ink-3 leading-relaxed">
        If your browser asks for a user name and password, enter your domain account as <span className="font-mono">DOMAIN\name</span>.
      </p>
      <DemoLink />
    </Frame>
  );
}

/** Shown instead of the app when sign-in failed or the person has no Data Scope role. Never shows real or demo data. */
export function SignInProblem({ backend }: { backend: Backend }) {
  if (backend.mode === 'live') {
    return (
      <Frame>
        <h2 className="font-display text-[22px] font-[650] text-ink [text-wrap:balance]">You don't have access to Data Scope yet</h2>
        <p className="mt-3 text-[14px] text-ink-2 leading-relaxed">
          You're signed in as <b className="text-ink">{backend.me.name}</b>, but your account isn't in any of the groups Data Scope uses, and you
          don't own any data with a review waiting.
        </p>
        <p className="mt-3 text-[14px] text-ink-2 leading-relaxed">
          Ask a Data Scope administrator to add you to the right group. Then sign out of Windows and back in so the change takes effect.
        </p>
        <button type="button" onClick={signOut} className="mt-6 text-[14px] font-medium text-accent hover:underline">Back to sign in</button>
        <DemoLink />
      </Frame>
    );
  }
  const unavailable = backend.mode === 'signin-failed' && backend.unavailable;
  return (
    <Frame>
      <h2 className="font-display text-[22px] font-[650] text-ink [text-wrap:balance]">
        {unavailable ? "Data Scope isn't responding" : "Windows sign-in didn't work"}
      </h2>
      {unavailable ? (
        <p className="mt-3 text-[14px] text-ink-2 leading-relaxed">
          The server answered, but couldn't finish signing you in. Try again in a minute. If it keeps happening, ask your administrator to check that
          the Data Scope services and the database are running.
        </p>
      ) : (
        <>
          <p className="mt-3 text-[14px] text-ink-2 leading-relaxed">The server didn't receive your Windows sign-in. Usually one of these fixes it:</p>
          <ul className="mt-3 space-y-2 text-[14px] text-ink-2 leading-relaxed list-disc pl-5">
            <li>Use a computer joined to the domain, signed in with your domain account.</li>
            <li>
              Ask your administrator to add this site to the browser's <b className="text-ink">Local intranet</b> sites, so it signs you in without asking.
            </li>
            <li>If the browser asks for a password, enter your domain account as <span className="font-mono">DOMAIN\name</span>.</li>
          </ul>
        </>
      )}
      <div className="mt-6">
        <PrimaryButton onClick={() => void signIn()}>Try again</PrimaryButton>
      </div>
      <DemoLink />
    </Frame>
  );
}

/** While the page finds out where it is running. Shows nothing that could be mistaken for data. */
export function Starting() {
  return (
    <div className="min-h-full grid place-items-center bg-bg" aria-busy="true">
      <div className="flex items-center gap-2.5 text-ink-3 text-[13.5px]">
        <Loader2 size={16} className="animate-spin" /> Opening Data Scope…
      </div>
    </div>
  );
}
