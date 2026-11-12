// The history view of one lesson (010): the shapes the server builds and the pure view rules the components run, so the
// filter toggle needs no request (research R7). Client-safe: nothing here may import from $lib/server.

export type Outcome = 'normal' | 'lowPriority' | 'hidden';
/** When something was found: `text` is relative under 24 hours (`relative`), `exact` always has the year. */
export type Found = { text: string; exact: string; iso: string; relative: boolean };

/** One changed detail of an entry: `label · before → after` (research R6). */
export type HistoryRow = { label: string; before: string; after: string; note: string | null; wrap: boolean; outcome: Outcome };

/** All rows of one check, in id order (research R6, data-model.md § History entry). */
export type HistoryEntry = {
	key: string;
	ids: number[];
	/** The outcome of each row, in `ids` order. */
	outcomes: Outcome[];
	kind: 'added' | 'removed' | 'changed';
	found: Found;
	name: string;
	/** The mildest row outcome: hidden only when every row is hidden. */
	outcome: Outcome;
	/** The sentence of an added or removed entry. */
	summary: string | null;
	rows: HistoryRow[];
};

export type CurrentDetails = {
	state: 'present' | 'removed' | 'gone';
	name: string;
	tile: { month: string; day: string; weekday: string; full: string } | null;
	/** Line 2: start–end · location. */
	when: string | null;
	location: string | null;
	/** Line 3: teachers · groups · type. */
	people: string[];
	more: { label: string; value: string }[];
	removedAt: Found | null;
};

export type HistoryPage = {
	changeId: number;
	calendarName: string;
	name: string;
	current: CurrentDetails;
	/** Newest first. */
	entries: HistoryEntry[];
	historyStart: string | null;
	retention: string;
};

export const mildest = (outcomes: Outcome[]): Outcome =>
	outcomes.includes('normal') ? 'normal' : outcomes.includes('lowPriority') ? 'lowPriority' : 'hidden';

/** `{id}` of /changes/{id}: digits only, no leading zero (contracts/pages.md § Addresses). */
export const parseId = (v: string | null | undefined): string | null => (v && /^[1-9]\d{0,17}$/.test(v) ? v : null);

/** `all`: 1 forces show all, 0 forces filtered, anything else is the default rule (research R7). */
export const parseAll = (v: string | null | undefined): boolean | null => (v === '1' ? true : v === '0' ? false : null);

/** The address of a history, keeping the toggle when it is set. */
export const historyHref = (id: number, all: boolean | null) => `/changes/${id}${all === null ? '' : `?all=${all ? 1 : 0}`}`;

export type HistoryView = {
	showAll: boolean;
	/** False when every entry is hidden: no toggle is offered. */
	toggle: boolean;
	shown: HistoryEntry[];
	more: number;
	hiddenCount: number;
	openedHidden: boolean;
	allHidden: boolean;
	openedBeyondCap: boolean;
	startsBeforeHistory: boolean;
};

/** Research R7's table, the FR-009a cap and the FR-011 rule. `entries` are newest first. */
export function historyView(entries: HistoryEntry[], opts: { all: boolean | null; openedId: number; cap: number | null }): HistoryView {
	const opened = entries.find((e) => e.ids.includes(opts.openedId));
	const openedRowHidden = opened ? opened.outcomes[opened.ids.indexOf(opts.openedId)] === 'hidden' : false;
	const allHidden = entries.length > 0 && entries.every((e) => e.outcome === 'hidden');
	const showAll = allHidden || (openedRowHidden ? opts.all !== false : opts.all === true);

	let hiddenCount = 0;
	const filtered = showAll
		? entries
		: entries.flatMap((e) => {
				const dropped = e.outcomes.filter((o) => o === 'hidden').length;
				hiddenCount += dropped;
				if (e.outcome === 'hidden') return [];
				return dropped ? [{ ...e, rows: e.rows.filter((r) => r.outcome !== 'hidden') }] : [e];
			});
	const shown = opts.cap === null ? filtered : filtered.slice(0, opts.cap);
	const openedAt = opened ? filtered.findIndex((e) => e.key === opened.key) : -1;
	return {
		showAll,
		toggle: !allHidden,
		shown,
		more: filtered.length - shown.length,
		hiddenCount,
		openedHidden: openedRowHidden && !showAll,
		allHidden,
		openedBeyondCap: openedAt >= shown.length,
		// Judged over every kept entry, so a reappearance is never shown as the first appearance (FR-011, research R4).
		startsBeforeHistory: entries.length > 0 && entries[entries.length - 1].kind !== 'added'
	};
}

/** A plain primary click opens the card; anything else is left to the browser (FR-005b, research R8). */
export const opensCard = (e: {
	button: number;
	ctrlKey: boolean;
	metaKey: boolean;
	shiftKey: boolean;
	altKey: boolean;
	defaultPrevented: boolean;
}): boolean => e.button === 0 && !e.ctrlKey && !e.metaKey && !e.shiftKey && !e.altKey && !e.defaultPrevented;
