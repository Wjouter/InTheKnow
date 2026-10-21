import { api, clearSession, must } from '$lib/server/api';
import { raw, refuse } from '$lib/server/actions';

// 009: a dead link says so before a password is typed. After a submit the action's result decides the page, and a re-check
// would find the token just used (or fail on a rate limit) and hide "done"; reset links are always a full page load.
export const load = async (event) => {
	const token = event.url.searchParams.get('token');
	if (!token) return { usable: false };
	if (event.request.method !== 'GET' || event.isDataRequest) return { usable: true };
	const r = await api(event, 'POST', '/api/auth/password-reset/check', { token });
	if (r.ok) return { usable: true };
	if (r.status === 400 && r.reason === 'token_invalid') return { usable: false };
	return must(event, r) as never;
};

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
