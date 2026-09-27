import { error } from '@sveltejs/kit';
import { api, must, signedIn } from '$lib/server/api';

export type ChannelType = {
	type: string;
	configured: boolean;
	lastAttemptAt: string | null;
	lastOk: boolean | null;
	lastReason: string | null;
	consecutiveFailures: number;
	failingSince: string | null;
	waiting: number;
};

// Non-admins get "not allowed" (US8-2). An admin session without a second factor gets the API's two_factor_required,
// which must() turns into the locked page with a way forward (US8-1). Actions don't run this load: their refusals
// go through refuse() instead.
export const load = async (event) => {
	if (signedIn(event).role !== 'admin') error(403, { kind: 'notAllowed', message: event.locals.t.errors.notAllowedTitle });
	const types = must(event, await api<{ items: ChannelType[] }>(event, 'GET', '/api/admin/channel-types'));
	return { channelTypes: types.items };
};
