import { isRedirect, type ResolveOptions } from '@sveltejs/kit';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { handle } from './hooks.server';
import { fakeEvent, jsonResponse } from '$lib/server/fake';

const fetchMock = vi.fn();
let logs: string[] = [];
beforeEach(() => {
	vi.stubGlobal('fetch', fetchMock);
	fetchMock.mockReset();
	logs = [];
	vi.spyOn(console, 'log').mockImplementation((...a) => void logs.push(a.join(' ')));
	vi.spyOn(console, 'error').mockImplementation(() => {});
});
afterEach(() => vi.restoreAllMocks());

const resolve = vi.fn(async (_event: unknown, opts?: ResolveOptions) => {
	const html = (await opts?.transformPageChunk?.({ html: '<html lang="%lang%">', done: true })) ?? '';
	return new Response(html, { status: 200 });
});

const me = (language = 'en') => ({
	email: 'a@example.org',
	role: 'user',
	language,
	twoFactor: false,
	recoveryCodesLeft: 0,
	hasPassword: true,
	pendingEmail: null,
	homePage: 'changes'
});
const signedIn = (language = 'en') =>
	fetchMock.mockImplementation(async (url: string) =>
		url.endsWith('/api/me') ? jsonResponse(200, me(language)) : jsonResponse(200, { effectiveTimeZone: 'Europe/Amsterdam' })
	);

const run = (o: Parameters<typeof fakeEvent>[0]) => {
	const f = fakeEvent(o);
	return { ...f, response: handle({ event: f.event, resolve }) };
};

describe('gate', () => {
	it('sends a visitor on a gated path to sign-in', async () => {
		const res = await run({ url: '/changes' }).response;
		expect(res.status).toBe(303);
		expect(res.headers.get('location')).toBe('/sign-in?next=%2Fchanges');
	});

	it('throws Kit’s redirect for data requests and enhanced actions', async () => {
		for (const o of [
			{ url: '/changes', isDataRequest: true },
			{ url: '/calendars?/addLink', method: 'POST', headers: { 'sec-fetch-site': 'same-origin', 'x-sveltekit-action': 'true' } }
		]) {
			const thrown = (await Promise.resolve(run(o).response).catch((e: unknown) => e)) as { location: string };
			expect(isRedirect(thrown)).toBe(true);
			expect(thrown.location).toMatch(/^\/sign-in\?next=/);
		}
	});

	it.each(['/', '/sign-in', '/sign-in/code', '/account/reset', '/language', '/_app/x.js'])('lets %s through', async (url) => {
		expect((await run({ url }).response).status).toBe(200);
	});

	it('clears an expired session on a public page and still renders it', async () => {
		fetchMock.mockResolvedValue(jsonResponse(401, { reason: 'not_signed_in' }));
		const { response, deleted } = run({ url: '/account/reset?token=T', cookies: { itk_session: 'OLD' } });
		expect((await response).status).toBe(200);
		expect(deleted.itk_session).toBeDefined();
	});
});

describe('CSRF', () => {
	const post = (headers: Record<string, string>) => run({ url: '/sign-in', method: 'POST', headers }).response;

	it('refuses cross-site and same-site posts', async () => {
		expect((await post({ 'sec-fetch-site': 'cross-site' })).status).toBe(403);
		expect((await post({ 'sec-fetch-site': 'same-site' })).status).toBe(403);
	});

	it('accepts same-origin posts with Origin: null', async () => {
		expect((await post({ 'sec-fetch-site': 'same-origin', origin: 'null' })).status).toBe(200);
	});

	it('falls back to Origin without Sec-Fetch-Site', async () => {
		expect((await post({ origin: 'http://localhost:5173' })).status).toBe(200);
		expect((await post({ origin: 'https://evil.example' })).status).toBe(403);
	});
});

describe('headers', () => {
	it.each(['/changes', '/changes/__data.json'])('are on %s', async (path) => {
		signedIn();
		const res = await run({ url: path, cookies: { itk_session: 'T' }, isDataRequest: path.endsWith('.json') }).response;
		expect(res.headers.get('x-frame-options')).toBe('DENY');
		expect(res.headers.get('referrer-policy')).toBe('no-referrer');
		expect(res.headers.get('x-content-type-options')).toBe('nosniff');
		expect(res.headers.get('cache-control')).toBe('no-store');
	});
});

describe('log', () => {
	it('writes one line without query, cookies, addresses or the user agent', async () => {
		fetchMock.mockResolvedValue(jsonResponse(401, { reason: 'not_signed_in' }));
		await run({
			url: '/account/reset?token=SECRET',
			cookies: { itk_session: 'TOKEN' },
			headers: { 'x-forwarded-for': '203.0.113.9', 'user-agent': 'UA-X' }
		}).response;
		expect(logs).toHaveLength(1);
		expect(logs[0]).toMatch(/^\S+Z GET \/account\/reset \d{3} \d+ms$/);
		for (const secret of ['SECRET', 'TOKEN', '203.0.113.9', 'UA-X']) expect(logs[0]).not.toContain(secret);
	});
});

describe('language', () => {
	it('follows Accept-Language for visitors', async () => {
		const res = await run({ url: '/', headers: { 'accept-language': 'nl' } }).response;
		expect(await res.text()).toBe('<html lang="nl">');
	});

	it('follows the account for signed-in users', async () => {
		signedIn('en');
		const res = await run({ url: '/', cookies: { itk_session: 'T' }, headers: { 'accept-language': 'nl' } }).response;
		expect(await res.text()).toBe('<html lang="en">');
	});
});
