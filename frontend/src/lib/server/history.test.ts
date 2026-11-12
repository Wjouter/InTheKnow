import { describe, expect, it } from 'vitest';
import { dicts } from '$lib/i18n';
import type { Change } from './changes';
import { formatter } from './format';
import { build, entries, row, type HistoryData } from './history';

const now = new Date('2026-11-12T12:00:00Z');
const t = dicts.en;
const fmt = formatter('en', 'Europe/Amsterdam', now);
let id = 100;
const change = (kind: Change['kind'], field: string | null, oldValue: unknown, newValue: unknown, more: Partial<Change> = {}): Change => ({
	id: ++id,
	calendarId: 12,
	eventKey: 'K',
	kind,
	field,
	oldValue,
	newValue,
	eventName: 'Optics',
	eventStartsAt: '2027-01-06T08:00:00Z',
	detectedAt: '2026-11-12T10:00:00Z',
	outcome: 'normal',
	...more
});
const event = { name: 'Optics', start: '2027-01-06T08:00:00Z', end: '2027-01-06T09:30:00Z', status: 'confirmed', fields: {} };

describe('entries (research R6)', () => {
	it('groups rows of one check into one entry in id order, newest entry first', () => {
		const [a, b] = [change('changed', 'name', 'x', 'y'), change('changed', 'rooms', ['A'], ['B'])];
		const c = change('changed', 'type', 'lesson', 'test', { detectedAt: '2026-11-12T11:00:00Z' });
		const list = entries([c, b, a], t, fmt); // the API's id DESC order
		expect(list.map((e) => e.ids)).toEqual([[c.id], [a.id, b.id]]);
		expect(list[1].rows.map((r) => r.label)).toEqual(['Name', 'Rooms']);
		expect(list[1].found.text).toBe('2 hours ago');
		expect(list[1].name).toBe('Optics');
	});

	it('takes added, else removed, else changed as the kind, with the start–end sentence', () => {
		const added = entries([change('added', null, null, event)], t, fmt)[0];
		expect([added.kind, added.summary, added.rows]).toEqual(['added', t.changes.addedAt(fmt.range(event.start, event.end)), []]);
		const removed = entries([change('removed', null, event, null)], t, fmt)[0];
		expect([removed.kind, removed.summary]).toEqual(['removed', t.changes.removed]);
		expect(entries([change('changed', 'name', 'a', 'b')], t, fmt)[0].kind).toBe('changed');
	});

	// T028: outcomes stay per row; the entry takes the mildest.
	it('keeps each row’s outcome and gives the entry the mildest', () => {
		const at = { detectedAt: '2026-11-12T09:00:00Z' };
		const mixed = entries(
			[change('changed', 'name', 'a', 'b', { ...at, outcome: 'hidden' }), change('changed', 'type', 'a', 'b', { ...at, outcome: 'lowPriority' })],
			t,
			fmt
		)[0];
		expect(mixed.rows.map((r) => r.outcome)).toEqual(['hidden', 'lowPriority']);
		expect(mixed.outcomes).toEqual(['hidden', 'lowPriority']);
		expect(mixed.outcome).toBe('lowPriority');
		const withNormal = entries([change('changed', 'name', 'a', 'b', { ...at, outcome: 'hidden' }), change('changed', 'type', 'a', 'b', at)], t, fmt)[0];
		expect(withNormal.outcome).toBe('normal');
		const allHidden = entries([change('changed', 'name', 'a', 'b', { ...at, outcome: 'hidden' }), change('changed', 'type', 'a', 'b', { ...at, outcome: 'hidden' })], t, fmt)[0];
		expect(allHidden.outcome).toBe('hidden');
	});
});

