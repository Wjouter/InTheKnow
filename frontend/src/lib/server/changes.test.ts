import { describe, expect, it } from 'vitest';
import { dicts } from '$lib/i18n';
import { calendarView, describe as sentence, group, tone, type Calendar, type Change } from './changes';
import { formatter } from './format';

const now = new Date('2026-09-27T12:00:00Z');
const event = { key: 'K', name: 'ROS2 basics', start: '2026-10-01T06:30:00Z', end: '2026-10-01T08:15:00Z', status: 'confirmed' };
let id = 0;
const change = (kind: Change['kind'], field: string | null, oldValue: unknown, newValue: unknown, more: Partial<Change> = {}): Change => ({
	id: ++id,
	calendarId: 12,
	eventKey: 'K',
	kind,
	field,
	oldValue,
	newValue,
	eventName: 'ROS2 basics',
	eventStartsAt: '2026-10-01T06:30:00Z',
	detectedAt: '2026-09-24T19:30:02Z',
	outcome: 'normal',
	...more
});

// One change per kind/field row of 001 detected-change.md.
const fixture: Change[] = [
	change('added', null, null, event),
	change('removed', null, event, null),
	change('changed', 'name', 'ROS2 basics', 'ROS2 advanced'),
	change('changed', 'time', { start: event.start, end: event.end }, { start: '2026-10-01T07:30:00Z', end: '2026-10-01T09:15:00Z' }),
	change('changed', 'status', 'tentative', 'confirmed'),
	change('changed', 'location', 'AL00.A1.38', null),
	change('changed', 'description', 'old text', 'new text'),
	change('changed', 'type', 'lesson', 'test'),
	change('changed', 'rooms', ['AL00.A1.38'], ['AL00.A1.20']),
	change('changed', 'teachers', ['Jansen, Piet'], ['Jansen, Piet', 'Vries, Anna']),
	change('changed', 'groups', ['ALTI26VT4ES1'], []),
	change('changed', 'details.Opmerking', null, 'Bring a laptop'),
	change('changed', 'details.text', 'a', 'b')
];

describe('describe', () => {
	for (const lang of ['en', 'nl'] as const) {
		const t = dicts[lang];
		const fmt = formatter(lang, 'Europe/Amsterdam', now);
		it.each(fixture.map((c) => [c.field ?? c.kind, c] as const))(`words %s in ${lang}`, (_, c) => {
			const s = sentence(c, t, fmt);
			expect(s.length).toBeGreaterThan(0);
			for (const raw of ['details.', 'eventKey', 'oldValue', '{', '[', 'null', 'undefined']) expect(s).not.toContain(raw);
		});
	}

	const t = dicts.en;
	const fmt = formatter('en', 'Europe/Amsterdam', now);

	it('words status changes', () => {
		expect(sentence(change('changed', 'status', 'tentative', 'confirmed'), t, fmt)).toBe('Event confirmed');
		expect(sentence(change('changed', 'status', 'tentative', 'confirmed'), dicts.nl, fmt)).toBe('Afspraak bevestigd');
		expect(sentence(change('changed', 'status', 'confirmed', 'cancelled'), t, fmt)).toBe('Cancelled');
	});

	it('names what was added and removed in set fields', () => {
		const rooms = sentence(change('changed', 'rooms', ['AL00.A1.38'], ['AL00.A1.20']), t, fmt);
		expect(rooms).toContain('AL00.A1.38');
		expect(rooms).toContain('AL00.A1.20');
		expect(sentence(change('changed', 'groups', ['A'], ['A', 'B']), t, fmt)).toBe('Group B added.');
	});
});

describe('group', () => {
	const t = dicts.en;
	const fmt = formatter('en', 'Europe/Amsterdam', now);
	const cal = (calendarId: number, name: string): Calendar => ({
		calendarId,
		name,
		status: 'ok',
		lastCheckedAt: null,
		lastOk: null,
		lastError: null,
		consecutiveFailures: 0,
		nextCheckAt: '2026-09-27T12:10:00Z',
		notify: true,
		label: null,
		links: []
	});

	it('groups consecutive changes of one event in one check', () => {
		const items = [
			change('changed', 'rooms', ['a'], ['b']),
			change('changed', 'time', null, null),
			change('changed', 'name', 'x', 'y'),
			change('changed', 'name', 'y', 'z', { detectedAt: '2026-09-25T10:00:00Z' })
		];
		const groups = group(items, [cal(12, 'Rooster')], t, fmt);
		expect(groups.map((g) => g.items.length)).toEqual([3, 1]);
		expect(groups[0].when).toBe('Thu 1 Oct 08:30');
	});

	it('takes the most severe tone and keeps outcomes per item', () => {
		const tones = (items: Change[]) => group(items, [cal(12, 'R')], t, fmt).map((g) => g.tone);
		expect(tones([change('changed', 'location', 'a', 'b'), change('removed', null, event, null)])).toEqual(['cancelled']);
		expect(tones([change('added', null, null, event)])).toEqual(['new']);
		expect(tones([change('changed', 'time', null, null)])).toEqual(['changed']);
		expect(tone(change('changed', 'rooms', [], ['x']))).toBe('moved');
		const g = group([change('changed', 'name', 'a', 'b', { outcome: 'lowPriority' }), change('changed', 'type', 'a', 'b', { outcome: 'hidden' })], [cal(12, 'R')], t, fmt);
		expect(g[0].items.map((i) => i.outcome)).toEqual(['lowPriority', 'hidden']);
	});

	it('names the calendar only when there is more than one', () => {
		const c = [change('added', null, null, event)];
		expect(group(c, [cal(12, 'Rooster')], t, fmt)[0].calendarName).toBeNull();
		expect(group(c, [cal(12, 'Rooster'), cal(13, 'Minor')], t, fmt)[0].calendarName).toBe('Rooster');
	});
});

describe('calendarView', () => {
	const t = dicts.en;
	const fmt = formatter('en', 'Europe/Amsterdam', now);
	const base: Calendar = {
		calendarId: 12,
		status: 'ok',
		lastCheckedAt: '2026-09-24T19:30:00Z',
		lastOk: true,
		lastError: null,
		consecutiveFailures: 0,
		nextCheckAt: '2026-09-24T19:40:00Z',
		notify: true,
		label: 'Minor robotics',
		name: 'Minor robotics',
		links: [{ linkId: 40, flagged: false, createdAt: '2026-09-24T18:00:00Z', url: 'https://secret.example/ical?token=abc' }]
	};

	it('gives each state', () => {
		expect(calendarView(base, t, fmt).state).toBe('ok');
		const failedOnce = calendarView({ ...base, lastOk: false, lastError: 'http_404' }, t, fmt);
		expect([failedOnce.state, failedOnce.stateText]).toEqual(['lastFailed', t.patterns.linkGone]);
		const failing = calendarView({ ...base, status: 'failing', lastOk: false, lastError: 'timeout', consecutiveFailures: 4 }, t, fmt);
		expect([failing.state, failing.failures, failing.stateText]).toEqual(['failing', 4, t.checkFailures.timeout]);
		expect(calendarView({ ...base, lastCheckedAt: null, lastOk: null }, t, fmt).state).toBe('notChecked');
		expect(calendarView(base, t, fmt).name).toBe('Minor robotics');
	});

	it('never carries a link or URL', () => {
		const json = JSON.stringify(calendarView(base, t, fmt));
		for (const leak of ['links', 'url', 'https://']) expect(json).not.toContain(leak);
		expect(calendarView(base, t, fmt).linkCount).toBe(1);
	});
});
