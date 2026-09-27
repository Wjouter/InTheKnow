import { env } from '$env/dynamic/private';
import { error, redirect, type RequestEvent } from '@sveltejs/kit';
import { returnPath } from '$lib/next';

// The only module that talks to the API (research R2, R7). Server-only, so the token and API address never reach the
// browser (FR-003).

export type ApiFailure = {
	ok: false;
	status: number;
	reason: string;
	retryAfter?: number;
	extra: Record<string, unknown>;
};
export type ApiResult<T> = { ok: true; status: number; data: T } | ApiFailure;

const unavailable = (): ApiFailure => ({ ok: false, status: 503, reason: 'unavailable', extra: {} });

/** The API path without its query, with numeric segments as {id}: safe to log (FR-047). */
const template = (path: string) => path.split('?')[0].replace(/\/\d+(?=\/|$)/g, '/{id}');

export async function api<T = unknown>(
	event: RequestEvent,
	method: string,
	path: string,
	body?: unknown,
	opts: { token?: string | null; on401?: 'redirect' | 'return' } = {}
): Promise<ApiResult<T>> {
	const token = opts.token ?? event.cookies.get('itk_session');
	const headers: Record<string, string> = {
		'X-Forwarded-For': event.getClientAddress(),
		'User-Agent': event.request.headers.get('user-agent') ?? ''
	};
	if (token) headers.Authorization = `Bearer ${token}`;
	if (body !== undefined) headers['Content-Type'] = 'application/json';

	let res: Response;
	try {
		res = await fetch(`${env.API_URL || 'http://localhost:5000'}${path}`, {
			method,
			headers,
			body: body === undefined ? undefined : JSON.stringify(body),
			signal: AbortSignal.timeout(30_000)
		});
	} catch {
		console.error(`api ${method} ${template(path)} unreachable`);
		return unavailable();
	}

	const text = await res.text().catch(() => '');
	let json: unknown = null;
	try {
		json = text ? JSON.parse(text) : null;
	} catch {
		json = null;
	}
	if (res.ok) return { ok: true, status: res.status, data: json as T };

	const problem = (json && typeof json === 'object' ? json : {}) as Record<string, unknown>;
	const { reason, type: _t, title: _ti, status: _s, detail: _d, instance: _i, traceId: _tr, ...extra } = problem;
	if (typeof reason !== 'string') {
		console.error(`api ${method} ${template(path)} ${res.status}`);
		// A 5xx without a reason is the API being down or broken; any other bare status is an unexpected refusal.
		return res.status >= 500 ? unavailable() : { ok: false, status: res.status, reason: 'unknown', extra: {} };
	}
	console.error(`api ${method} ${template(path)} ${res.status} ${reason}`);

	const failure: ApiFailure = { ok: false, status: res.status, reason, extra };
	const retryAfter = Number(res.headers.get('retry-after'));
	if (retryAfter > 0) failure.retryAfter = retryAfter;

	if (res.status === 401 && reason === 'not_signed_in') {
		event.cookies.delete('itk_session', { path: '/' });
		if (opts.on401 !== 'return') redirect(303, `/sign-in?next=${encodeURIComponent(returnPath(event.url))}`);
	}
	return failure;
}

/** For loads: the data, or the error page that fits the refusal. */
export function must<T>(
	event: RequestEvent,
	r: ApiResult<T>,
	opts: { notFound?: 'notFound' | 'notInAccount' } = {}
): T {
	if (r.ok) return r.data;
	const t = event.locals.t;
	if (r.status === 403 && r.reason === 'two_factor_required') error(403, { kind: 'locked', message: t.errors.lockedTitle });
	if (r.status === 403 && r.reason === 'admin_required') error(403, { kind: 'notAllowed', message: t.errors.notAllowedTitle });
	if (r.status === 404) {
		const kind = opts.notFound ?? 'notFound';
		error(404, kind === 'notInAccount'
			? { kind, message: t.errors.notInAccountTitle, email: event.locals.me?.email }
			: { kind, message: t.errors.notFoundTitle });
	}
	if (r.reason === 'unavailable') error(503, { kind: 'unavailable', message: t.errors.unavailableTitle });
	error(r.status, { message: t.errors.generic });
}

const cookie = { httpOnly: true, secure: true, sameSite: 'lax' } as const;

export function setSession(event: RequestEvent, token: string): void {
	event.cookies.set('itk_session', token, { ...cookie, path: '/', maxAge: 34_560_000 });
}
export function clearSession(event: RequestEvent): void {
	event.cookies.delete('itk_session', { path: '/' });
}
export function setPending(event: RequestEvent, token: string): void {
	event.cookies.set('itk_pending', token, { ...cookie, path: '/sign-in', maxAge: 600 });
}
export function clearPending(event: RequestEvent): void {
	event.cookies.delete('itk_pending', { path: '/sign-in' });
}
