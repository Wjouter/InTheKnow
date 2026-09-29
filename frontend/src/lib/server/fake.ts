import type { RequestEvent } from '@sveltejs/kit';
import { dicts } from '$lib/i18n';

// A RequestEvent for unit tests: records the cookies that were set and deleted.
export function fakeEvent(o: {
	url?: string;
	method?: string;
	cookies?: Record<string, string>;
	headers?: Record<string, string>;
	form?: Record<string, string>;
	locals?: Partial<App.Locals>;
	isDataRequest?: boolean;
	params?: Record<string, string>;
} = {}) {
	const jar = new Map(Object.entries(o.cookies ?? {}));
	const set: Record<string, { value: string; opts: Record<string, unknown> }> = {};
	const deleted: Record<string, Record<string, unknown>> = {};
	const url = new URL(o.url ?? 'http://localhost:5173/', 'http://localhost:5173');
	let body: FormData | undefined;
	if (o.form) {
		body = new FormData();
		for (const [k, v] of Object.entries(o.form)) body.set(k, v);
	}
	const request = new Request(url, { method: o.method ?? (body ? 'POST' : 'GET'), headers: o.headers, body });
	const event = {
		url,
		request,
		params: o.params ?? {},
		route: { id: null },
		isDataRequest: o.isDataRequest ?? false,
		isSubRequest: false,
		getClientAddress: () => '203.0.113.9',
		setHeaders: () => {},
		cookies: {
			get: (name: string) => jar.get(name),
			getAll: () => [...jar].map(([name, value]) => ({ name, value })),
			set: (name: string, value: string, opts: Record<string, unknown>) => {
				jar.set(name, value);
				set[name] = { value, opts };
			},
			delete: (name: string, opts: Record<string, unknown>) => {
				jar.delete(name);
				deleted[name] = opts;
			},
			serialize: () => ''
		},
		locals: { token: null, me: null, tz: 'UTC', lang: 'en', t: dicts.en, unavailable: false, ...o.locals }
	};
	return { event: event as unknown as RequestEvent, set, deleted, jar };
}

/** A fetch mock that answers with JSON (or Problem Details) and records the calls. */
export function jsonResponse(status: number, body?: unknown, headers: Record<string, string> = {}) {
	return new Response(body === undefined ? null : JSON.stringify(body), {
		status,
		headers: { 'content-type': 'application/json', ...headers }
	});
}
