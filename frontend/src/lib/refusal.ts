/** What an action returns on an API refusal (research R7). Built by refuse() in $lib/server/actions. */
export type Refusal = { reason: string; message: string; rule?: number; stepUp?: true; values?: Record<string, string> };

/** The refusal in an action result, if it is one. */
export function refusal(form: unknown): Refusal | undefined {
	return form && typeof form === 'object' && 'reason' in form && 'message' in form ? (form as Refusal) : undefined;
}

/** The refusal's message when its reason is one of `reasons`: shown next to that field. */
export function fieldError(form: unknown, ...reasons: string[]): string | undefined {
	const r = refusal(form);
	return r && reasons.includes(r.reason) ? r.message : undefined;
}

/** The refusal's message when no field shows it. */
export function formError(form: unknown, ...fieldReasons: string[]): string | undefined {
	const r = refusal(form);
	return r && !r.stepUp && !fieldReasons.includes(r.reason) ? r.message : undefined;
}
