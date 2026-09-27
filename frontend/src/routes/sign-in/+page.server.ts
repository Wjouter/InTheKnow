import { redirect } from '@sveltejs/kit';
import { safeNext } from '$lib/next';
import { api, setPending, setSession } from '$lib/server/api';
import { botCheckValue, field, loadBotCheck, raw, refuse } from '$lib/server/actions';

export const load = async (event) => {
	const next = safeNext(event.url.searchParams.get('next'), event.url.origin);
	if (event.locals.me) redirect(303, next);
	return { botCheck: await loadBotCheck(event), expired: event.url.searchParams.get('expired') === 'code' };
};

export const actions = {
	default: async (event) => {
		const data = await event.request.formData();
		const email = field(data, 'email');
		// on401 'return': here a 401 is invalid_credentials, not an ended session.
		const r = await api<{ token: string; secondFactorRequired: boolean }>(
			event,
			'POST',
			'/api/auth/sign-in',
			{ email, password: raw(data, 'password'), botCheck: botCheckValue(data) },
			{ on401: 'return' }
		);
		if (!r.ok) return refuse(event, r, { email });

		const next = safeNext(event.url.searchParams.get('next'), event.url.origin);
		if (r.data.secondFactorRequired) {
			// Never itk_session: the gate would treat this half-signed-in token as signed in.
			setPending(event, r.data.token);
			redirect(303, `/sign-in/code?next=${encodeURIComponent(next)}`);
		}
		setSession(event, r.data.token);
		redirect(303, next);
	}
};
