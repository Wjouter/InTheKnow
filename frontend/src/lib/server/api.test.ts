import { isRedirect } from '@sveltejs/kit';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, setPending, setSession } from './api';
import { fakeEvent, jsonResponse } from './fake';

const fetchMock = vi.fn();
beforeEach(() => {
	vi.stubGlobal('fetch', fetchMock);
	fetchMock.mockReset();
});
afterEach(() => vi.restoreAllMocks());

describe('api', () => {
	it('sends the session, one client address and the browser’s User-Agent', async () => {
		fetchMock.mockResolvedValue(jsonResponse(200, { ok: 1 }));
		const { event } = fakeEvent({ cookies: { itk_session: 'TOKEN' }, headers: { 'user-agent': 'UA-X' } });
		const r = await api(event, 'GET', '/api/me');
		expect(r).toEqual({ ok: true, status: 200, data: { ok: 1 } });
		const headers = fetchMock.mock.calls[0][1].headers;
		expect(headers.Authorization).toBe('Bearer TOKEN');
		expect(headers['X-Forwarded-For']).toBe('203.0.113.9');
		expect(headers['User-Agent']).toBe('UA-X');
	});

	it('deletes the session on 401 not_signed_in and redirects to sign-in with next', async () => {
		fetchMock.mockResolvedValue(jsonResponse(401, { status: 401, reason: 'not_signed_in' }));
		const { event, deleted } = fakeEvent({ url: '/changes?showHidden=1', cookies: { itk_session: 'T' } });
		const thrown = await api(event, 'GET', '/api/me/changes').catch((e) => e);
		expect(isRedirect(thrown)).toBe(true);
		expect(thrown.status).toBe(303);
		expect(thrown.location).toBe('/sign-in?next=%2Fchanges%3FshowHidden%3D1');
		expect(deleted.itk_session).toMatchObject({ path: '/' });
	});

	it('returns the failure with on401: return', async () => {
		fetchMock.mockResolvedValue(jsonResponse(401, { status: 401, reason: 'not_signed_in' }));
		const { event, deleted } = fakeEvent({ cookies: { itk_session: 'T' } });
		const r = await api(event, 'GET', '/api/me', undefined, { on401: 'return' });
		expect(r).toMatchObject({ ok: false, status: 401, reason: 'not_signed_in' });
		expect(deleted.itk_session).toBeDefined();
	});

	it('drops action names from next', async () => {
		fetchMock.mockResolvedValue(jsonResponse(401, { status: 401, reason: 'not_signed_in' }));
		const { event } = fakeEvent({ url: '/calendars/1?/remove', method: 'POST', cookies: { itk_session: 'T' } });
		const thrown = await api(event, 'DELETE', '/api/me/calendars/1').catch((e) => e);
		expect(thrown.location).toBe('/sign-in?next=%2Fcalendars%2F1');
	});

	it('reads Retry-After', async () => {
		fetchMock.mockResolvedValue(jsonResponse(429, { reason: 'too_many_attempts' }, { 'retry-after': '120' }));
		const { event } = fakeEvent();
		expect(await api(event, 'POST', '/api/auth/sign-in', {})).toMatchObject({ retryAfter: 120, reason: 'too_many_attempts' });
	});

	it('maps a failed fetch and a bare 502 to unavailable without details', async () => {
		const { event } = fakeEvent();
		fetchMock.mockRejectedValue(new DOMException('timed out', 'TimeoutError'));
		expect(await api(event, 'GET', '/api/me')).toEqual({ ok: false, status: 503, reason: 'unavailable', extra: {} });
		fetchMock.mockResolvedValue(new Response('<html>Bad gateway</html>', { status: 502 }));
		expect(await api(event, 'GET', '/api/me')).toEqual({ ok: false, status: 503, reason: 'unavailable', extra: {} });
	});

	it('keeps other Problem members in extra', async () => {
		fetchMock.mockResolvedValue(jsonResponse(400, { status: 400, title: 'x', reason: 'invalid_condition', rule: 2 }));
		const { event } = fakeEvent();
		expect(await api(event, 'PUT', '/api/me/presets/1', {})).toMatchObject({ extra: { rule: 2 } });
	});

	it('logs only the method, the path template, the status and the reason', async () => {
		const log = vi.spyOn(console, 'log').mockImplementation(() => {});
		const err = vi.spyOn(console, 'error').mockImplementation(() => {});
		fetchMock.mockResolvedValue(jsonResponse(404, { status: 404, reason: 'not_found' }));
		const { event } = fakeEvent();
		await api(event, 'GET', '/api/admin/accounts/7?q=alice@example.org');
		const lines = [...log.mock.calls, ...err.mock.calls].map((c) => c.join(' '));
		expect(lines).toHaveLength(1);
		expect(lines[0]).toContain('/api/admin/accounts/{id}');
		for (const secret of ['?', 'alice', 'example.org']) expect(lines[0]).not.toContain(secret);
	});
});

describe('cookies', () => {
	it('sets the session and pending cookies with the data-model attributes', () => {
		const { event, set } = fakeEvent();
		setSession(event, 'A');
		setPending(event, 'B');
		expect(set.itk_session).toEqual({
			value: 'A',
			opts: { httpOnly: true, secure: true, sameSite: 'lax', path: '/', maxAge: 34560000 }
		});
		expect(set.itk_pending).toEqual({
			value: 'B',
			opts: { httpOnly: true, secure: true, sameSite: 'lax', path: '/sign-in', maxAge: 600 }
		});
	});
});
