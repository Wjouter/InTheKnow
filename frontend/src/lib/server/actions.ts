import { fail, type ActionFailure, type RequestEvent } from '@sveltejs/kit';
import { reasonMessage } from '$lib/codes';
import { formatter } from './format';
import type { ApiFailure } from './api';

// Shared by every form action (research R7, R8).

export type Refusal = { reason: string; message: string; rule?: number; stepUp?: true; values?: Record<string, string> };

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
