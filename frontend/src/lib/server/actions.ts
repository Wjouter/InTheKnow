import { fail, type ActionFailure, type RequestEvent } from '@sveltejs/kit';
import { reasonMessage } from '$lib/codes';
import { formatter } from './format';
import { api, type ApiFailure } from './api';
import type { Refusal } from '$lib/refusal';

// Shared by every form action (research R7, R8).

export function refuse(event: RequestEvent, r: ApiFailure, values?: Record<string, string>): ActionFailure<Refusal> {
	const { t, lang, tz } = event.locals;
	let message = reasonMessage(r.reason, t);
	if (message === t.reasons.unknown && r.reason !== 'unknown') console.error(`unknown reason ${r.reason}`);
	if (r.retryAfter) message += ` ${t.wait(formatter(lang, tz).wait(r.retryAfter))}`;
	const body: Refusal = { reason: r.reason, message };
	if (typeof r.extra.rule === 'number') body.rule = r.extra.rule;
	if (r.status === 403 && r.reason === 'reauthentication_required') body.stepUp = true;
	if (values) body.values = values;
	return fail(r.status, body);
}

const secret = new Set(['password', 'newPassword', 'code', 'token']);

/** The listed fields to refill a form after a refusal. Never a password, code or token. */
export function formValues(data: FormData, keep: string[]): Record<string, string> {
	const values: Record<string, string> = {};
	for (const key of keep) {
		const v = data.get(key);
		if (!secret.has(key) && typeof v === 'string') values[key] = v;
	}
	return values;
}

/** A string field as typed ('' when absent). Use this for passwords. */
export const raw = (data: FormData, key: string) => {
	const v = data.get(key);
	return typeof v === 'string' ? v : '';
};

/** A string field, trimmed; '' when absent. */
export const field = (data: FormData, key: string) => raw(data, key).trim();

export type BotCheck = { provider: 'turnstile' | 'recaptcha'; siteKey: string } | null;

/** The bot check provider for the four public forms, or null when none is configured (research R15). */
export async function loadBotCheck(event: RequestEvent): Promise<BotCheck> {
	const r = await api<{ provider: string | null; siteKey: string | null }>(event, 'GET', '/api/auth/bot-check', undefined, {
		on401: 'return'
	});
	if (!r.ok || !r.data.siteKey || (r.data.provider !== 'turnstile' && r.data.provider !== 'recaptcha')) return null;
	return { provider: r.data.provider, siteKey: r.data.siteKey };
}

/** The widget's own hidden field, sent to the API as `botCheck`. */
export function botCheckValue(data: FormData): string | undefined {
	for (const key of ['cf-turnstile-response', 'g-recaptcha-response']) {
		const v = data.get(key);
		if (typeof v === 'string' && v) return v;
	}
	return undefined;
}

/** The add-calendar form, shared by the calendars page and the empty home page (FR-024). */
export async function addLink(event: RequestEvent) {
	const url = field(await event.request.formData(), 'url');
	// The link travels only in the body (001 FR-032). Validation can take seconds.
	const r = await api(event, 'POST', '/api/me/links', { url });
	if (!r.ok) return refuse(event, r, { url });
	return { added: true } as const;
}
