import { api, must } from '$lib/server/api';
import { field, refuse } from '$lib/server/actions';
import { channelFailureMessage } from '$lib/codes';
import { formatter } from '$lib/server/format';

type Status = 'awaitingConfirmation' | 'active' | 'paused' | 'unsubscribed' | 'failing';
type Channel = {
	id: number;
	type: string;
	target: string;
	status: Status;
	failureReason: string | null;
	lastMessageAt: string | null;
	accountAddress: boolean;
};
type Action = 'pause' | 'resume' | 'test' | 'resend' | 'remove';

// What each status allows (data-model.md § Channel, FR-034).
const allowed: Record<Status, Action[]> = {
	awaitingConfirmation: ['resend', 'remove'],
	active: ['pause', 'test', 'remove'],
	paused: ['resume', 'remove'],
	failing: ['resend', 'remove'],
	unsubscribed: ['resend', 'remove']
};

export const load = async (event) => {
	const { t, lang, tz } = event.locals;
	const [channels, types] = await Promise.all([
		api<{ items: Channel[] }>(event, 'GET', '/api/me/channels'),
		api<{ items: { type: string }[] }>(event, 'GET', '/api/me/channel-types')
	]);
	const fmt = formatter(lang, tz);
	return {
		channels: must(event, channels).items.map((c) => ({
			id: c.id,
			target: c.target,
			status: c.status,
			failure: c.status === 'failing' && c.failureReason ? channelFailureMessage(c.failureReason, t) : null,
			lastMessage: c.lastMessageAt ? fmt.dateTime(c.lastMessageAt) : null,
			accountAddress: c.accountAddress,
			actions: allowed[c.status] ?? ['remove']
		})),
		emailAvailable: must(event, types).items.some((x) => x.type === 'email')
	};
};

/** One channel action: the channel id comes from the form, the API checks it is this account's. */
function channelAction(method: string, suffix: string, done: string) {
	return async (event: Parameters<typeof refuse>[0]) => {
		const id = field(await event.request.formData(), 'channelId');
		if (!/^[1-9]\d{0,17}$/.test(id)) return refuse(event, { ok: false, status: 400, reason: 'unknown', extra: {} });
		const r = await api(event, method, `/api/me/channels/${id}${suffix}`);
		if (!r.ok) return refuse(event, r, { channelId: id });
		return { done };
	};
}

export const actions = {
	add: async (event) => {
		const target = field(await event.request.formData(), 'target');
		const r = await api(event, 'POST', '/api/me/channels', { type: 'email', target });
		if (!r.ok) return refuse(event, r, { target, form: 'add' });
		return { done: 'added' };
	},
	pause: channelAction('POST', '/pause', 'paused'),
	resume: channelAction('POST', '/resume', 'resumed'),
	test: channelAction('POST', '/test', 'tested'),
	resend: channelAction('POST', '/confirmation', 'resent'),
	remove: channelAction('DELETE', '', 'removed')
};
