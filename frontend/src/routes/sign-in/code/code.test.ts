import { isRedirect } from '@sveltejs/kit';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeEvent } from '$lib/server/fake';
import * as apiModule from '$lib/server/api';
import { actions, load } from './+page.server';

vi.mock('$lib/server/api', () => ({ api: vi.fn(), setSession: vi.fn(), clearPending: vi.fn() }));
const api = vi.mocked(apiModule.api);
const setSession = vi.mocked(apiModule.setSession);
const clearPending = vi.mocked(apiModule.clearPending);

beforeEach(() => vi.clearAllMocks());

const attempt = <T>(fn: () => T) =>
	Promise.resolve()
		.then(fn)
		.catch((e: unknown) => e);

describe('code step', () => {
	it('sends a browser without itk_pending back to sign-in', async () => {
		const { event } = fakeEvent({ url: '/sign-in/code?next=%2Fchanges' });
		const r = (await attempt(() => load(event as never))) as { location: string };
		expect(isRedirect(r)).toBe(true);
		expect(r.location).toBe('/sign-in?next=%2Fchanges&expired=code');
	});

	it('promotes the same token after a correct code', async () => {
		api.mockResolvedValue({ ok: true, status: 200, data: { secondFactorRequired: false } });
		const { event } = fakeEvent({ url: '/sign-in/code?next=%2Fchanges', cookies: { itk_pending: 'P' }, form: { code: '123456' } });
		const r = (await attempt(() => actions.default(event as never))) as { location: string };
		expect(api.mock.calls[0][4]).toMatchObject({ token: 'P' });
		expect(setSession).toHaveBeenCalledWith(expect.anything(), 'P');
		expect(clearPending).toHaveBeenCalled();
		expect(setSession.mock.invocationCallOrder[0]).toBeLessThan(clearPending.mock.invocationCallOrder[0]);
		expect(r.location).toBe('/changes');
	});

	it('starts again when the awaiting-code session ended', async () => {
		api.mockResolvedValue({ ok: false, status: 401, reason: 'not_signed_in', extra: {} });
		const { event } = fakeEvent({ url: '/sign-in/code?next=%2Fchanges', cookies: { itk_pending: 'P' }, form: { code: '1' } });
		const r = (await attempt(() => actions.default(event as never))) as { location: string };
		expect(clearPending).toHaveBeenCalled();
		expect(setSession).not.toHaveBeenCalled();
		expect(r.location).toBe('/sign-in?next=%2Fchanges&expired=code');
	});

	it('refuses a wrong code without echoing it, and keeps itk_pending', async () => {
		api.mockResolvedValue({ ok: false, status: 400, reason: 'code_invalid', extra: {} });
		const { event, jar } = fakeEvent({ url: '/sign-in/code', cookies: { itk_pending: 'P' }, form: { code: '999999' } });
		const r = (await attempt(() => actions.default(event as never))) as { status: number; data: object };
		expect(r.status).toBe(400);
		expect(JSON.stringify(r.data)).not.toContain('999999');
		expect(clearPending).not.toHaveBeenCalled();
		expect(jar.get('itk_pending')).toBe('P');
	});
});
