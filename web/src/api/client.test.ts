import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api } from './client';
import { CONTENT_STATE_LABEL, GAP_LABEL, backendFromProbeError, canManageReviews, canReadPosture, canReadReviews, canReviewClassification, canTriageRisks, hasNoRole } from './backend';

const respond = (status: number, body: unknown) =>
  vi.fn(async () => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }));

afterEach(() => vi.unstubAllGlobals());

describe('api client', () => {
  it('sends JSON to the versioned API with same-origin credentials', async () => {
    const fetchMock = respond(201, { id: 's1', hostname: 'nw-fs01' });
    vi.stubGlobal('fetch', fetchMock);
    const server = await api.enroll('nw-fs01');
    expect(server.hostname).toBe('nw-fs01');
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/v1/servers');
    expect(init.method).toBe('POST');
    expect(init.credentials).toBe('same-origin');
    expect(JSON.parse(init.body as string)).toEqual({ hostname: 'nw-fs01' });
    expect(init.headers).toMatchObject({ 'X-DataScope-Client': 'web', 'Content-Type': 'application/json' });
  });

  it('asks for people or groups only when the Who has access list is filtered', async () => {
    const fetchMock = respond(200, { items: [], limit: 50, offset: 0, total: 0 });
    vi.stubGlobal('fetch', fetchMock);
    await api.principals('fin', 'Group');
    await api.principals('', 'User');
    const urls = fetchMock.mock.calls.map((c) => (c as unknown as [string])[0]);
    expect(urls[0]).toBe('/api/v1/directory/principals?search=fin&kind=Group&limit=50&offset=0');
    expect(urls[1]).toBe('/api/v1/directory/principals?kind=User&limit=50&offset=0');
  });

  it('asks for stale and duplicate data with the threshold only when one is picked', async () => {
    const fetchMock = respond(200, { items: [], limit: 50, offset: 0, total: 0 });
    vi.stubGlobal('fetch', fetchMock);
    await api.cleanup({});
    await api.cleanup({ shareId: 'sh1' }, 5);
    await api.staleFiles({ shareId: 'sh1', folderId: 'f1' }, 3, 'oldest', 50);
    await api.duplicates({ serverId: 's1' }, 100);
    const urls = fetchMock.mock.calls.map((c) => (c as unknown as [string])[0]);
    expect(urls).toEqual([
      '/api/v1/cleanup',
      '/api/v1/cleanup?shareId=sh1&years=5',
      '/api/v1/cleanup/stale-files?shareId=sh1&folderId=f1&years=3&sort=oldest&offset=50&limit=50',
      '/api/v1/cleanup/duplicates?serverId=s1&offset=100&limit=50',
    ]);
  });

  it('sends the anti-forgery header on actions without a body', async () => {
    const fetchMock = respond(200, { id: 'j1', state: 'Paused' });
    vi.stubGlobal('fetch', fetchMock);
    await api.controlScan('j1', 'pause');
    const [, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(init.method).toBe('POST');
    expect(init.headers).toMatchObject({ 'X-DataScope-Client': 'web' });
  });

  it('removes a server with DELETE and the anti-forgery header, and can list removed servers', async () => {
    const fetchMock = respond(200, { scansCancelled: 1, shares: 2, folders: 30, files: 400, activityStopped: true });
    vi.stubGlobal('fetch', fetchMock);
    const removal = await api.removeServer('s 1');
    expect(removal.files).toBe(400);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/v1/servers/s%201');
    expect(init.method).toBe('DELETE');
    expect(init.headers).toMatchObject({ 'X-DataScope-Client': 'web' });

    await api.servers(true);
    expect((fetchMock.mock.calls[1] as unknown as [string])[0]).toBe('/api/v1/servers?includeRemoved=true&limit=200');
    await api.servers();
    expect((fetchMock.mock.calls[2] as unknown as [string])[0]).toBe('/api/v1/servers?limit=200');
  });

  it('asks for a risk view only when it is not the default, and triages with the anti-forgery header', async () => {
    const fetchMock = respond(201, { id: 3, kind: 'Snoozed', note: null, until: '2026-10-17', severity: 'High', decidedByName: 'GOV\\rita', decidedAt: '2026-10-10T00:00:00Z', superseded: false });
    vi.stubGlobal('fetch', fetchMock);
    await api.risks({ view: 'open' });
    await api.risks({ view: 'accepted', severity: 'High' });
    const urls = fetchMock.mock.calls.map((c) => (c as unknown as [string])[0]);
    expect(urls[0]).toBe('/api/v1/risks');
    expect(urls[1]).toBe('/api/v1/risks?view=accepted&severity=High');

    await api.decideRisk('secrets:f1', 'Snoozed', '2026-10-17', '');
    const [url, init] = fetchMock.mock.calls[2] as unknown as [string, RequestInit];
    expect([url, init.method]).toEqual(['/api/v1/risks/decisions', 'POST']);
    expect(JSON.parse(init.body as string)).toEqual({ riskId: 'secrets:f1', kind: 'Snoozed', until: '2026-10-17', note: null });
    expect(init.headers).toMatchObject({ 'X-DataScope-Client': 'web' });

    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 204 })));
    await expect(api.reopenRisk(3)).resolves.toBeUndefined();
  });

  it('saves a scan schedule with PUT and the anti-forgery header', async () => {
    const fetchMock = respond(200, { id: 's 1', hostname: 'fs01' });
    vi.stubGlobal('fetch', fetchMock);
    await api.setSchedule('s 1', { frequency: 'Weekly', startHour: 22, day: 'Saturday', stopHour: 6 });
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect([url, init.method]).toEqual(['/api/v1/servers/s%201/schedule', 'PUT']);
    expect(JSON.parse(init.body as string)).toEqual({ frequency: 'Weekly', startHour: 22, day: 'Saturday', stopHour: 6 });
    expect(init.headers).toMatchObject({ 'X-DataScope-Client': 'web' });
  });

  it('manages the no-decision list with the anti-forgery header', async () => {
    const fetchMock = respond(201, { id: 7, sid: 'S-1-5-21-1-2-3-512', name: 'Domain Admins', kind: 'Group', reason: 'IT', addedByName: 'NW\\admin', addedAt: '2026-10-10T00:00:00Z' });
    vi.stubGlobal('fetch', fetchMock);
    await api.addReviewExemption('S-1-5-21-1-2-3-512', 'IT admins');
    let [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/v1/review-exemptions');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({ sid: 'S-1-5-21-1-2-3-512', reason: 'IT admins' });

    vi.stubGlobal('fetch', (fetchMock.mockImplementation(async () => new Response(null, { status: 204 })), fetchMock));
    await api.removeReviewExemption(7);
    [url, init] = fetchMock.mock.calls[1] as unknown as [string, RequestInit];
    expect(url).toBe('/api/v1/review-exemptions/7');
    expect(init.method).toBe('DELETE');
    expect(init.headers).toMatchObject({ 'X-DataScope-Client': 'web' });
  });

  it('surfaces the server validation message instead of a status code', async () => {
    vi.stubGlobal('fetch', respond(400, { title: 'One or more validation errors occurred.', errors: { hostname: ['IP addresses are not accepted; use the host name.'] } }));
    await expect(api.enroll('10.0.0.5')).rejects.toThrow('IP addresses are not accepted; use the host name.');
  });

  it('uses the problem title for conflicts', async () => {
    vi.stubGlobal('fetch', respond(409, { title: 'nw-fs01 already has a scan in progress.' }));
    const err = await api.startScan('s1').catch((e: unknown) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).status).toBe(409);
    expect((err as ApiError).message).toBe('nw-fs01 already has a scan in progress.');
  });

  it('explains a forbidden action in plain words', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 403 })));
    await expect(api.setIncluded('x', false)).rejects.toThrow('Your role does not allow this.');
  });

  it('encodes query parameters', async () => {
    const fetchMock = respond(200, { items: [], limit: 200, offset: 0, total: 0 });
    vi.stubGlobal('fetch', fetchMock);
    await api.folders('share 1', 'parent/2');
    expect((fetchMock.mock.calls[0] as unknown as [string])[0]).toBe('/api/v1/shares/share 1/folders?parentId=parent%2F2&limit=200&offset=0');
  });
});

