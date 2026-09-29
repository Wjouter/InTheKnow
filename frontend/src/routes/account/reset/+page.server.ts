import { api, clearSession } from '$lib/server/api';
import { raw, refuse } from '$lib/server/actions';

export const load = ({ url }) => ({ hasToken: Boolean(url.searchParams.get('token')) });

export const actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const token = event.url.searchParams.get('token') ?? '';
		const r = await api(event, 'POST', '/api/auth/password-reset/complete', { token, password: raw(data, 'password') });
		if (!r.ok) return refuse(event, r);
		// Every session of the account ended, this browser's included (US5-2).
		clearSession(event);
		return { reset: true } as const;
	}
};
