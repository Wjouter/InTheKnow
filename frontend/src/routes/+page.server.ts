import { api, must } from '$lib/server/api';
import { addLink } from '$lib/server/actions';
import { loadFeed, type Calendar } from '$lib/server/changes';

// A visitor gets the intro. A signed-in user gets the home page they chose (FR-046), or the empty state.
export const load = async (event) => {
	const me = event.locals.me;
	if (!me) return { view: 'visitor' as const };
	const calendars = must(event, await api<Calendar[]>(event, 'GET', '/api/me/calendars'));
	if (!calendars.length) return { view: 'empty' as const };
	const feed = await loadFeed(event);
	return { view: me.homePage, groups: feed.groups, next: feed.next, calendars: feed.calendars };
};

export const actions = { addLink };
