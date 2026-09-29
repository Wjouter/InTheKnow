import { redirect } from '@sveltejs/kit';
import { api, clearSession, signedIn } from '$lib/server/api';
import { field, reauthenticate, refuse } from '$lib/server/actions';

export const load = (event) => {
	const me = signedIn(event);
	return { email: me.email, pendingEmail: me.pendingEmail };
};

export const actions = {
	changeEmail: async (event) => {
		const email = field(await event.request.formData(), 'email');
		const r = await api(event, 'POST', '/api/me/email', { email });
		// The same answer for an address another account uses (004 FR-022).
		if (!r.ok) return refuse(event, r, { email, form: 'email' });
		return { done: 'emailSent' } as const;
	},
	deleteAccount: async (event) => {
		const r = await api(event, 'DELETE', '/api/me');
		if (!r.ok) return refuse(event, r, { form: 'delete' });
		clearSession(event);
		redirect(303, '/');
	},
	reauthenticate
};
