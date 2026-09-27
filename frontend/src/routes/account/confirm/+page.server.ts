import { api } from '$lib/server/api';
import { raw, refuse } from '$lib/server/actions';

// Opening the link changes nothing: only the button does (SC-004).
export const load = ({ url }) => ({ hasToken: Boolean(url.searchParams.get('token')) });

export const actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const token = event.url.searchParams.get('token') ?? '';
		const r = await api(event, 'POST', '/api/auth/confirm', { token, password: raw(data, 'password') });
		if (!r.ok) return refuse(event, r);
		return { confirmed: true } as const;
	}
};
