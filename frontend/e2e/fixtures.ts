// The stub API's fixed answers (e2e/api-stub.ts), typed with the frontend's own types so `npm run check` fails when
// a type and a fixture drift apart. Placeholder values only.
import type { Calendar, Change } from '../src/lib/server/changes';
import type { HistoryData } from '../src/lib/server/history';
import type { Preset } from '../src/lib/server/presets';
import type { ChannelType } from '../src/routes/admin/+layout.server';
import type { Account } from '../src/routes/admin/accounts/+page.server';
import type { AdminCalendar } from '../src/routes/admin/calendars/+page.server';
import type { Invite, Settings as SignUpSettings } from '../src/routes/admin/sign-up/+page.server';
import type { Filter } from '../src/routes/calendars/[id]/filter/+page.server';
import type { Settings as CalendarNotifications } from '../src/routes/calendars/[id]/notifications/+page.server';
import type { Channel } from '../src/routes/(settings)/notifications/+page.server';
import type { Settings as NotificationSettings } from '../src/routes/(settings)/settings/preferences/+page.server';
import type { Method, Session } from '../src/routes/(settings)/settings/security/+page.server';

type Lang = 'en' | 'nl';

export const me = (language: Lang): Me => ({
	email: 'admin@example.com',
	role: 'admin',
	language,
	twoFactor: true,
	recoveryCodesLeft: 8,
	hasPassword: true,
	pendingEmail: null,
	homePage: 'changes'
});

export const notificationSettings = (language: Lang): NotificationSettings => ({
	language,
	timeZone: null,
	effectiveTimeZone: 'Europe/Amsterdam'
});

export const calendars: Calendar[] = [
	{
		calendarId: 1,
		status: 'ok',
		lastCheckedAt: '2026-11-16T08:00:00Z',
		lastOk: true,
		lastError: null,
		consecutiveFailures: 0,
		nextCheckAt: '2026-11-16T09:00:00Z',
		notify: true,
		label: null,
		name: 'Class 1A',
		links: []
	},
	{
		calendarId: 2,
		status: 'failing',
		lastCheckedAt: '2026-11-16T08:00:00Z',
		lastOk: false,
		lastError: 'http_404',
		consecutiveFailures: 3,
		nextCheckAt: '2026-11-16T09:00:00Z',
		notify: false,
		label: 'Teacher',
		name: 'Room 2.14',
		links: []
	}
];

const at = (day: number, hour: number) => `2026-11-${day}T${String(hour).padStart(2, '0')}:00:00Z`;
const span = (day: number, from: number, to: number) => ({ start: at(day, from), end: at(day, to) });
const change = (id: number, c: Partial<Change>): Change => ({
	id,
	calendarId: 1,
	eventKey: `event-${id}`,
	kind: 'changed',
	field: null,
	oldValue: null,
	newValue: null,
	eventName: 'Mathematics',
	eventStartsAt: '2026-11-18T09:00:00Z',
	detectedAt: at(16, id),
	outcome: 'normal',
	...c
});

/** Newest first: one change of each kind, a low-priority one and a hidden one. */
export const changes: Change[] = [
	change(6, { eventName: 'Physics', field: 'status', oldValue: 'confirmed', newValue: 'cancelled' }),
	change(5, { eventName: 'Chemistry', field: 'location', oldValue: 'Room 1.01', newValue: 'Room 2.14', calendarId: 2 }),
	change(4, { eventName: 'Biology', kind: 'added', newValue: span(19, 10, 12) }),
	change(3, { eventName: 'Mathematics', field: 'time', oldValue: span(18, 9, 10), newValue: span(18, 11, 12) }),
	change(2, { eventName: 'History', field: 'description', oldValue: 'a', newValue: 'b', outcome: 'lowPriority' }),
	change(1, { eventName: 'Holiday', field: 'type', oldValue: 'lesson', newValue: 'holiday', outcome: 'hidden' })
];

const lesson = { name: 'Mathematics', ...span(18, 11, 12), status: 'confirmed', fields: { rooms: ['Room 1.01'], teachers: ['T. Teacher'] } };

/** Change 6 is of a lesson that is gone, change 4 of one removed from the timetable; any other id is still there. */
export const history = (id: number): HistoryData => ({
	changeId: id,
	calendarId: 1,
	calendarName: 'Class 1A',
	current:
		id === 6
			? { state: 'gone', event: null, removedAt: null }
			: id === 4
				? { state: 'removed', event: lesson, removedAt: at(16, 4) }
				: { state: 'present', event: lesson, removedAt: null },
	historyStartsAt: '2026-09-01T00:00:00Z',
	retentionDays: 365,
	items: [
		...(id === 4 ? [change(4, { kind: 'removed', eventKey: 'event-3' })] : []),
		change(3, { field: 'time', oldValue: span(18, 9, 10), newValue: span(18, 11, 12) }),
		change(2, { field: 'rooms', oldValue: ['Room 0.01'], newValue: ['Room 1.01'], eventKey: 'event-3' }),
		change(1, { field: 'description', oldValue: 'a', newValue: 'b', eventKey: 'event-3', outcome: 'lowPriority' }),
		change(5, { field: 'name', oldValue: 'Maths', newValue: 'Mathematics', eventKey: 'event-3', detectedAt: at(15, 9), outcome: 'hidden' }),
		change(7, { kind: 'added', newValue: span(18, 9, 10), eventKey: 'event-3', detectedAt: at(15, 8) })
	]
});

