import { fail, redirect } from '@sveltejs/kit';
import { api } from '$lib/server/api';
import { refuse } from '$lib/server/actions';
import { calendarId } from '$lib/server/calendar';
import type { Refusal } from '$lib/refusal';

export const actions = {
	remove: async (event) => {
		const r = await api(event, 'DELETE', `/api/me/calendars/${calendarId(event)}`);
		if (!r.ok) {
			if (r.status !== 404) return refuse(event, r);
			const { t, me } = event.locals;
			return fail(404, { reason: 'notInAccount', message: t.errors.notInAccount(me?.email ?? '') } satisfies Refusal);
		}
		redirect(303, '/calendars');
	}
};
