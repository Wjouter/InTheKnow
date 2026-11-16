// A stand-in for the InTheKnow API with fixed answers, for the browser tests (011 research R8). Run by
// playwright.config.ts with Node's own type stripping, so: Node modules and ./fixtures.ts only.
import { createServer } from 'node:http';
import * as f from './fixtures.ts';

type Lang = 'en' | 'nl';

const get: Record<string, (lang: Lang, id: number) => unknown> = {
	'/api/me': (lang) => f.me(lang),
	'/api/me/notification-settings': (lang) => f.notificationSettings(lang),
	'/api/me/calendars': () => f.calendars,
	'/api/me/changes': () => ({ items: f.changes, next: null }),
	'/api/me/changes/{id}/history': (_, id) => f.history(id),
	'/api/me/calendars/{id}/filter': () => f.filter,
	'/api/me/calendars/{id}/notifications': () => f.calendarNotifications,
	'/api/me/presets': () => ({ items: f.presets }),
	'/api/me/channels': () => ({ items: f.channels }),
	'/api/me/channel-types': () => ({ items: [{ type: 'email' }] }),
	'/api/me/sessions': () => ({ items: f.sessions }),
	'/api/me/methods': () => ({ items: f.methods }),
	'/api/admin/channel-types': () => ({ items: f.channelTypes }),
	'/api/admin/accounts': () => ({ items: f.accounts, next: null }),
	'/api/admin/calendars': () => ({ items: f.adminCalendars, next: null }),
	'/api/admin/signup-settings': () => f.signUpSettings,
	'/api/admin/invites': () => ({ items: f.invites }),
	'/api/admin/presets': () => ({ items: f.presets.filter((p) => p.scope === 'global') })
};
const post: Record<string, unknown> = { '/api/me/2fa/setup': f.twoFactorSetup };

const port = Number(process.env.STUB_PORT ?? 5199);

createServer((req, res) => {
	const url = new URL(req.url ?? '/', 'http://stub');
	const id = Number(/\/(\d+)(?=\/|$)/.exec(url.pathname)?.[1] ?? 0);
	const route = url.pathname.replace(/\/\d+(?=\/|$)/g, '/{id}');
	// The frontend sends the session cookie as a bearer token; its value picks the account's language.
	const token = /^Bearer (.+)$/.exec(req.headers.authorization ?? '')?.[1];
	const lang: Lang = token === 'nl' ? 'nl' : 'en';
	const send = (status: number, body?: unknown) => {
		res.writeHead(status, { 'content-type': 'application/json' });
		res.end(body === undefined ? '' : JSON.stringify(body));
	};
	req.resume();
	if (route === '/api/auth/bot-check') return send(200, { provider: null, siteKey: null });
	if (!token && (route.startsWith('/api/me') || route.startsWith('/api/admin'))) return send(401, { reason: 'unauthenticated' });
	if (req.method === 'GET') {
		const answer = get[route];
		if (answer) return send(200, answer(lang, id));
		// A missing route would show the unavailable or error view, which passes a contrast check while testing nothing.
		console.error(`api-stub: no answer for GET ${route}`);
		return send(404, { reason: 'not_found' });
	}
	return send(200, post[route] ?? {});
}).listen(port, () => console.log(`api-stub on ${port}`));
