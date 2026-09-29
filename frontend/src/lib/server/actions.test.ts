import { describe, expect, it, vi } from 'vitest';
import { formValues, refuse } from './actions';
import { fakeEvent } from './fake';
import { dicts } from '$lib/i18n';

const failure = (status: number, reason: string, more: { retryAfter?: number; extra?: Record<string, unknown> } = {}) => ({
	ok: false as const,
	status,
	reason,
	extra: more.extra ?? {},
	...(more.retryAfter ? { retryAfter: more.retryAfter } : {})
});

describe('refuse', () => {
	const { event } = fakeEvent();

	it('adds the wait from Retry-After', () => {
		const r = refuse(event, failure(429, 'too_many_attempts', { retryAfter: 120 }));
		expect(r.status).toBe(429);
		expect(r.data.message).toBe(`${dicts.en.reasons.too_many_attempts} Try again in 2 minutes.`);
		expect(refuse(event, failure(429, 'too_many_attempts')).data.message).toBe(dicts.en.reasons.too_many_attempts);
	});

	it('asks for step-up on reauthentication_required', () => {
		expect(refuse(event, failure(403, 'reauthentication_required')).data.stepUp).toBe(true);
		expect(refuse(event, failure(400, 'invalid_email')).data.stepUp).toBeUndefined();
	});

	it('passes the rule index only when it is a number', () => {
		expect(refuse(event, failure(400, 'invalid_condition', { extra: { rule: 2 } })).data.rule).toBe(2);
		expect(refuse(event, failure(400, 'invalid_condition', { extra: { rule: '2' } })).data.rule).toBeUndefined();
		expect(refuse(event, failure(400, 'invalid_condition')).data.rule).toBeUndefined();
	});

	it('never shows a raw code', () => {
		vi.spyOn(console, 'error').mockImplementation(() => {});
		const r = refuse(event, failure(400, 'brand_new_reason'));
		expect(r.data.message).toBe(dicts.en.reasons.unknown);
		expect(r.data.message).not.toContain('brand_new_reason');
	});
});

describe('formValues', () => {
	it('never returns passwords, codes or tokens, even when listed', () => {
		const data = new FormData();
		for (const k of ['email', 'password', 'newPassword', 'code', 'token']) data.set(k, `${k}-value`);
		expect(formValues(data, ['email', 'password', 'newPassword', 'code', 'token'])).toEqual({ email: 'email-value' });
	});
});