describe('classification calls', () => {
  it('sends finding filters as query parameters, encoding category names', async () => {
    const fetchMock = respond(200, { items: [], limit: 100, offset: 0, total: 0 });
    vi.stubGlobal('fetch', fetchMock);
    await api.classifiedFiles({ category: 'US SSN', minSensitivity: 'Confidential', shareId: undefined }, 100);
    expect((fetchMock.mock.calls[0] as unknown as [string])[0]).toBe('/api/v1/classification/files?category=US%20SSN&minSensitivity=Confidential&limit=100&offset=100');
  });

  it('posts an override with its reason, and null to remove it', async () => {
    const fetchMock = respond(200, {});
    vi.stubGlobal('fetch', fetchMock);
    await api.overrideClassification('f1', null, 'Wrong file.');
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/v1/files/f1/classification-override');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body as string)).toEqual({ sensitivity: null, reason: 'Wrong file.' });
    expect(init.headers).toMatchObject({ 'X-DataScope-Client': 'web' });
  });

  it('labels every content state the server can report', () => {
    const states = ['NotRead', 'Scanned', 'Partial', 'Unsupported', 'TooLarge', 'Offline', 'AccessDenied', 'NotFound', 'Encrypted', 'Unreadable', 'TimedOut', 'IoError', 'Excluded'];
    expect(Object.keys(CONTENT_STATE_LABEL).sort()).toEqual([...states].sort());
  });

  it('shows review controls only to admins and analysts', () => {
    const live = (roles: string[]) => ({ mode: 'live' as const, me: { name: 'n', sid: 's', roles, version: '1', openReviews: 0, owns: 0 } });
    expect(canReviewClassification(live(['SecurityAnalyst']))).toBe(true);
    expect(canReviewClassification(live(['PlatformAdmin']))).toBe(true);
    expect(canReviewClassification(live(['Auditor', 'Viewer']))).toBe(false);
    expect(canReviewClassification({ mode: 'demo' })).toBe(false);
  });
});

