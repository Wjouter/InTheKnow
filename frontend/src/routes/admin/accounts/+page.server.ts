import type { RequestEvent } from '@sveltejs/kit';
import { api, must } from '$lib/server/api';
import { field } from '$lib/server/actions';
import { after, itemAction } from '$lib/server/admin';
import { formatter } from '$lib/server/format';

type Account = { id: number; email: string; role: 'user' | 'admin'; confirmed: boolean; disabled: boolean; twoFactor: boolean; createdAt: string };

export const load = async (event) => {
	const { lang, tz } = event.locals;
	const q = event.url.searchParams.get('q') ?? '';
	const query = new URLSearchParams({ limit: '50' });
	if (q) query.set('q', q);
	const from = after(event.url);
	if (from) query.set('after', from);
	const page = must(event, await api<{ items: Account[]; next: number | null }>(event, 'GET', `/api/admin/accounts?${query}`));
	const fmt = formatter(lang, tz);
	return { q, next: page.next, accounts: page.items.map((a) => ({ ...a, created: fmt.date(a.createdAt) })) };
};

const path = (id: string) => `/api/admin/accounts/${id}`;

// A 409 is about the account acted on, so it gets a message naming that account. (The admin gate's own
// two_factor_required is a 403 and keeps the general message.)
const aboutTarget = (action: ReturnType<typeof itemAction>) => async (event: RequestEvent) => {
	const result = await action(event);
	const refused: Record<string, string> = event.locals.t.admin.accounts.refused;
	if (!('done' in result) && result.status === 409 && refused[result.data.reason]) result.data.message = refused[result.data.reason];
	return result;
};

export const actions = {
	setRole: aboutTarget(itemAction('accountId', 'PUT', (id) => `${path(id)}/role`, 'role', (data) => ({ role: field(data, 'role') }))),
	disable: aboutTarget(itemAction('accountId', 'POST', (id) => `${path(id)}/disable`, 'disabled')),
	enable: itemAction('accountId', 'POST', (id) => `${path(id)}/enable`, 'enabled'),
	delete: itemAction('accountId', 'DELETE', path, 'deleted'),
	remove2fa: itemAction('accountId', 'DELETE', (id) => `${path(id)}/2fa`, 'twoFactorRemoved')
};
