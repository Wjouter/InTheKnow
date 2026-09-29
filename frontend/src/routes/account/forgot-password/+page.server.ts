import { api } from '$lib/server/api';
import { botCheckValue, field, loadBotCheck, refuse } from '$lib/server/actions';

// The same result for every address (FR-016, US5-1, SC-005).
const done = { done: true } as const;

export const load = async (event) => ({ botCheck: await loadBotCheck(event) });

export const actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const email = field(data, 'email');
		const r = await api(event, 'POST', '/api/auth/password-reset', { email, botCheck: botCheckValue(data) });
		if (!r.ok) return refuse(event, r, { email });
		return done;
	}
};
