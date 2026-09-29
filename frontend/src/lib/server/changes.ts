import type { RequestEvent } from '@sveltejs/kit';
import { checkFailureMessage } from '$lib/codes';
import type { Dict } from '$lib/i18n';
import { api, must } from './api';
import { formatter, type Formatter } from './format';

// Change sentences, grouping and tones (research R11). Built on the server; components only print the strings.

export type Outcome = 'normal' | 'lowPriority' | 'hidden';
export type Change = {
	id: number;
	calendarId: number;
	eventKey: string;
	kind: 'added' | 'removed' | 'changed';
	field: string | null;
	oldValue: unknown;
	newValue: unknown;
	eventName: string;
	eventStartsAt: string;
	detectedAt: string;
	outcome: Outcome;
};
export type Calendar = {
	calendarId: number;
	status: string;
	lastCheckedAt: string | null;
	lastOk: boolean | null;
	lastError: string | null;
	consecutiveFailures: number;
	nextCheckAt: string;
	notify: boolean;
	label: string | null;
	name: string;
	links: unknown[];
};
export type Tone = 'new' | 'cancelled' | 'moved' | 'changed';
export type Icon = 'plus-circle' | 'x-circle' | 'map-pin' | 'clock' | 'check-circle' | 'pencil';
export type ChangeGroup = {
	key: string;
	eventName: string;
	when: string;
	calendarName: string | null;
	tone: Tone;
	/** The icon of the change that gave the group its tone. */
	icon: Icon;
	items: { sentence: string; outcome: Outcome; icon: Icon }[];
};

const text = (v: unknown, t: Dict) => (v === null || v === undefined || v === '' ? t.changes.none : String(v));
const list = (v: unknown): string[] => (Array.isArray(v) ? v.map(String) : []);
const span = (v: unknown) => (v && typeof v === 'object' ? (v as { start?: string; end?: string }) : {});

function statusWord(v: unknown, t: Dict): string {
	return v === 'confirmed' || v === 'tentative' || v === 'cancelled' ? t.changes.status[v] : text(v, t);
}
function typeWord(v: unknown, t: Dict): string {
	return v === 'lesson' || v === 'test' || v === 'holiday' || v === 'appointment' ? t.changes.types[v] : text(v, t);
}

/** Set fields name what was added and removed, never the whole arrays. */
function setChange(kind: 'rooms' | 'teachers' | 'groups', oldValue: unknown, newValue: unknown, t: Dict): string {
	const before = list(oldValue);
	const after = list(newValue);
	const added = after.filter((x) => !before.includes(x));
	const removed = before.filter((x) => !after.includes(x));
	const words = t.changes.sets[kind];
	if (added.length === 1 && removed.length === 1) return words.replaced(removed[0], added[0]);
	const parts = [];
	if (added.length) parts.push(words.added(added.join(', ')));
	if (removed.length) parts.push(words.removed(removed.join(', ')));
	return parts.length ? parts.join(' ') : words.changed;
}

export function describe(c: Change, t: Dict, fmt: Formatter): string {
	const w = t.changes;
	if (c.kind === 'added') {
		const e = span(c.newValue);
		return e.start && e.end ? w.addedAt(fmt.range(e.start, e.end)) : w.added;
	}
	if (c.kind === 'removed') return w.removed;
	const field = c.field ?? '';
	switch (field) {
		case 'name':
			return w.renamed(text(c.oldValue, t), text(c.newValue, t));
		case 'time': {
			const [a, b] = [span(c.oldValue), span(c.newValue)];
			return a.start && a.end && b.start && b.end ? w.time(fmt.range(a.start, a.end), fmt.range(b.start, b.end)) : w.timeChanged;
		}
		case 'status':
			if (c.newValue === 'cancelled') return w.cancelled;
			if (c.oldValue === 'tentative' && c.newValue === 'confirmed') return w.confirmed;
			return w.statusChanged(statusWord(c.oldValue, t), statusWord(c.newValue, t));
		case 'location':
			return w.location(text(c.oldValue, t), text(c.newValue, t));
		case 'description':
			return w.description;
		case 'type':
			return w.type(typeWord(c.oldValue, t), typeWord(c.newValue, t));
		case 'rooms':
		case 'teachers':
		case 'groups':
			return setChange(field, c.oldValue, c.newValue, t);
		case 'details.text':
			return w.detailsText;
	}
	if (field.startsWith('details.')) return w.detail(field.slice('details.'.length), text(c.oldValue, t), text(c.newValue, t));
	return w.other;
}

