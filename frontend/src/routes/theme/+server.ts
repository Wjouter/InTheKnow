import { redirect } from '@sveltejs/kit';
import { safeNext } from '$lib/next';
import { field } from '$lib/server/actions';
import { storeTheme } from '$lib/theme';

// The theme switch (011 contracts/theme.md). An endpoint, not a form action: the Preferences radio buttons sit inside
// another form and post here with fetch (research R4).
export const POST = async ({ request, cookies, url }) => {
	const data = await request.formData();
	storeTheme(cookies, field(data, 'theme'));
	redirect(303, safeNext(field(data, 'next'), url.origin));
};

export const GET = () => redirect(303, '/');
