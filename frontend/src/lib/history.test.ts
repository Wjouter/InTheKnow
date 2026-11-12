import { describe, expect, it } from 'vitest';
import { historyHref, historyView, opensCard, parseAll, parseId, type HistoryEntry, type Outcome } from './history';

let n = 0;
/** One entry with one row per outcome, newest first in the arrays below. */
const entry = (outcomes: Outcome[], kind: HistoryEntry['kind'] = 'changed'): HistoryEntry => {
	const ids = outcomes.map(() => ++n);
	return {
		key: `k${ids[0]}`,
		ids,
		outcomes,
		kind,
		found: { text: '', exact: '', iso: '', relative: true },
		name: 'Optics',
		outcome: outcomes.includes('normal') ? 'normal' : outcomes.includes('lowPriority') ? 'lowPriority' : 'hidden',
		summary: null,
		rows: kind === 'changed' ? outcomes.map((outcome) => ({ label: 'Name', before: 'a', after: 'b', note: null, wrap: true, outcome })) : []
	};
};
const view = (entries: HistoryEntry[], openedId: number, all: boolean | null = null, cap: number | null = null) =>
	historyView(entries, { all, openedId, cap });

describe('historyView: opened change not hidden (research R7 row 1)', () => {
	const make = () => [entry(['normal']), entry(['hidden', 'normal']), entry(['hidden']), entry(['normal'], 'added')];

	it('drops hidden rows and entries with none left, and counts them', () => {
		for (const all of [null, false]) {
			const es = make();
			const v = view(es, es[0].ids[0], all);
			expect(v.shown.map((e) => e.key)).toEqual([es[0].key, es[1].key, es[3].key]);
			expect(v.shown[1].rows.map((r) => r.outcome)).toEqual(['normal']);
			expect([v.hiddenCount, v.showAll, v.toggle, v.openedHidden, v.allHidden]).toEqual([2, false, true, false, false]);
		}
	});

	it('caps after the filter and says when the opened entry is further back', () => {
		const es = make();
		const v = view(es, es[3].ids[0], null, 2);
		expect(v.shown.map((e) => e.key)).toEqual([es[0].key, es[1].key]);
		expect([v.more, v.openedBeyondCap]).toEqual([1, true]);
		expect(view(es, es[0].ids[0], null, 2).openedBeyondCap).toBe(false);
		expect(view(es, es[0].ids[0], null, null)).toMatchObject({ more: 0, openedBeyondCap: false });
	});

	it('says the history starts before the oldest entry unless that entry is added (FR-011)', () => {
		expect(view([entry(['normal']), entry(['normal'], 'added')], n).startsBeforeHistory).toBe(false);
		expect(view([entry(['normal']), entry(['normal'])], n).startsBeforeHistory).toBe(true);
		// In the baseline, removed, then back: the reappearance is not the oldest entry.
		const back = [entry(['normal'], 'added'), entry(['normal'], 'removed')];
		expect(view(back, back[0].ids[0]).startsBeforeHistory).toBe(true);
		// Judged over every kept entry: a hidden oldest added entry still counts with the toggle off.
		const es = [entry(['normal']), entry(['hidden'], 'added')];
		const v = view(es, es[0].ids[0]);
		expect([v.shown.length, v.startsBeforeHistory]).toEqual([1, false]);
	});
});

// T027: the remaining rows of research R7's table.
describe('historyView: the toggle states (FR-014, FR-014a, FR-014b)', () => {
	it('shows everything with all=1', () => {
		const es = [entry(['normal']), entry(['hidden'])];
		const v = view(es, es[0].ids[0], true);
		expect([v.shown.length, v.showAll, v.toggle, v.hiddenCount]).toEqual([2, true, true, 0]);
	});

	it('shows everything when the opened change is hidden, unless all=0', () => {
		const es = [entry(['normal']), entry(['hidden'])];
		for (const all of [null, true]) {
			const v = view(es, es[1].ids[0], all);
			expect([v.shown.length, v.showAll, v.toggle, v.openedHidden]).toEqual([2, true, true, false]);
		}
		const off = view(es, es[1].ids[0], false);
		expect([off.shown.length, off.showAll, off.openedHidden, off.hiddenCount]).toEqual([1, false, true, 1]);
	});

	it('offers no toggle when every entry is hidden', () => {
		const es = [entry(['hidden']), entry(['hidden', 'hidden'])];
		for (const all of [null, false, true]) {
			const v = view(es, es[0].ids[0], all);
			expect([v.shown.length, v.showAll, v.toggle, v.allHidden, v.openedHidden]).toEqual([2, true, false, true, false]);
		}
	});

	it('counts hidden entries toward the cap with the toggle on', () => {
		const es = [entry(['hidden']), entry(['normal']), entry(['normal'])];
		const v = view(es, es[1].ids[0], true, 2);
		expect([v.shown.map((e) => e.key), v.more]).toEqual([[es[0].key, es[1].key], 1]);
	});
});

describe('addresses (contracts/pages.md § Addresses)', () => {
	it('accepts only a positive digit id', () => {
		expect(parseId('4711')).toBe('4711');
		expect(parseId('999999999999999999')).toBe('999999999999999999');
		for (const bad of ['0', '0123', '-1', '1e3', '12a', '', '1234567890123456789', ' 1', undefined]) expect(parseId(bad)).toBeNull();
	});

	it('reads all as 0 or 1 only', () => {
		expect([parseAll('1'), parseAll('0')]).toEqual([true, false]);
		for (const other of [null, '', 'true', '2', 'yes']) expect(parseAll(other)).toBeNull();
	});

	it('keeps the toggle in the address only when it is set', () => {
		expect([historyHref(5, null), historyHref(5, true), historyHref(5, false)]).toEqual(['/changes/5', '/changes/5?all=1', '/changes/5?all=0']);
	});
});

describe('opensCard (FR-005b)', () => {
	const click = { button: 0, ctrlKey: false, metaKey: false, shiftKey: false, altKey: false, defaultPrevented: false };
	it('opens the card only for a plain primary click', () => {
		expect(opensCard(click)).toBe(true);
		for (const k of ['ctrlKey', 'metaKey', 'shiftKey', 'altKey', 'defaultPrevented'] as const) expect(opensCard({ ...click, [k]: true })).toBe(false);
		expect(opensCard({ ...click, button: 1 })).toBe(false);
	});
});
