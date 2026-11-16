import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeEvent } from '$lib/server/fake';
import * as apiModule from '$lib/server/api';
import { actions } from './+page.server';

vi.mock('$lib/server/api', async (orig) => ({ ...(await orig<typeof apiModule>()), api: vi.fn() }));
const api = vi.mocked(apiModule.api);

beforeEach(() => {
	vi.clearAllMocks();
	api.mockResolvedValue({ ok: true, status: 204, data: undefined });
});

const form = { homePage: 'changes', language: 'en', timeZone: '' };
const save = async (extra: Record<string, string>, cookies: Record<string, string> = {}) => {
	const f = fakeEvent({ url: '/settings/preferences?/save', form: { ...form, ...extra }, cookies });
	await actions.save(f.event as never);
	return f;
};

// The theme on Save, the no-script path (011 FR-020, FR-021, SC-008).
describe('preferences save: theme', () => {
	it('stores dark and renders the page in it', async () => {
		const { set, event } = await save({ theme: 'dark' });
		expect(set.itk_theme.value).toBe('dark');
		expect(event.locals.theme).toBe('dark');
	});

	it('deletes the cookie for "same as this device"', async () => {
		const { deleted, event } = await save({ theme: 'device' }, { itk_theme: 'dark' });
		expect(deleted.itk_theme).toEqual({ path: '/' });
		expect(event.locals.theme).toBeNull();
	});

	it('changes nothing without a theme field', async () => {
		const { set, deleted, event } = await save({});
		expect(set.itk_theme).toBeUndefined();
		expect(deleted.itk_theme).toBeUndefined();
		expect(event.locals.theme).toBeNull();
	});

	it('never sends the theme to the API', async () => {
		await save({ theme: 'dark' });
		expect(api).toHaveBeenCalledTimes(2);
		for (const call of api.mock.calls) expect(JSON.stringify(call[3])).not.toContain('dark');
	});

	it('keeps the theme when the API save fails', async () => {
		api.mockResolvedValue({ ok: false, status: 503, reason: 'unavailable', extra: {} });
		const { set } = await save({ theme: 'light' });
		expect(set.itk_theme.value).toBe('light');
	});
});
