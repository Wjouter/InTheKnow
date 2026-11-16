import { isRedirect } from '@sveltejs/kit';
import { describe, expect, it } from 'vitest';
import { fakeEvent } from '$lib/server/fake';
import { GET, POST } from './+server';

// contracts/theme.md § POST /theme
const post = async (form: Record<string, string>) => {
	const f = fakeEvent({ url: '/theme', form });
	const thrown = await Promise.resolve(POST(f.event as never)).catch((e: unknown) => e);
	return { ...f, location: isRedirect(thrown) && thrown.status === 303 ? thrown.location : null };
};

describe('POST /theme', () => {
	it.each(['light', 'dark'])('stores %s for a year, readable by script', async (theme) => {
		const { set, location } = await post({ theme, next: '/sign-in' });
		expect(set.itk_theme).toEqual({
			value: theme,
			opts: { httpOnly: false, secure: true, sameSite: 'lax', path: '/', maxAge: 31_536_000 }
		});
		expect(location).toBe('/sign-in');
	});

	it('deletes the cookie for "same as this device"', async () => {
		const { set, deleted } = await post({ theme: 'device', next: '/' });
		expect(deleted.itk_theme).toEqual({ path: '/' });
		expect(set.itk_theme).toBeUndefined();
	});

	it.each<Record<string, string>>([{ theme: 'purple' }, {}])('changes nothing for %o', async (form) => {
		const { set, deleted, location } = await post({ ...form, next: '/about' });
		expect(set).toEqual({});
		expect(deleted).toEqual({});
		expect(location).toBe('/about');
	});

	it.each(['https://evil.example', '//evil.example', ''])('sends next=%s to /', async (next) => {
		expect((await post({ theme: 'dark', next })).location).toBe('/');
	});
});

describe('GET /theme', () => {
	it('redirects to /', async () => {
		const thrown = await Promise.resolve()
			.then(() => GET())
			.catch((e: unknown) => e);
		expect(isRedirect(thrown) && thrown.location).toBe('/');
	});
});
