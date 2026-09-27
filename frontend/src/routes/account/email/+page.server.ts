import { api } from '$lib/server/api';
import { refuse } from '$lib/server/actions';

export const load = ({ url }) => ({ hasToken: Boolean(url.searchParams.get('token')) });

export const actions = {
	default: async (event) => {
		const token = event.url.searchParams.get('token') ?? '';
		const r = await api(event, 'POST', '/api/auth/email-change/confirm', { token });
		if (!r.ok) return refuse(event, r);
		return { changed: true } as const;
	}
};
