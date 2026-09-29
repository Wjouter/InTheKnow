import type { Dict } from '$lib/i18n';

// Every reason the API can send to a route the frontend calls (001 to 005 contracts/api.md), plus the frontend's own
// `unavailable` and `unknown`. codes.test.ts keeps this list complete, and svelte-check keeps the dictionaries
// complete (SC-009, research R10).
export const reasons = [
	'unavailable',
	'unknown',
	// 004 authentication, bot check and throttling
	'not_signed_in',
	'admin_required',
	'two_factor_required',
	'reauthentication_required',
	'bot_check_failed',
	'try_again_later',
	'too_many_attempts',
	'too_many_requests',
	// 004 sign-up, confirmation, sign-in, reset, email change, invites
	'invalid_email',
	'invalid_language',
	'password_length',
	'password_common',
	'password_is_email',
	'sign_up_by_invitation',
	'invite_invalid',
	'token_invalid',
	'password_incorrect',
	'invalid_credentials',
	'not_confirmed',
	'account_disabled',
	'code_invalid',
	'code_required',
	'address_taken',
	'same_email',
	// 004 account, sessions, methods, 2FA
	'last_admin',
	'use_sign_out',
	'last_method',
	'two_factor_on',
	'two_factor_off',
	'setup_not_started',
	'demote_first',
	// 004 admin
	'invalid_role',
	'invalid_settings',
	'invalid_domain',
	'too_many_domains',
	'invalid_kind',
	'invalid_valid_days',
	'invalid_max_uses',
	'admin_needs_named',
	'account_exists',
	'invite_exists',
	'not_named',
	// 001 links, calendars, changes
	'invalid_url',
	'unsupported_scheme',
	'url_too_long',
	'blocked_address',
	'host_budget_exhausted',
	'invalid_limit',
	'invalid_status',
	// 002 filters and presets
	'invalid_include_hidden',
	'invalid_name',
	'invalid_rules',
	'invalid_outcome',
	'empty_conditions',
	'invalid_condition',
	'preset_is_global',
	'preset_is_default',
	'name_taken',
	'preset_limit',
	// 003 notification settings and channels
	'invalid_time_zone',
	'empty_body',
	'invalid_label',
	'invalid_notify',
	'invalid_type',
	'invalid_target',
	'target_taken',
	'channel_limit',
	'target_declined',
	'channel_not_active',
	'confirmation_limit',
	'test_limit',
	'type_unavailable',
	// 005 additions
	'invalid_order',
	'invalid_cursor',
	'invalid_home_page'
] as const;
export type Reason = (typeof reasons)[number];

// 001 data-model `last_error`, also sent as `validation_failed:<code>` when adding a link. `http_<n>` is a pattern.
export const checkFailures = [
	'network',
	'timeout',
	'too_large',
	'unparseable',
	'empty_feed',
	'too_many_occurrences',
	'blocked_address'
] as const;
export type CheckFailure = (typeof checkFailures)[number];

const isReason = (code: string): code is Reason => (reasons as readonly string[]).includes(code);
const isCheckFailure = (code: string): code is CheckFailure => (checkFailures as readonly string[]).includes(code);

export function checkFailureMessage(code: string, t: Dict): string {
	const c = code.startsWith('validation_failed:') ? code.slice('validation_failed:'.length) : code;
	if (isCheckFailure(c)) return t.checkFailures[c];
	const http = /^http_(\d{3})$/.exec(c);
	if (http) {
		if (http[1] === '404' || http[1] === '410') return t.patterns.linkGone;
		if (http[1] === '401' || http[1] === '403') return t.patterns.linkRefused;
		return t.patterns.serverError;
	}
	return t.reasons.unknown;
}

export function channelFailureMessage(code: string, t: Dict): string {
	const smtp = /^smtp_([45])\d\d$/.exec(code);
	if (smtp) return smtp[1] === '5' ? t.patterns.mailRejected : t.patterns.mailDeferred;
	if (code === 'unreachable') return t.patterns.mailUnreachable;
	return t.reasons.unknown;
}

export function reasonMessage(reason: string, t: Dict): string {
	if (reason.startsWith('validation_failed:')) return checkFailureMessage(reason, t);
	return isReason(reason) ? t.reasons[reason] : t.reasons.unknown;
}