describe('gap labels', () => {
  it('covers every gap kind the server can report', () => {
    const kinds = ['ShareUnavailable', 'AccessDenied', 'NotFound', 'PathTooLong', 'ReparsePointSkipped', 'DepthLimit', 'FolderEntryLimit', 'JobEntryLimit', 'UnrepresentableName', 'IoError', 'Excluded'];
    for (const k of kinds) expect(GAP_LABEL[k]?.fix).toBeTruthy();
  });
});

describe('backend detection', () => {
  it('treats a sign-in failure on a real server as such, never as demo data', () => {
    expect(backendFromProbeError(new ApiError(401, 'Unauthorized'))).toEqual({ mode: 'signin-failed' });
    expect(backendFromProbeError(new ApiError(404, 'Not found'))).toEqual({ mode: 'demo' });
    expect(backendFromProbeError(new TypeError('Failed to fetch'))).toEqual({ mode: 'demo' });
  });

  it('spots a signed-in user without any Data Scope role', () => {
    const me = { name: 'NW\\pat', sid: 'S-1-5-21-1', version: '0.2.0', openReviews: 0, owns: 0 };
    expect(hasNoRole({ mode: 'live', me: { ...me, roles: [] } })).toBe(true);
    expect(hasNoRole({ mode: 'live', me: { ...me, roles: ['Viewer'] } })).toBe(false);
    expect(hasNoRole({ mode: 'demo' })).toBe(false);
  });

  it('lets data owners in without a role, to their own reviews', () => {
    const me = { name: 'NW\\maria', sid: 'S-1-5-21-2', version: '0.2.0', roles: [] };
    expect(hasNoRole({ mode: 'live', me: { ...me, openReviews: 1, owns: 0 } })).toBe(false);
    expect(hasNoRole({ mode: 'live', me: { ...me, openReviews: 0, owns: 2 } })).toBe(false);
    const owner = { mode: 'live' as const, me: { ...me, openReviews: 1, owns: 1 } };
    expect(canReadPosture(owner)).toBe(false);
    expect(canReadReviews(owner)).toBe(false);
  });

  it('matches the review and risk policies on the server', () => {
    const live = (roles: string[]) => ({ mode: 'live' as const, me: { name: 'n', sid: 's', roles, version: '1', openReviews: 0, owns: 0 } });
    expect(canManageReviews(live(['PlatformAdmin']))).toBe(true);
    expect(canManageReviews(live(['SecurityAnalyst']))).toBe(false);
    expect(canReadReviews(live(['Auditor']))).toBe(true);
    expect(canReadReviews(live(['Viewer']))).toBe(false);
    expect(canTriageRisks(live(['SecurityAnalyst']))).toBe(true);
    expect(canTriageRisks(live(['PlatformAdmin']))).toBe(true);
    expect(canTriageRisks(live(['Auditor', 'Viewer']))).toBe(false);
    expect(canTriageRisks({ mode: 'demo' })).toBe(false);
  });
});

