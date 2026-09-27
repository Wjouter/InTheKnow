import { channelFailureMessage } from '$lib/codes';
import { formatter } from '$lib/server/format';

// The channel types come from the admin layout's load; this only words and formats them (FR-039).
export const load = async ({ parent, locals }) => {
	const { channelTypes } = await parent();
	const fmt = formatter(locals.lang, locals.tz);
	return {
		types: channelTypes.map((c) => ({
			type: c.type,
			configured: c.configured,
			lastAttempt: c.lastAttemptAt ? fmt.dateTime(c.lastAttemptAt) : null,
			lastOk: c.lastOk,
			lastReason: c.lastReason ? channelFailureMessage(c.lastReason, locals.t) : null,
			failures: c.consecutiveFailures,
			failingSince: c.failingSince ? fmt.dateTime(c.failingSince) : null,
			waiting: c.waiting
		}))
	};
};
