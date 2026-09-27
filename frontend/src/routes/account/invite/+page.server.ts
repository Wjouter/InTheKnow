import { redirect } from '@sveltejs/kit';
import { api, setSession } from '$lib/server/api';
import { field, raw, refuse } from '$lib/server/actions';

export const load = ({ url }) => ({ hasToken: Boolean(url.searchParams.get('token')) });

export const actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const token = event.url.searchParams.get('token') ?? '';
		const language = field(data, 'language');
		const r = await api<{ token: string }>(event, 'POST', '/api/auth/invite/accept', {
			token,
			password: raw(data, 'password'),
			// Empty means "the invite's language": the API then uses the invite's own (004).
			language: language === 'en' || language === 'nl' ? language : undefined
		});
		if (!r.ok) return refuse(event, r, { language });
		setSession(event, r.data.token);

		// The answer carries no role, so ask for it with the new session.
		const me = await api<{ role: string }>(event, 'GET', '/api/me', undefined, { token: r.data.token, on401: 'return' });
		if (me.ok && me.data.role === 'admin') return { admin: true } as const;
		redirect(303, '/');
	}
};
