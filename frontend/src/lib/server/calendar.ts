import { error, type RequestEvent } from '@sveltejs/kit';

/** A /calendars/[id] parameter as a positive integer, else the "not in this account" page (spec edge case). */
export function calendarId(event: RequestEvent): number {
	const id = event.params.id ?? '';
	if (!/^[1-9]\d{0,17}$/.test(id)) notInAccount(event);
	return Number(id);
}

export function notInAccount(event: RequestEvent): never {
	error(404, { kind: 'notInAccount', message: event.locals.t.errors.notInAccountTitle, email: event.locals.me?.email });
}
