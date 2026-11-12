import type { RequestEvent } from '@sveltejs/kit';
import type { Dict } from '$lib/i18n';
import { mildest, type CurrentDetails, type HistoryEntry, type HistoryPage, type HistoryRow } from '$lib/history';
import { api, must } from './api';
import { describe, list, span, statusWord, text, typeWord, type Change } from './changes';
import { formatter, type Formatter } from './format';

// The history of one lesson (010): GET /api/me/changes/{id}/history, built into entries and texts on the server like
// the feed. The toggle and cap run in the component (lib/history.ts).

type EventObject = { name?: string; start?: string; end?: string; status?: string | null; fields?: Record<string, unknown> };
export type HistoryData = {
	changeId: number;
	calendarId: number;
	calendarName: string;
	current: { state: 'present' | 'removed' | 'gone'; event: EventObject | null; removedAt: string | null };
	historyStartsAt: string | null;
	retentionDays: number;
	/** Newest first. */
	items: Change[];
};

const joined = (v: unknown, t: Dict) => (Array.isArray(v) ? (v.length ? list(v).join(', ') : t.changes.none) : text(v, t));
const nonEmpty = (v: unknown) => !(v === null || v === undefined || v === '' || (Array.isArray(v) && v.length === 0));

/** One row of a changed entry (research R6). */
export function row(c: Change, t: Dict, fmt: Formatter): HistoryRow {
	const field = c.field ?? '';
	const labels = t.history.labels;
	const base = { note: null, wrap: false, outcome: c.outcome };
	switch (field) {
		case 'time': {
			const [a, b] = [span(c.oldValue), span(c.newValue)];
			if (!(a.start && a.end && b.start && b.end)) return { ...base, label: labels.time, before: t.changes.none, after: t.changes.none };
			const days = [a.start, a.end, b.start, b.end].map(fmt.date);
			if (days.every((d) => d === days[0]))
				return {
					...base,
					label: labels.time,
					before: `${fmt.time(a.start)}–${fmt.time(a.end)}`,
					after: `${fmt.time(b.start)}–${fmt.time(b.end)}`,
					note: t.history.sameDay(days[0])
				};
			return { ...base, label: labels.time, before: fmt.range(a.start, a.end), after: fmt.range(b.start, b.end) };
		}
		case 'status':
			return { ...base, label: labels.status, before: statusWord(c.oldValue, t), after: statusWord(c.newValue, t) };
		case 'type':
			return { ...base, label: labels.type, before: typeWord(c.oldValue, t), after: typeWord(c.newValue, t) };
		case 'rooms':
		case 'teachers':
		case 'groups':
		case 'location':
			return { ...base, label: labels[field], before: joined(c.oldValue, t), after: joined(c.newValue, t) };
		case 'name':
		case 'description':
			return { ...base, wrap: true, label: labels[field], before: text(c.oldValue, t), after: text(c.newValue, t) };
	}
	const label = field.startsWith('details.') ? field.slice('details.'.length) : field;
	return { ...base, wrap: true, label, before: joined(c.oldValue, t), after: joined(c.newValue, t) };
}

/** Rows found in one check form one entry, rows in id order, entries newest first (research R6). */
export function entries(items: Change[], t: Dict, fmt: Formatter): HistoryEntry[] {
	const byCheck = new Map<string, Change[]>();
	for (const c of items) byCheck.set(c.detectedAt, [...(byCheck.get(c.detectedAt) ?? []), c]);
	return [...byCheck.values()]
		.map((rows) => rows.toSorted((a, b) => a.id - b.id))
		.map((rows): HistoryEntry => {
			const added = rows.find((c) => c.kind === 'added');
			const removed = rows.find((c) => c.kind === 'removed');
			const kind = added ? 'added' : removed ? 'removed' : 'changed';
			const outcomes = rows.map((c) => c.outcome);
			return {
				key: `${rows[0].detectedAt} ${rows[0].id}`,
				ids: rows.map((c) => c.id),
				outcomes,
				kind,
				found: fmt.found(rows[0].detectedAt),
				name: rows[0].eventName,
				outcome: mildest(outcomes),
				summary: added ? describe(added, t, fmt) : removed ? describe(removed, t, fmt) : null,
				rows: kind === 'changed' ? rows.map((c) => row(c, t, fmt)) : []
			};
		})
		.toSorted((a, b) => Math.max(...b.ids) - Math.max(...a.ids));
}

/** The lesson as a calendar event (FR-007, FR-008, contracts/pages.md § Current details). */
export function current(data: HistoryData, fallbackName: string, t: Dict, fmt: Formatter): CurrentDetails {
	const e = data.current.event ?? {};
	const f = e.fields ?? {};
	const labels = t.history.labels;
	const location = nonEmpty(f.location) ? text(f.location, t) : nonEmpty(f.rooms) ? list(f.rooms).join(', ') : null;
	const when = e.start && e.end ? (fmt.date(e.start) === fmt.date(e.end) ? `${fmt.time(e.start)}–${fmt.time(e.end)}` : fmt.range(e.start, e.end)) : null;
	const people = [
		nonEmpty(f.teachers) ? list(f.teachers).join(', ') : null,
		nonEmpty(f.groups) ? list(f.groups).join(', ') : null,
		nonEmpty(f.type) ? typeWord(f.type, t) : null
	].filter((x): x is string => x !== null);
	const more: { label: string; value: string }[] = [];
	if (nonEmpty(e.status)) more.push({ label: labels.status, value: statusWord(e.status, t) });
	if (nonEmpty(f.description)) more.push({ label: labels.description, value: text(f.description, t) });
	if (nonEmpty(f.rooms) && list(f.rooms).join(', ') !== location) more.push({ label: labels.rooms, value: list(f.rooms).join(', ') });
	const details = f.details && typeof f.details === 'object' ? (f.details as Record<string, unknown>) : {};
	for (const [label, value] of Object.entries(details)) if (nonEmpty(value)) more.push({ label, value: joined(value, t) });
	more.push({ label: labels.calendar, value: data.calendarName });
	return {
		state: data.current.state,
		name: e.name ?? fallbackName,
		tile: e.start ? fmt.tile(e.start) : null,
		when,
		location,
		people,
		more,
		removedAt: data.current.state === 'removed' && data.current.removedAt ? fmt.found(data.current.removedAt) : null
	};
}

export function build(data: HistoryData, t: Dict, fmt: Formatter): HistoryPage {
	const list = entries(data.items, t, fmt);
	const cur = current(data, list[0]?.name ?? '', t, fmt);
	return {
		changeId: data.changeId,
		calendarName: data.calendarName,
		name: cur.name,
		current: cur,
		entries: list,
		historyStart: data.historyStartsAt ? fmt.date(data.historyStartsAt) : null,
		retention: fmt.retention(data.retentionDays)
	};
}

/** The history for a page or card. A 404 is the same "not found" page for every cause (FR-016). */
export async function loadHistory(event: RequestEvent, id: string): Promise<HistoryPage> {
	const { t, lang, tz } = event.locals;
	const data = must(event, await api<HistoryData>(event, 'GET', `/api/me/changes/${id}/history`));
	return build(data, t, formatter(lang, tz));
}
