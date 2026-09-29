import { api, must } from '$lib/server/api';
import { field, formValues, refuse } from '$lib/server/actions';
import { itemAction } from '$lib/server/admin';
import { formatter } from '$lib/server/format';

type Settings = { open: boolean; allowedDomains: string[] };
type Invite = {
	id: number;
	kind: 'named' | 'shareable';
	email: string | null;
	language: string;
	grantsAdmin: boolean;
	maxUses: number;
	used: number;
	left: number;
	expiresAt: string;
};

export const load = async (event) => {
	const { lang, tz } = event.locals;
	const [settings, invites] = await Promise.all([
		api<Settings>(event, 'GET', '/api/admin/signup-settings'),
		api<{ items: Invite[] }>(event, 'GET', '/api/admin/invites')
	]);
	const fmt = formatter(lang, tz);
	return {
		settings: must(event, settings),
		invites: must(event, invites).items.map((i) => ({ ...i, expires: fmt.dateTime(i.expiresAt) }))
	};
};

const number = (data: FormData, key: string) => Number(field(data, key)) || undefined;

export const actions = {
	saveSettings: async (event) => {
		const data = await event.request.formData();
		const allowedDomains = field(data, 'allowedDomains')
			.split(/\s+/)
			.filter(Boolean);
		const r = await api(event, 'PUT', '/api/admin/signup-settings', { open: data.has('open'), allowedDomains });
		if (!r.ok) return refuse(event, r, { form: 'settings', allowedDomains: field(data, 'allowedDomains') });
		return { done: 'settings' } as const;
	},
	createInvite: async (event) => {
		const data = await event.request.formData();
		const kind = field(data, 'kind');
		const common = { kind, language: field(data, 'language'), validDays: number(data, 'validDays') };
		const body =
			kind === 'named'
				? { ...common, email: field(data, 'email'), grantsAdmin: data.has('grantsAdmin') }
				: { ...common, maxUses: number(data, 'maxUses') };
		const r = await api<{ link?: string }>(event, 'POST', '/api/admin/invites', body);
		if (!r.ok) return refuse(event, r, { form: 'invite', ...formValues(data, ['kind', 'email', 'language', 'validDays', 'maxUses']) });
		// A shareable link is in this response only (004 contracts/api.md), so it is shown once.
		return { done: 'invite', link: r.data.link ?? null };
	},
	resend: itemAction('inviteId', 'POST', (id) => `/api/admin/invites/${id}/resend`, 'resent'),
	revoke: itemAction('inviteId', 'DELETE', (id) => `/api/admin/invites/${id}`, 'revoked')
};
