import { isHttpError, isRedirect, type RequestEvent } from '@sveltejs/kit';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeEvent, jsonResponse } from '$lib/server/fake';
import { load } from './+page.server';

// 010 contracts/pages.md § Addresses: the id and `all` rules, and one "not found" for every cause (FR-016).
const fetchMock = vi.fn();
beforeEach(() => {
	vi.stubGlobal('fetch', fetchMock);
	fetchMock.mockReset();
	vi.spyOn(console, 'error').mockImplementation(() => {});
});
afterEach(() => vi.restoreAllMocks());

const run = (id: string, query = '') => {
	const { event } = fakeEvent({ url: `/changes/${id}${query}`, params: { id }, cookies: { itk_session: 'T' } });
	return (load as unknown as (e: RequestEvent) => Promise<{ openedId: number; all: boolean | null }>)(event);
};
const body = {
	changeId: 7,
	calendarId: 1,
	calendarName: 'Class 3B',
	current: { state: 'gone', event: null, removedAt: null },
	historyStartsAt: null,
	retentionDays: 365,
	items: []
};

describe('/changes/[id] load', () => {
	it('gives the not-found page for an id that is not a positive digit string, without asking the API', async () => {
		for (const bad of ['0', '007', 'abc', '1e3', '-4']) {
			const thrown = await run(bad).catch((e) => e);
			expect(isHttpError(thrown) && thrown.status).toBe(404);
			expect(thrown.body.kind).toBe('notFound');
		}
		expect(fetchMock).not.toHaveBeenCalled();
	});

	it('reads all as 0 or 1 only', async () => {
		for (const [query, all] of [['?all=1', true], ['?all=0', false], ['?all=yes', null], ['', null]] as const) {
			fetchMock.mockResolvedValueOnce(jsonResponse(200, body));
			const data = await run('7', query);
			expect([data.openedId, data.all]).toEqual([7, all]);
		}
		expect(fetchMock.mock.calls[0][0]).toMatch(/\/api\/me\/changes\/7\/history$/);
	});

	it('shows the same not-found page for every API 404', async () => {
		fetchMock.mockResolvedValue(jsonResponse(404, { status: 404, reason: 'not_found' }));
		const thrown = await run('7').catch((e) => e);
		expect(isHttpError(thrown) && thrown.status).toBe(404);
		expect(thrown.body).toEqual({ kind: 'notFound', message: (await run('8').catch((e) => e)).body.message });
	});

	it('sends an expired session to sign-in with this address as next', async () => {
		fetchMock.mockResolvedValue(jsonResponse(401, { status: 401, reason: 'not_signed_in' }));
		const thrown = await run('7', '?all=1').catch((e) => e);
		expect(isRedirect(thrown) && thrown.location).toBe('/sign-in?next=%2Fchanges%2F7%3Fall%3D1');
	});
});