describe('row (research R6)', () => {
	it('shows only the hours for a same-day time change, with the date once', () => {
		const r = row(change('changed', 'time', { start: '2027-01-06T12:30:00Z', end: '2027-01-06T14:00:00Z' }, { start: '2027-01-06T08:00:00Z', end: '2027-01-06T09:30:00Z' }), t, fmt);
		expect([r.label, r.before, r.after, r.note, r.wrap]).toEqual(['Time', '13:30–15:00', '09:00–10:30', 'Same day: Wed 6 Jan 2027', false]);
	});

	it('shows the dates when the day moved, with the year only for another year', () => {
		const r = row(change('changed', 'time', { start: '2026-12-01T12:30:00Z', end: '2026-12-01T14:00:00Z' }, { start: '2026-12-02T08:00:00Z', end: '2026-12-02T09:30:00Z' }), t, fmt);
		expect([r.before, r.after, r.note]).toEqual(['Tue 1 Dec 13:30–15:00', 'Wed 2 Dec 09:00–10:30', null]);
		const next = row(change('changed', 'time', { start: '2026-12-31T12:30:00Z', end: '2026-12-31T14:00:00Z' }, { start: '2027-01-04T08:00:00Z', end: '2027-01-04T09:30:00Z' }), t, fmt);
		expect(next.before).not.toContain('2026');
		expect(next.after).toContain('2027');
	});

	it('joins sets, prints none for empty values, and words status and type', () => {
		expect(row(change('changed', 'teachers', ['A'], ['A', 'B']), t, fmt)).toMatchObject({ label: 'Teachers', before: 'A', after: 'A, B' });
		expect(row(change('changed', 'groups', ['3B'], []), t, fmt)).toMatchObject({ label: 'Groups', before: '3B', after: t.changes.none });
		expect(row(change('changed', 'location', 'B2.14', null), t, fmt)).toMatchObject({ label: 'Location', after: t.changes.none });
		expect(row(change('changed', 'status', 'tentative', 'cancelled'), t, fmt)).toMatchObject({ before: 'tentative', after: 'cancelled' });
		expect(row(change('changed', 'status', 'tentative', 'cancelled'), dicts.nl, fmt)).toMatchObject({ label: 'Status', before: 'voorlopig', after: 'vervallen' });
		expect(row(change('changed', 'type', 'lesson', 'test'), t, fmt)).toMatchObject({ label: 'Type', before: 'lesson', after: 'test' });
	});

	it('lets free text wrap and keeps detail labels as they are', () => {
		expect(row(change('changed', 'name', 'a', 'b'), t, fmt).wrap).toBe(true);
		expect(row(change('changed', 'description', 'a', 'b'), t, fmt).wrap).toBe(true);
		expect(row(change('changed', 'details.Opmerking', null, 'Bring a laptop'), t, fmt)).toMatchObject({
			label: 'Opmerking',
			before: t.changes.none,
			after: 'Bring a laptop',
			wrap: true
		});
	});
});

describe('build', () => {
	const data = (more: Partial<HistoryData> = {}): HistoryData => ({
		changeId: 1,
		calendarId: 12,
		calendarName: 'Class 3B',
		current: {
			state: 'present',
			event: { ...event, fields: { location: 'B2.14', rooms: ['B2.14', 'B2.15'], teachers: ['ABC'], groups: ['3B'], type: 'lesson', details: { Lecturer: 'Jansen' } } },
			removedAt: null
		},
		historyStartsAt: '2026-03-02T07:15:00Z',
		retentionDays: 365,
		items: [change('changed', 'name', 'a', 'b')],
		...more
	});

	it('shapes the current details as a calendar event', () => {
		const h = build(data(), t, fmt);
		expect(h.name).toBe('Optics');
		expect(h.current.tile).toEqual({ month: 'Jan', day: '6', weekday: 'Wed', full: 'Wed 6 Jan 2027' });
		expect([h.current.when, h.current.location, h.current.people]).toEqual(['09:00–10:30', 'B2.14', ['ABC', '3B', 'lesson']]);
		expect(h.current.more).toEqual([
			{ label: 'Status', value: 'confirmed' },
			{ label: 'Rooms', value: 'B2.14, B2.15' },
			{ label: 'Lecturer', value: 'Jansen' },
			{ label: 'Timetable', value: 'Class 3B' }
		]);
		expect([h.historyStart, h.retention, h.calendarName]).toEqual(['Mon 2 Mar', '1 year', 'Class 3B']);
	});

	it('shortens long lists in the summary and moves the full lists to More details', () => {
		const rooms = ['R1', 'R2', 'R3', 'R4', 'R5'];
		const groups = Array.from({ length: 20 }, (_, i) => `G${i + 1}`);
		const h = build(data({ current: { state: 'present', event: { ...event, fields: { rooms, teachers: ['A', 'B', 'C', 'D'], groups } }, removedAt: null } }), t, fmt);
		// Four items stay whole: "and 1 more" would save nothing.
		expect([h.current.location, h.current.people]).toEqual(['R1, R2, R3 and 2 more', ['A, B, C, D', 'G1, G2, G3 and 17 more']]);
		expect(h.current.more.slice(1, 3)).toEqual([
			{ label: 'Rooms', value: rooms.join(', ') },
			{ label: 'Groups', value: groups.join(', ') }
		]);
	});

	it('gives the removal time only for a removed lesson, and falls back to the entry name without an event', () => {
		const removed = build(data({ current: { state: 'removed', event, removedAt: '2026-11-12T11:00:00Z' } }), t, fmt);
		expect(removed.current.removedAt?.text).toBe('1 hour ago');
		const gone = build(data({ current: { state: 'gone', event: null, removedAt: null }, historyStartsAt: null }), t, fmt);
		expect([gone.current.state, gone.current.removedAt, gone.current.tile, gone.name, gone.historyStart]).toEqual(['gone', null, null, 'Optics', null]);
	});
});
