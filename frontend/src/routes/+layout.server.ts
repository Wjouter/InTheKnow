import { isPublic } from '$lib/paths';
import type { LayoutServerLoad } from './$types';

// No error() here: an error in the root layout's load has no +error.svelte above it, and Kit would show the static
// error.html instead. The layout shows the unavailable view itself.
export const load: LayoutServerLoad = ({ locals, url }) => ({
	lang: locals.lang,
	me: locals.me,
	unavailable: locals.unavailable && !isPublic(url.pathname)
});
