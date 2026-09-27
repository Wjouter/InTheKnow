import { redirect, type Handle, type HandleServerError } from '@sveltejs/kit';
import { api } from '$lib/server/api';
import { dicts, pickLang } from '$lib/i18n';
import { isPublic } from '$lib/paths';

// research R3 to R6, R9, R16, R17.

const unsafe = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

type Settings = { effectiveTimeZone: string };

export const handle: Handle = async ({ event, resolve }) => {
	const started = performance.now();
	const { request, url, cookies, locals } = event;

	const finish = (response: Response) => {
		const h = response.headers;
		h.set('X-Frame-Options', 'DENY');
		h.set('Referrer-Policy', 'no-referrer');
		h.set('X-Content-Type-Options', 'nosniff');
		h.set('Cache-Control', 'no-store');
		log(request.method, url.pathname, response.status, started);
		return response;
	};

	// CSRF (FR-005, R4): the browser says where a request comes from. `Origin` is only a fallback, because no-referrer
	// makes browsers send `Origin: null` on the app's own form posts.
	if (unsafe.has(request.method)) {
		const site = request.headers.get('sec-fetch-site');
		const allowed = site ? site === 'same-origin' : request.headers.get('origin') === url.origin;
		if (!allowed) return finish(new Response('Forbidden', { status: 403 }));
	}

	// Who is signed in, and their time zone.
	locals.token = cookies.get('itk_session') ?? null;
	locals.me = null;
	locals.unavailable = false;
	locals.tz = 'UTC'; // Visitors have no settings to read, and visitor pages show no dates.
	if (locals.token) {
		const [me, settings] = await Promise.all([
			api<Me>(event, 'GET', '/api/me', undefined, { on401: 'return' }),
			api<Settings>(event, 'GET', '/api/me/notification-settings', undefined, { on401: 'return' })
		]);
		if (me.ok && settings.ok) {
			const m = me.data;
			locals.me = {
				email: m.email,
				role: m.role,
				language: m.language,
				twoFactor: m.twoFactor,
				recoveryCodesLeft: m.recoveryCodesLeft,
				hasPassword: m.hasPassword,
				pendingEmail: m.pendingEmail,
				homePage: m.homePage
			};
			locals.tz = settings.data.effectiveTimeZone;
		} else if ([me, settings].some((r) => !r.ok && r.status === 401)) {
			locals.token = null; // api() already deleted the cookie.
		} else {
			locals.unavailable = true;
		}
	}

	locals.lang = locals.me?.language ?? pickLang(cookies.get('itk_lang'), request.headers.get('accept-language'));
	locals.t = dicts[locals.lang];

	// The sign-in gate (R6). It is here, not in a layout, because layout loads don't run for form actions.
	if (!locals.unavailable && !locals.me && !isPublic(url.pathname)) {
		const location = `/sign-in?next=${encodeURIComponent(url.pathname + url.search)}`;
		// Client-side navigation and enhanced forms expect Kit's JSON redirect, which a thrown redirect gives.
		if (event.isDataRequest || request.headers.get('x-sveltekit-action') === 'true') {
			log(request.method, url.pathname, 303, started);
			redirect(303, location);
		}
		return finish(new Response(null, { status: 303, headers: { location } }));
	}

	const response = await resolve(event, {
		transformPageChunk: ({ html }) => html.replace('%lang%', locals.lang)
	});
	return finish(response);
};

/** One line per request, without query, headers, cookies, addresses or users (FR-047). */
function log(method: string, pathname: string, status: number, started: number) {
	console.log(`${new Date().toISOString()} ${method} ${pathname} ${status} ${Math.round(performance.now() - started)}ms`);
}

export const handleError: HandleServerError = ({ error, event }) => {
	const e = error instanceof Error ? error : new Error(String(error));
	console.error(`${new Date().toISOString()} ${event.url.pathname} ${e.name}: ${e.message}`);
	return { message: (event.locals.t ?? dicts.en).errors.generic };
};
