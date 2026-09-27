import { redirect } from '@sveltejs/kit';
import { safeNext } from '$lib/next';
import { field } from '$lib/server/actions';

// The visitor's language switch (US9-1). Signed-in users change it on Preferences instead.
export const load = () => redirect(303, '/');

export const actions = {
	default: async ({ request, cookies, url }) => {
		const data = await request.formData();
		const lang = field(data, 'lang');
		if (lang === 'en' || lang === 'nl') {
			// Not HttpOnly and not personal data (data-model.md).
			cookies.set('itk_lang', lang, { httpOnly: false, secure: true, sameSite: 'lax', path: '/', maxAge: 31_536_000 });
		}
		redirect(303, safeNext(field(data, 'next'), url.origin));
	}
};
