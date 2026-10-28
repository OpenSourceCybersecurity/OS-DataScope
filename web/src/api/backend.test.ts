import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, type Me } from './client';
import { currentBackend, exploreDemo, resetBackendForTests, signIn, signOut, startBackendCheck } from './backend';

const me: Me = { name: 'NW\\maria', sid: 'S-1-5-21-1-2-3-1101', roles: ['PlatformAdmin'], version: '1.0', openReviews: 0, owns: 0 };
const settle = () => new Promise((r) => setTimeout(r, 0));

function memoryStorage() {
  const data = new Map<string, string>();
  return {
    getItem: (k: string) => data.get(k) ?? null,
    setItem: (k: string, v: string) => void data.set(k, v),
    removeItem: (k: string) => void data.delete(k),
    data,
  };
}

let storage: ReturnType<typeof memoryStorage>;
beforeEach(() => {
  resetBackendForTests();
  storage = memoryStorage();
  vi.stubGlobal('localStorage', storage);
});
afterEach(() => vi.unstubAllGlobals());

describe('sign-in flow', () => {
  it('shows the sign-in page on a server without starting Windows sign-in', async () => {
    const meProbe = vi.fn(async () => me);
    startBackendCheck({ info: async () => ({ product: 'Data Scope' }), me: meProbe });
    await settle();
    expect(currentBackend()).toEqual({ mode: 'signed-out' });
    expect(meProbe).not.toHaveBeenCalled();
  });

  it('runs as the standalone demo when there is no server', async () => {
    startBackendCheck({ info: async () => Promise.reject(new TypeError('Failed to fetch')) });
    await settle();
    expect(currentBackend()).toEqual({ mode: 'demo' });
  });

  it('signs in when asked, and straight away on the next visit', async () => {
    startBackendCheck({ info: async () => ({}), me: async () => me });
    await settle();
    await signIn();
    expect(currentBackend()).toEqual({ mode: 'live', me });
    expect(storage.data.get('datascope.signIn')).toBe('auto');

    resetBackendForTests();
    const meProbe = vi.fn(async () => me);
    startBackendCheck({ info: async () => ({}), me: meProbe });
    await settle();
    expect(meProbe).toHaveBeenCalledOnce();
    expect(currentBackend()).toEqual({ mode: 'live', me });
  });

  it('never falls back to demo data when sign-in is refused or the server fails', async () => {
    startBackendCheck({ info: async () => ({}), me: async () => Promise.reject(new ApiError(401, 'Unauthorized')) });
    await settle();
    await signIn();
    expect(currentBackend()).toEqual({ mode: 'signin-failed', unavailable: false });
    expect(storage.data.has('datascope.signIn')).toBe(false);

    resetBackendForTests();
    startBackendCheck({ info: async () => ({}), me: async () => Promise.reject(new ApiError(500, 'Request failed (500).')) });
    await settle();
    await signIn();
    expect(currentBackend()).toEqual({ mode: 'signin-failed', unavailable: true });
  });

  it('opens the demo only when chosen, and signing out forgets automatic sign-in', async () => {
    startBackendCheck({ info: async () => ({}), me: async () => me });
    await settle();
    exploreDemo();
    expect(currentBackend()).toEqual({ mode: 'demo', chosen: true });

    await signIn();
    signOut();
    expect(currentBackend()).toEqual({ mode: 'signed-out' });
    expect(storage.data.has('datascope.signIn')).toBe(false);
  });

  it('still works when the browser blocks storage', async () => {
    vi.stubGlobal('localStorage', { getItem: () => { throw new Error('blocked'); }, setItem: () => { throw new Error('blocked'); }, removeItem: () => { throw new Error('blocked'); } });
    startBackendCheck({ info: async () => ({}), me: async () => me });
    await settle();
    expect(currentBackend()).toEqual({ mode: 'signed-out' });
    await signIn();
    expect(currentBackend()).toEqual({ mode: 'live', me });
  });
});
