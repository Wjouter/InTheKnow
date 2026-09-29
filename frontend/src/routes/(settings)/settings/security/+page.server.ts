import { redirect } from '@sveltejs/kit';
import { api, clearSession, must, signedIn } from '$lib/server/api';
import { field, raw, reauthenticate, refuse } from '$lib/server/actions';
import { formatter } from '$lib/server/format';

type Session = { id: number; device: string; createdAt: string; lastUsedAt: string; current: boolean };
type Method = { id: number; type: string; createdAt: string; lastUsedAt: string | null };

const id = (v: string) => (/^[1-9]\d{0,17}$/.test(v) ? v : null);

export const load = async (event) => {
	const { lang, tz } = event.locals;
	const me = signedIn(event);
	const [sessions, methods] = await Promise.all([
		api<{ items: Session[] }>(event, 'GET', '/api/me/sessions'),
		api<{ items: Method[] }>(event, 'GET', '/api/me/methods')
	]);
	const fmt = formatter(lang, tz);
	return {
		hasPassword: me.hasPassword,
		twoFactor: me.twoFactor,
		recoveryCodesLeft: me.recoveryCodesLeft,
		sessions: must(event, sessions).items.map((s) => ({ id: s.id, device: s.device, lastUsed: fmt.dateTime(s.lastUsedAt), current: s.current })),
		methods: must(event, methods).items.map((m) => ({
			id: m.id,
			type: m.type,
			added: fmt.date(m.createdAt),
			lastUsed: m.lastUsedAt ? fmt.dateTime(m.lastUsedAt) : null
		}))
	};
};

export const actions = {
	setPassword: async (event) => {
		const r = await api(event, 'PUT', '/api/me/password', { password: raw(await event.request.formData(), 'password') });
		if (!r.ok) return refuse(event, r, { form: 'password' });
		return { done: 'password' } as const;
	},
	removeMethod: async (event) => {
		const methodId = id(field(await event.request.formData(), 'methodId'));
		const r = await api(event, 'DELETE', `/api/me/methods/${methodId}`);
		if (!r.ok) return refuse(event, r, { form: 'methods' });
		return { done: 'methodRemoved' } as const;
	},
	endSession: async (event) => {
		const sessionId = id(field(await event.request.formData(), 'sessionId'));
		const r = await api(event, 'DELETE', `/api/me/sessions/${sessionId}`);
		if (!r.ok) return refuse(event, r, { form: 'sessions' });
		return { done: 'sessionEnded' } as const;
	},
	signOutEverywhere: async (event) => {
		const r = await api(event, 'POST', '/api/auth/sign-out-everywhere');
		if (!r.ok) return refuse(event, r, { form: 'sessions' });
		clearSession(event);
		redirect(303, '/sign-in');
	},
	newRecoveryCodes: async (event) => {
		const r = await api<{ recoveryCodes: string[] }>(event, 'POST', '/api/me/2fa/recovery-codes', {
			code: field(await event.request.formData(), 'code')
		});
		if (!r.ok) return refuse(event, r, { form: 'recovery' });
		// Shown once, from this action result only (FR-034).
		return { recoveryCodes: r.data.recoveryCodes };
	},
	disable2fa: async (event) => {
		const r = await api(event, 'DELETE', '/api/me/2fa');
		if (!r.ok) return refuse(event, r, { form: 'twoFactor' });
		return { done: 'twoFactorOff' } as const;
	},
	reauthenticate
};
