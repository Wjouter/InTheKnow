import { api, must } from '$lib/server/api';
import { addLink } from '$lib/server/actions';
import { calendarView, type Calendar } from '$lib/server/changes';
import { formatter } from '$lib/server/format';

export const load = async (event) => {
	const { t, lang, tz } = event.locals;
	const calendars = must(event, await api<Calendar[]>(event, 'GET', '/api/me/calendars'));
	const fmt = formatter(lang, tz);
	return { calendars: calendars.map((c) => calendarView(c, t, fmt)) };
};

export const actions = { addLink };
