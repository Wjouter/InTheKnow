import { redirect } from '@sveltejs/kit';
import { safeNext } from '$lib/next';
import { api, clearPending, setSession } from '$lib/server/api';
import { field, refuse } from '$lib/server/actions';

const expired = (url: URL) =>
	`/sign-in?next=${encodeURIComponent(safeNext(url.searchParams.get('next'), url.origin))}&expired=code`;

export const load = ({ cookies, url }) => {
	if (!cookies.get('itk_pending')) redirect(303, expired(url));
	return {};
};

export const actions = {
	default: async (event) => {
		const pending = event.cookies.get('itk_pending');
		if (!pending) redirect(303, expired(event.url));
		const code = field(await event.request.formData(), 'code');
		const r = await api(event, 'POST', '/api/auth/sign-in/code', { code }, { token: pending, on401: 'return' });
		if (!r.ok) {
			if (r.status === 401) {
				// The awaiting-code session timed out: start again.
				clearPending(event);
				redirect(303, expired(event.url));
			}
			return refuse(event, r);
		}
		// The API returns no new token: the same one is now fully signed in.
		setSession(event, pending);
		clearPending(event);
		redirect(303, safeNext(event.url.searchParams.get('next'), event.url.origin));
	}
};