describe('access calls', () => {
  it('asks to explain access for a user and target, encoding the SID', async () => {
    const fetchMock = respond(200, { user: {}, reasons: [], notes: [] });
    vi.stubGlobal('fetch', fetchMock);
    await api.explain('f1', 'S-1-5-21-1-2-3-1001', 'Folder');
    expect((fetchMock.mock.calls[0] as unknown as [string])[0]).toBe('/api/v1/access/folders/f1/explain?sid=S-1-5-21-1-2-3-1001&target=Folder');
  });

  it('filters exposure by status and pages it', async () => {
    const fetchMock = respond(200, { folders: { items: [], limit: 100, offset: 0, total: 0 } });
    vi.stubGlobal('fetch', fetchMock);
    await api.exposure({ status: 'PossiblyOpen' });
    expect((fetchMock.mock.calls[0] as unknown as [string])[0]).toBe('/api/v1/access/exposure?status=PossiblyOpen&limit=100&offset=0');
  });

  it('leaves out an empty principal search', async () => {
    const fetchMock = respond(200, { items: [], limit: 50, offset: 0, total: 0 });
    vi.stubGlobal('fetch', fetchMock);
    await api.principals('', 'User');
    expect((fetchMock.mock.calls[0] as unknown as [string])[0]).toBe('/api/v1/directory/principals?kind=User&limit=50&offset=0');
  });

  it('requests a directory sync as a POST with the anti-forgery header', async () => {
    const fetchMock = respond(202, { id: 's1', state: 'Queued' });
    vi.stubGlobal('fetch', fetchMock);
    await api.syncDirectory();
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/v1/directory/sync');
    expect(init.method).toBe('POST');
    expect(init.headers).toMatchObject({ 'X-DataScope-Client': 'web' });
  });

  it('asks for any domain user’s reach with the reserved token', async () => {
    const fetchMock = respond(200, { places: { items: [] } });
    vi.stubGlobal('fetch', fetchMock);
    await api.reach('any');
    expect((fetchMock.mock.calls[0] as unknown as [string])[0]).toBe('/api/v1/access/principals/any/reach?limit=200&offset=0');
  });
});

describe('live navigation', () => {
  it('shows owners without a role only their reviews, and reviewers the campaign screens', async () => {
    const { liveRoutes } = await import('../App');
    const live = (roles: string[], owner = false) => ({ mode: 'live' as const, me: { name: 'n', sid: 's', roles, version: '1', openReviews: owner ? 1 : 0, owns: 0 } });
    expect([...liveRoutes(live([], true))]).toEqual(['my-reviews']);
    expect(liveRoutes(live(['Viewer'])).has('reviews')).toBe(false);
    expect(liveRoutes(live(['Viewer'])).has('owners')).toBe(true);
    expect(liveRoutes(live(['Auditor'])).has('reviews')).toBe(true);
    expect(liveRoutes(live(['Auditor'])).has('my-reviews')).toBe(false);
    expect(liveRoutes(live(['Viewer'])).has('cleanup')).toBe(true);
    expect(liveRoutes(live([], true)).has('cleanup')).toBe(false);
    // Review exclusions sit under Settings for everyone who can see reviews; only admins can change them.
    expect(liveRoutes(live(['Auditor'])).has('exclusions')).toBe(true);
    expect(liveRoutes(live(['PlatformAdmin'])).has('exclusions')).toBe(true);
    expect(liveRoutes(live(['Viewer'])).has('exclusions')).toBe(false);
    expect(liveRoutes(live([], true)).has('exclusions')).toBe(false);
    expect(liveRoutes({ mode: 'demo' }).size).toBe(0);
  });

  it('lists progress for anyone who reads posture, and System health for admins and auditors', async () => {
    const { liveRoutes } = await import('../App');
    const live = (roles: string[]) => ({ mode: 'live' as const, me: { name: 'n', sid: 's', roles, version: '1', openReviews: 0, owns: 0 } });
    expect(liveRoutes(live(['Viewer'])).has('progress')).toBe(true);
    expect(liveRoutes(live([])).has('progress')).toBe(false);
    expect(liveRoutes(live(['Auditor'])).has('health')).toBe(true);
    expect(liveRoutes(live(['SecurityAnalyst'])).has('health')).toBe(false);
  });
});
