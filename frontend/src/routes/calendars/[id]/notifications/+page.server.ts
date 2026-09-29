import { api, must } from '$lib/server/api';
import { field, refuse } from '$lib/server/actions';
import { calendarId } from '$lib/server/calendar';

type Settings = { notify: boolean; label: string | null; name: string };

// Opening this page changes nothing (FR-014): emails link here, and only the Save button acts.
export const load = async (event) =>
	must(event, await api<Settings>(event, 'GET', `/api/me/calendars/${calendarId(event)}/notifications`), { notFound: 'notInAccount' });

export const actions = {
	save: async (event) => {
		const data = await event.request.formData();
		const label = field(data, 'label');
		const notify = data.has('notify');
		// An empty label clears it, so emails fall back to the feed's name.
		const r = await api(event, 'PUT', `/api/me/calendars/${calendarId(event)}/notifications`, { notify, label: label || null });
		if (!r.ok) return refuse(event, r, { label, notify: String(notify) });
		return { saved: true } as const;
	}
};
