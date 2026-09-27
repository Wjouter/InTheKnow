import { api, must } from '$lib/server/api';
import { after } from '$lib/server/admin';
import { checkFailureMessage } from '$lib/codes';
import { formatter } from '$lib/server/format';

type AdminCalendar = {
	calendarId: number;
	status: 'ok' | 'failing';
	lastCheckedAt: string | null;
	lastOk: boolean | null;
	lastError: string | null;
	consecutiveFailures: number;
	nextCheckAt: string;
	subscribers: number;
	validLinks: number;
	totalLinks: number;
};

export const load = async (event) => {
	const { t, lang, tz } = event.locals;
	const q = event.url.searchParams;
	const status = q.get('status') === 'ok' || q.get('status') === 'failing' ? q.get('status')! : '';
	const overdue = q.get('overdue') === 'true';
	const query = new URLSearchParams({ limit: '50' });
	if (status) query.set('status', status);
	if (overdue) query.set('overdue', 'true');
	const from = after(event.url);
	if (from) query.set('after', from);
	const page = must(event, await api<{ items: AdminCalendar[]; next: number | null }>(event, 'GET', `/api/admin/calendars?${query}`));
	const fmt = formatter(lang, tz);
	// No links or addresses: only names, states and counts (FR-039).
	return {
		status,
		overdue,
		next: page.next,
		calendars: page.items.map((c) => ({
			id: c.calendarId,
			// GET /api/admin/calendars carries no name (the admin query selects none), so the id stands in.
			name: t.admin.calendars.name(c.calendarId),
			status: c.status,
			error: c.lastOk === false && c.lastError ? checkFailureMessage(c.lastError, t) : null,
			failures: c.consecutiveFailures,
			lastChecked: c.lastCheckedAt ? fmt.dateTime(c.lastCheckedAt) : null,
			nextCheck: fmt.dateTime(c.nextCheckAt),
			subscribers: c.subscribers,
			validLinks: c.validLinks,
			totalLinks: c.totalLinks
		}))
	};
};
