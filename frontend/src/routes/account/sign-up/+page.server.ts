import { redirect } from '@sveltejs/kit';
import { api } from '$lib/server/api';
import { botCheckValue, field, loadBotCheck, raw, refuse } from '$lib/server/actions';

const inviteCookie = { httpOnly: true, secure: true, sameSite: 'lax', path: '/account/sign-up', maxAge: 3600 } as const;

// The same result for every address (SC-005).
const done = { done: true } as const;

export const load = async (event) => {
	// A shareable invite link: move the token out of the address before any bot check script can read it (research R15).
	const invite = event.url.searchParams.get('invite');
	if (invite) {
		event.cookies.set('itk_invite', invite, inviteCookie);
		redirect(303, '/account/sign-up');
	}
	return { botCheck: await loadBotCheck(event), invite: Boolean(event.cookies.get('itk_invite')) };
};

export const actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const email = field(data, 'email');
		const language = field(data, 'language');
		const invite = event.cookies.get('itk_invite');
		const r = await api(event, 'POST', '/api/auth/sign-up', {
			email,
			password: raw(data, 'password'),
			language,
			invite,
			botCheck: botCheckValue(data)
		});
		if (!r.ok) return refuse(event, r, { email, language });
		if (invite) event.cookies.delete('itk_invite', { path: '/account/sign-up' });
		return done;
	}
};
