import { api, must, signedIn } from '$lib/server/api';
import { field, refuse } from '$lib/server/actions';
import { pickTheme, storeTheme } from '$lib/theme';

export type Settings = { language: 'en' | 'nl'; timeZone: string | null; effectiveTimeZone: string };

export const load = async (event) => {
	const settings = must(event, await api<Settings>(event, 'GET', '/api/me/notification-settings'));
	return {
		homePage: signedIn(event).homePage,
		language: settings.language,
		timeZone: settings.timeZone,
		// The API names the operator's default only as effectiveTimeZone, when the account has no zone of its own.
		defaultTimeZone: settings.timeZone === null ? settings.effectiveTimeZone : null,
		timeZones: Intl.supportedValuesOf('timeZone')
	};
};

export const actions = {
	save: async (event) => {
		const data = await event.request.formData();
		// The theme belongs to this device, not the account (011 FR-020): stored here even when the API save fails, and
		// never sent to it. locals too, because the page is rendered in this same request.
		if (data.has('theme')) {
			storeTheme(event.cookies, field(data, 'theme'));
			event.locals.theme = field(data, 'theme') === 'device' ? null : (pickTheme(field(data, 'theme')) ?? event.locals.theme);
		}
		const values = { homePage: field(data, 'homePage'), language: field(data, 'language'), timeZone: field(data, 'timeZone') };
		const home = await api(event, 'PUT', '/api/me/settings', { homePage: values.homePage });
		if (!home.ok) return refuse(event, home, values);
		// The "Default" option is empty and clears the zone of one's own.
		const settings = await api(event, 'PUT', '/api/me/notification-settings', {
			language: values.language,
			timeZone: values.timeZone || null
		});
		if (!settings.ok) return refuse(event, settings, values);
		return { saved: true } as const;
	}
};