export function tone(c: Change): Tone {
	if (c.kind === 'added') return 'new';
	if (c.kind === 'removed' || (c.field === 'status' && c.newValue === 'cancelled')) return 'cancelled';
	if (c.field === 'location' || c.field === 'rooms') return 'moved';
	return 'changed';
}

export function icon(c: Change): Icon {
	if (c.kind === 'added') return 'plus-circle';
	if (tone(c) === 'cancelled') return 'x-circle';
	if (tone(c) === 'moved') return 'map-pin';
	if (c.field === 'time') return 'clock';
	if (c.field === 'status' && c.oldValue === 'tentative' && c.newValue === 'confirmed') return 'check-circle';
	return 'pencil';
}

const severity: Tone[] = ['cancelled', 'moved', 'new', 'changed'];

/** Consecutive changes of one event in one check form one group (001 detected-change "Grouping"). */
export function group(items: Change[], calendars: Calendar[], t: Dict, fmt: Formatter): ChangeGroup[] {
	const names = new Map(calendars.map((c) => [c.calendarId, c.name]));
	const groups: (Omit<ChangeGroup, 'tone' | 'icon'> & { tones: Tone[] })[] = [];
	let last = '';
	for (const c of items) {
		const key = `${c.calendarId} ${c.eventKey} ${c.detectedAt}`;
		if (key !== last || !groups.length) {
			groups.push({
				key: `${key} ${c.id}`,
				eventName: c.eventName,
				when: fmt.dateTime(c.eventStartsAt),
				calendarName: calendars.length > 1 ? (names.get(c.calendarId) ?? null) : null,
				tones: [],
				items: []
			});
			last = key;
		}
		const g = groups[groups.length - 1];
		g.tones.push(tone(c));
		g.items.push({ sentence: describe(c, t, fmt), outcome: c.outcome, icon: icon(c) });
	}
	return groups.map(({ tones, ...g }) => {
		const t = severity.find((s) => tones.includes(s)) ?? 'changed';
		return { ...g, tone: t, icon: g.items[tones.indexOf(t)].icon };
	});
}

export type CalendarView = {
	calendarId: number;
	name: string;
	state: 'ok' | 'lastFailed' | 'failing' | 'notChecked';
	stateText: string;
	lastChecked: string | null;
	nextCheck: string;
	failures: number;
	notify: boolean;
	label: string | null;
	linkCount: number;
};

/** A calendar as pages show it. Never its links or a URL, only how many links there are (FR-023). */
export function calendarView(c: Calendar, t: Dict, fmt: Formatter): CalendarView {
	const state = c.lastCheckedAt === null ? 'notChecked' : c.status === 'failing' ? 'failing' : c.lastOk === false ? 'lastFailed' : 'ok';
	return {
		calendarId: c.calendarId,
		name: c.name,
		state,
		stateText: (state === 'failing' || state === 'lastFailed') && c.lastError ? checkFailureMessage(c.lastError, t) : '',
		lastChecked: c.lastCheckedAt ? fmt.dateTime(c.lastCheckedAt) : null,
		nextCheck: fmt.dateTime(c.nextCheckAt),
		failures: c.consecutiveFailures,
		notify: c.notify,
		label: c.label,
		linkCount: c.links?.length ?? 0
	};
}

export const pageSize = 50;

/** One page of the feed, newest first, with the calendars it needs (the changes page and the home page). */
export async function loadFeed(event: RequestEvent, opts: { before?: number; showHidden?: boolean } = {}) {
	const { t, lang, tz } = event.locals;
	const query = new URLSearchParams({ order: 'newest', limit: String(pageSize) });
	if (opts.before) query.set('before', String(opts.before));
	if (opts.showHidden) query.set('includeHidden', 'true');
	const [changes, calendars] = await Promise.all([
		api<{ items: Change[]; next: number | null }>(event, 'GET', `/api/me/changes?${query}`),
		api<Calendar[]>(event, 'GET', '/api/me/calendars')
	]);
	const page = must(event, changes);
	const cals = must(event, calendars);
	const fmt = formatter(lang, tz);
	return { groups: group(page.items, cals, t, fmt), next: page.next, calendars: cals.map((c) => calendarView(c, t, fmt)) };
}
