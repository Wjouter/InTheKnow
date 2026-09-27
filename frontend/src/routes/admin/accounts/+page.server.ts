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
export const actions = {
	setRole: itemAction('accountId', 'PUT', (id) => `${path(id)}/role`, 'role', (data) => ({ role: field(data, 'role') })),
	disable: itemAction('accountId', 'POST', (id) => `${path(id)}/disable`, 'disabled'),
	enable: itemAction('accountId', 'POST', (id) => `${path(id)}/enable`, 'enabled'),
	delete: itemAction('accountId', 'DELETE', path, 'deleted'),
	remove2fa: itemAction('accountId', 'DELETE', (id) => `${path(id)}/2fa`, 'twoFactorRemoved')
};
