import { isRedirect } from '@sveltejs/kit';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeEvent } from '$lib/server/fake';
import * as apiModule from '$lib/server/api';
import { actions } from './+page.server';

vi.mock('$lib/server/api', () => ({ api: vi.fn(), setSession: vi.fn(), setPending: vi.fn() }));
const api = vi.mocked(apiModule.api);
const setSession = vi.mocked(apiModule.setSession);
const setPending = vi.mocked(apiModule.setPending);

beforeEach(() => vi.clearAllMocks());

const signIn = (next: string, form = { email: 'a@example.org', password: 'secret password' }) => {
	const { event } = fakeEvent({ url: `/sign-in?next=${encodeURIComponent(next)}`, form });
	return Promise.resolve()
		.then(() => actions.default(event as never))
		.catch((e: unknown) => e);
};

describe('sign-in action', () => {
	it('signs in without a second factor', async () => {
		api.mockResolvedValue({ ok: true, status: 200, data: { token: 'A', secondFactorRequired: false } });
		const r = (await signIn('/calendars')) as { status: number; location: string };
		expect(isRedirect(r)).toBe(true);
		expect(r.location).toBe('/calendars');
		expect(setSession).toHaveBeenCalledWith(expect.anything(), 'A');
		expect(setPending).not.toHaveBeenCalled();
	});

	it('keeps the awaiting-code token out of itk_session', async () => {
		api.mockResolvedValue({ ok: true, status: 200, data: { token: 'B', secondFactorRequired: true } });
		const r = (await signIn('/calendars')) as { location: string };
		expect(r.location).toBe('/sign-in/code?next=%2Fcalendars');
		expect(setPending).toHaveBeenCalledWith(expect.anything(), 'B');
		expect(setSession).not.toHaveBeenCalled();
	});

	it('never sends the user off the app', async () => {
		api.mockResolvedValue({ ok: true, status: 200, data: { token: 'A', secondFactorRequired: false } });
		expect(((await signIn('//evil.example')) as { location: string }).location).toBe('/');
	});

	it('refuses without echoing the password', async () => {
		api.mockResolvedValue({ ok: false, status: 401, reason: 'invalid_credentials', extra: {} });
		const r = (await signIn('/')) as { status: number; data: { values: Record<string, string> } };
		expect(r.status).toBe(401);
		expect(r.data.values).toEqual({ email: 'a@example.org' });
		expect(JSON.stringify(r)).not.toContain('secret password');
	});
});
