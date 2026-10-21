import { beforeEach, describe, expect, it, vi } from 'vitest';
import { fakeEvent } from '$lib/server/fake';
import * as apiModule from '$lib/server/api';
import { load } from './+page.server';

vi.mock('$lib/server/api', async (orig) => ({ ...(await orig<typeof apiModule>()), api: vi.fn() }));
const api = vi.mocked(apiModule.api);

beforeEach(() => vi.clearAllMocks());

const run = (o: Parameters<typeof fakeEvent>[0]) => load(fakeEvent(o).event as never);
const attempt = <T>(fn: () => T) =>
	Promise.resolve()
		.then(fn)
		.catch((e: unknown) => e);

describe('reset page load', () => {
	it('shows the dead-link panel without calling the API when there is no token', async () => {
		expect(await run({ url: '/account/reset' })).toEqual({ usable: false });
		expect(api).not.toHaveBeenCalled();
	});

	it('does not re-check after a submit (POST or data request)', async () => {
		expect(await run({ url: '/account/reset?token=T', method: 'POST' })).toEqual({ usable: true });
		expect(await run({ url: '/account/reset?token=T', isDataRequest: true })).toEqual({ usable: true });
		expect(api).not.toHaveBeenCalled();
	});

	it('shows the form when the check says the link is usable', async () => {
		api.mockResolvedValue({ ok: true, status: 204, data: undefined });
		expect(await run({ url: '/account/reset?token=T' })).toEqual({ usable: true });
		expect(api.mock.calls[0].slice(1, 4)).toEqual(['POST', '/api/auth/password-reset/check', { token: 'T' }]);
	});

	it('shows the dead-link panel on token_invalid', async () => {
		api.mockResolvedValue({ ok: false, status: 400, reason: 'token_invalid', extra: {} });
		expect(await run({ url: '/account/reset?token=T' })).toEqual({ usable: false });
	});

	it.each([503, 429])('shows the error page when the check answers %i', async (status) => {
		api.mockResolvedValue({ ok: false, status, reason: status === 503 ? 'unavailable' : 'rate_limited', extra: {} });
		expect(await attempt(() => run({ url: '/account/reset?token=T' }))).toMatchObject({ status });
	});
});
