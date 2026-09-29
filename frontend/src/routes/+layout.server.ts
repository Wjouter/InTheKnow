import { isPublic } from '$lib/paths';
import type { LayoutServerLoad } from './$types';

// No error() here: an error in the root layout's load has no +error.svelte above it, and Kit would show the static
// error.html instead. The layout shows the unavailable view itself.
export const load: LayoutServerLoad = ({ locals, url }) => {
	// Read the path first, always: that makes this load rerun on every navigation, so the header follows a session
	// that ended meanwhile (a `&&` that skipped it would leave the signed-in nav on the sign-in page).
	const publicPath = isPublic(url.pathname);
	return { lang: locals.lang, me: locals.me, unavailable: locals.unavailable && !publicPath };
};