const rules: Preset['rules'] = [
	{ outcome: 'hide', when: { activityType: ['holiday'] } },
	{ outcome: 'lowPriority', when: { changeKind: ['changed'], nameContains: 'History' } }
];

export const presets: Preset[] = [
	{ id: 1, name: 'My filter', scope: 'personal', rules },
	{ id: 2, name: 'School default', scope: 'global', isDefault: true, rules, followers: 12 }
];

export const filter: Filter = {
	source: { type: 'custom' },
	rules,
	inactiveRules: [],
	activeConditions: ['changeKind', 'eventEnded', 'statusFrom', 'statusTo', 'nameContains', 'activityType', 'group', 'room', 'teacher'],
	values: { activityType: ['lesson', 'holiday'], group: ['1A'], room: ['Room 1.01'], teacher: ['T. Teacher'] }
};

export const calendarNotifications: CalendarNotifications = { notify: true, label: null, name: 'Class 1A' };

export const channels: Channel[] = [
	{ id: 1, type: 'email', target: 'admin@example.com', status: 'active', failureReason: null, lastMessageAt: '2026-11-15T08:00:00Z', accountAddress: true },
	{ id: 2, type: 'email', target: 'other@example.com', status: 'failing', failureReason: 'bounced', lastMessageAt: null, accountAddress: false }
];

export const sessions: Session[] = [
	{ id: 1, device: 'Firefox on Linux', createdAt: '2026-11-01T08:00:00Z', lastUsedAt: '2026-11-16T08:00:00Z', current: true },
	{ id: 2, device: 'Safari on iPhone', createdAt: '2026-10-01T08:00:00Z', lastUsedAt: '2026-11-10T08:00:00Z', current: false }
];

export const methods: Method[] = [
	{ id: 1, type: 'password', createdAt: '2026-09-01T08:00:00Z', lastUsedAt: '2026-11-16T08:00:00Z' },
	{ id: 2, type: 'totp', createdAt: '2026-09-02T08:00:00Z', lastUsedAt: null }
];

export const channelTypes: ChannelType[] = [
	{
		type: 'email',
		configured: true,
		lastAttemptAt: '2026-11-16T08:00:00Z',
		lastOk: false,
		lastReason: 'smtp_unreachable',
		consecutiveFailures: 2,
		failingSince: '2026-11-16T07:00:00Z',
		waiting: 4
	}
];

export const accounts: Account[] = [
	{ id: 1, email: 'admin@example.com', role: 'admin', confirmed: true, disabled: false, twoFactor: true, createdAt: '2026-09-01T08:00:00Z' },
	{ id: 2, email: 'user@example.com', role: 'user', confirmed: false, disabled: true, twoFactor: false, createdAt: '2026-10-01T08:00:00Z' }
];

export const adminCalendars: AdminCalendar[] = [
	{ calendarId: 1, status: 'ok', lastCheckedAt: '2026-11-16T08:00:00Z', lastOk: true, lastError: null, consecutiveFailures: 0, nextCheckAt: '2026-11-16T09:00:00Z', subscribers: 3, validLinks: 2, totalLinks: 2 },
	{ calendarId: 2, status: 'failing', lastCheckedAt: '2026-11-16T08:00:00Z', lastOk: false, lastError: 'http_404', consecutiveFailures: 3, nextCheckAt: '2026-11-16T09:00:00Z', subscribers: 1, validLinks: 0, totalLinks: 1 }
];

export const signUpSettings: SignUpSettings = { open: false, allowedDomains: ['example.com'] };

export const invites: Invite[] = [
	{ id: 1, kind: 'named', email: 'new@example.com', language: 'en', grantsAdmin: false, maxUses: 1, used: 0, left: 1, expiresAt: '2026-11-23T08:00:00Z' },
	{ id: 2, kind: 'shareable', email: null, language: 'nl', grantsAdmin: false, maxUses: 30, used: 4, left: 26, expiresAt: '2026-11-30T08:00:00Z' }
];

export const twoFactorSetup = { secret: 'JBSWY3DPEHPK3PXP', uri: 'otpauth://totp/InTheKnow:admin@example.com?secret=JBSWY3DPEHPK3PXP&issuer=InTheKnow' };
