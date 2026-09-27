import { api, must } from '$lib/server/api';
import { calendarId, notInAccount } from '$lib/server/calendar';
import { calendarView, type Calendar } from '$lib/server/changes';
import { formatter } from '$lib/server/format';

export const load = async (event) => {
	const id = calendarId(event);
	const { t, lang, tz } = event.locals;
	const calendar = must(event, await api<Calendar[]>(event, 'GET', '/api/me/calendars')).find((c) => c.calendarId === id);
	if (!calendar) notInAccount(event);
	return { calendar: calendarView(calendar, t, formatter(lang, tz)) };
};
