import { isRedirect } from '@sveltejs/kit';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeEvent } from '$lib/server/fake';
import * as apiModule from '$lib/server/api';
import { actions, load } from './+page.server';

vi.mock('$lib/server/api', () => ({ api: vi.fn() }));
const api = vi.mocked(apiModule.api);

beforeEach(() => vi.clearAllMocks());

const attempt = <T>(fn: () => T) =>
	Promise.resolve()
		.then(fn)
		.catch((e: unknown) => e);

const form = { email: 'new@example.org', password: 'a long enough password', language: 'nl' };

describe('sign-up', () => {
	it('moves a shareable invite token into a cookie without calling the API', async () => {
		const { event, set } = fakeEvent({ url: '/account/sign-up?invite=T' });
		const r = (await attempt(() => load(event as never))) as { location: string };
		expect(isRedirect(r)).toBe(true);
		expect(r.location).toBe('/account/sign-up');
		expect(set.itk_invite).toEqual({
			value: 'T',
			opts: { httpOnly: true, secure: true, sameSite: 'lax', path: '/account/sign-up', maxAge: 3600 }
		});
		expect(api).not.toHaveBeenCalled();
	});

	it('sends the invite and deletes the cookie after sign-up', async () => {
		api.mockResolvedValue({ ok: true, status: 202, data: null });
		const { event, deleted } = fakeEvent({ url: '/account/sign-up', cookies: { itk_invite: 'T' }, form });
		await actions.default(event as never);
		expect(api.mock.calls[0][3]).toMatchObject({ invite: 'T', email: 'new@example.org', language: 'nl' });
		expect(deleted.itk_invite).toMatchObject({ path: '/account/sign-up' });
	});

	it('keeps the invite cookie after a refusal', async () => {
		api.mockResolvedValue({ ok: false, status: 400, reason: 'password_common', extra: {} });
		const { event, deleted, jar } = fakeEvent({ url: '/account/sign-up', cookies: { itk_invite: 'T' }, form });
		await actions.default(event as never);
		expect(deleted.itk_invite).toBeUndefined();
		expect(jar.get('itk_invite')).toBe('T');
	});

	it('gives the same result for every address', async () => {
		api.mockResolvedValue({ ok: true, status: 202, data: null });
		const a = await actions.default(fakeEvent({ url: '/account/sign-up', form }).event as never);
		const b = await actions.default(fakeEvent({ url: '/account/sign-up', form: { ...form, email: 'taken@example.org' } }).event as never);
		expect(a).toBe(b);
		expect(JSON.stringify(a)).not.toContain('example.org');
	});
});
