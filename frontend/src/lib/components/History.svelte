<script lang="ts">
	import ChevronDown from '@lucide/svelte/icons/chevron-down';
	import { replaceState } from '$app/navigation';
	import { page } from '$app/state';
	import Badge from './Badge.svelte';
	import { dicts } from '$lib/i18n';
	import { historyHref, historyView, type HistoryEntry, type HistoryPage } from '$lib/history';

	// The history of one lesson, shared by the card and the full page (010 contracts/pages.md § Shared content).
	let { history, openedId, all, cap = null }: { history: HistoryPage; openedId: number; all: boolean | null; cap?: number | null } =
		$props();

	const t = $derived(dicts[page.data.lang]);
	const h = $derived(t.history);
	// The toggle writes the address with replaceState, so it needs no request and adds no history step (FR-014a).
	const effectiveAll = $derived(page.state.historyAll ?? all);
	const view = $derived(historyView(history.entries, { all: effectiveAll, openedId, cap }));
	const pageHref = $derived(historyHref(openedId, effectiveAll));
	const c = $derived(history.current);
	let open = $state(false);
	const id = $props.id();

	function toggle(e: Event & { currentTarget: HTMLInputElement }) {
		const on = e.currentTarget.checked;
		const url = new URL(location.href);
		url.searchParams.set('all', on ? '1' : '0');
		replaceState(url, { ...page.state, historyAll: on });
	}

	const isOpened = (e: HistoryEntry) => e.ids.includes(openedId);
	const bar = (e: HistoryEntry) =>
		e.outcome === 'hidden' ? 'hidden' : e.outcome === 'lowPriority' ? 'low' : e.kind === 'added' ? 'new' : e.kind === 'removed' ? 'removed' : 'changed';
	const kindTone = { added: 'green', removed: 'red', changed: 'blue' } as const;
</script>

<section class="current {c.state}" aria-labelledby="{id}-current">
	<h2 id="{id}-current" class="label">{c.state === 'present' ? h.current : h.lastKnown}</h2>
	<div class="event">
		{#if c.tile}
			<div class="tile" aria-hidden="true">
				<span class="month">{c.tile.month}</span>
				<span class="day">{c.tile.day}</span>
				<span class="weekday">{c.tile.weekday}</span>
			</div>
		{/if}
		<div class="block">
			{#if c.tile}<span class="visually-hidden">{c.tile.full}</span>{/if}
			<div class="name-line">
				<strong class="name">{c.name}</strong>
				{#if c.state === 'removed'}<Badge tone="red">{h.removed}</Badge>{/if}
				{#if c.state === 'gone'}<Badge>{h.gone}</Badge>{/if}
			</div>
			{#if c.when || c.location}<span>{[c.when, c.location].filter(Boolean).join(' · ')}</span>{/if}
			{#if c.people.length}<span class="muted">{c.people.join(' · ')}</span>{/if}
			{#if c.removedAt}<span class="muted"><time datetime={c.removedAt.iso} title={c.removedAt.exact}>{h.removedAt(c.removedAt.text)}</time></span>{/if}
		</div>
	</div>
	<button type="button" class="disclosure" aria-expanded={open} aria-controls="{id}-more" onclick={() => (open = !open)}>
		<ChevronDown size={18} aria-hidden="true" />
		{open ? h.fewerDetails : h.moreDetails}
	</button>
	<dl id="{id}-more" hidden={!open}>
		{#each c.more as d, i (i)}
			<dt>{d.label}</dt>
			<dd>{d.value}</dd>
		{/each}
	</dl>
</section>

<section class="changes" aria-labelledby="{id}-changes">
	<div class="heading">
		<h2 id="{id}-changes">{h.heading}</h2>
		<div class="controls">
			{#if view.toggle}
				<label class="toggle">
					<input type="checkbox" checked={view.showAll} onchange={toggle} />
					{h.showAll}
				</label>
			{/if}
			{#if view.allHidden}
				<span class="message">{h.allHidden}</span>
			{:else if view.openedHidden}
				<span class="message">{h.openedHidden}</span>
			{:else if view.hiddenCount > 0}
				<span class="message">{h.hiddenCount(view.hiddenCount)}</span>
			{/if}
		</div>
	</div>

	{#if view.openedBeyondCap}
		<p class="note"><a href={pageHref} data-sveltekit-reload>{h.furtherBack}</a></p>
	{/if}

	<ol class="entries">
		{#each view.shown as e (e.key)}
			<li
				class="entry {bar(e)}"
				class:opened={isOpened(e)}
				class:hidden={e.outcome === 'hidden'}
				aria-current={isOpened(e) ? 'true' : undefined}
				data-opened={isOpened(e) ? '' : undefined}
			>
				<div class="entry-head">
					<Badge tone={kindTone[e.kind]}>{h.kinds[e.kind]}</Badge>
					<time datetime={e.found.iso}>{e.found.text}</time>
					{#if e.found.relative}<span class="exact">{e.found.exact}</span>{/if}
					{#if e.outcome === 'hidden'}<Badge>{t.changes.hidden}</Badge>{:else if e.outcome === 'lowPriority' && !e.rows.length}<Badge>{t.changes.lowPriority}</Badge>{/if}
					{#if isOpened(e)}<span class="opened-label">{h.opened}</span>{/if}
				</div>
				<span class="entry-name" class:struck={e.kind === 'removed'}>{e.name}</span>
				{#if e.summary}<p class="summary">{e.summary}</p>{/if}
				{#if e.rows.length}
					<ul class="rows">
						{#each e.rows as r, i (i)}
							<li class={r.outcome}>
								<span class="row-label">{r.label}</span>
								<span class="values" class:free={r.wrap}>
									<span class="before"><span class="visually-hidden">{h.before}</span> <s>{r.before}</s></span>
									<span class="arrow" aria-hidden="true">→</span>
									<span class="after"><span class="visually-hidden">{h.after}</span> {r.after}</span>
								</span>
								{#if r.outcome === 'hidden' && e.outcome !== 'hidden'}<Badge>{t.changes.hidden}</Badge>{/if}
								{#if r.outcome === 'lowPriority'}<Badge>{t.changes.lowPriority}</Badge>{/if}
								{#if r.note}<span class="row-note">{r.note}</span>{/if}
							</li>
						{/each}
					</ul>
				{/if}
			</li>
		{/each}
	</ol>

	{#if view.startsBeforeHistory}
		<p class="note">{history.historyStart ? h.startAt(history.historyStart) : h.start}</p>
	{/if}
	{#if view.more > 0}
		<p class="note"><a href={pageHref} data-sveltekit-reload>{h.more(view.more)}</a></p>
	{/if}
	<p class="note retention">{h.retention(history.retention)}</p>
</section>

<style>
	section + section {
		margin-top: var(--space-6);
	}
	.label,
	h2 {
		font: var(--type-overline);
		letter-spacing: var(--ls-caps);
		text-transform: uppercase;
		color: var(--text-muted);
	}
	.event {
		display: flex;
		gap: var(--space-3);
		margin-top: var(--space-2);
	}
	.tile {
		flex: none;
		width: 52px;
		display: flex;
		flex-direction: column;
		align-items: center;
		border: 1px solid var(--border-default);
		border-radius: var(--radius-md);
		overflow: hidden;
		background: var(--white);
		padding-bottom: var(--space-1);
	}
	.month {
		align-self: stretch;
		text-align: center;
		background: var(--brand);
		color: var(--text-inverse);
		font: var(--type-overline);
		text-transform: uppercase;
		padding: 2px 0;
	}
	.day {
		font: 700 var(--fs-20) / 1.2 var(--font-body);
		color: var(--text-strong);
	}
	.weekday {
		font: var(--type-small);
		color: var(--text-muted);
	}
	.block {
		flex: 1;
		min-width: 0;
		display: flex;
		flex-direction: column;
		gap: 2px;
		padding: var(--space-2) var(--space-3);
		border-radius: var(--radius-md);
		background: var(--brand-soft);
		border-left: 4px solid var(--brand);
		font: var(--type-small);
		color: var(--text-body);
		overflow-wrap: anywhere;
	}
	.name-line {
		display: flex;
		flex-wrap: wrap;
		gap: var(--space-2);
		align-items: center;
	}
	.name {
		font: 700 var(--fs-16) / 1.3 var(--font-body);
		color: var(--text-strong);
	}
	.muted {
		color: var(--text-muted);
	}
	.removed .month,
	.gone .month {
		background: var(--grey-400);
	}
	.removed .block,
	.gone .block {
		background: var(--grey-100);
		border: 1px dashed var(--border-strong);
		border-left-width: 4px;
	}
	.removed .name {
		text-decoration: line-through;
		color: var(--grey-600);
	}
	.disclosure {
		display: inline-flex;
		align-items: center;
		gap: var(--space-1);
		min-height: var(--hit-min);
		padding: 0 var(--space-2) 0 0;
		border: 0;
		background: transparent;
		color: var(--text-link);
		font: var(--type-small);
		font-weight: 700;
		cursor: pointer;
	}
	.disclosure :global(svg) {
		transition: transform var(--dur-fast) var(--ease-standard);
	}
	.disclosure[aria-expanded='true'] :global(svg) {
		transform: rotate(180deg);
	}
	dl {
		display: grid;
		grid-template-columns: minmax(0, max-content) minmax(0, 1fr);
		gap: var(--space-1) var(--space-3);
		margin: 0;
		font: var(--type-small);
	}
	dl[hidden] {
		display: none;
	}
	dt {
		color: var(--text-muted);
		overflow-wrap: anywhere;
	}
	dd {
		margin: 0;
		color: var(--text-body);
		overflow-wrap: anywhere;
		white-space: pre-line;
	}
	.heading {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		justify-content: space-between;
		gap: var(--space-2);
	}
	.controls {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-1) var(--space-3);
	}
	.toggle {
		display: inline-flex;
		align-items: center;
		gap: var(--space-2);
		min-height: var(--hit-min);
		font: var(--type-small);
		font-weight: 700;
		color: var(--text-strong);
		cursor: pointer;
	}
	.toggle input {
		width: 18px;
		height: 18px;
		margin: 0;
		accent-color: var(--brand);
	}
	.message {
		font: var(--type-small);
		color: var(--text-muted);
	}
	.entries {
		list-style: none;
		margin: var(--space-2) 0 0;
		padding: var(--space-3);
		background: var(--surface-subtle);
		border-radius: var(--radius-md);
		display: flex;
		flex-direction: column;
		gap: var(--space-3);
	}
	.entry {
		position: relative;
		display: flex;
		flex-direction: column;
		gap: var(--space-1);
		padding: var(--space-3) var(--space-3) var(--space-3) calc(var(--space-3) + 4px);
		background: var(--white);
		border: 1px solid var(--border-default);
		border-radius: 8px;
		box-shadow: var(--shadow-1);
		overflow: hidden;
		scroll-margin: var(--space-8);
	}
	.entry::before {
		content: '';
		position: absolute;
		inset: 0 auto 0 0;
		width: 4px;
		background: var(--status-info);
	}
	.entry.new::before {
		background: var(--status-new);
	}
	.entry.removed::before {
		background: var(--status-cancelled);
	}
	.entry.low::before,
	.entry.hidden::before {
		background: var(--grey-400);
	}
	.entry.hidden {
		background: var(--grey-100);
		border: 1px dashed var(--border-strong);
		box-shadow: none;
	}
	.entry.opened {
		background: var(--brand-soft);
		border: 2px solid var(--brand);
		padding-left: calc(var(--space-3) + 6px);
	}
	.entry.opened::before {
		width: 6px;
		background: var(--brand);
	}
	.entry.opened.removed {
		background: var(--grey-100);
	}
	.entry.opened.removed::before {
		background: var(--status-cancelled);
	}
	.entry-head {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-1) var(--space-2);
		font: var(--type-small);
		color: var(--text-muted);
	}
	.exact {
		color: var(--text-muted);
	}
	.exact::before {
		content: '· ';
	}
	.opened-label {
		margin-left: auto;
		color: var(--text-brand);
		font-weight: 700;
	}
	.entry-name {
		font: 700 var(--fs-16) / 1.3 var(--font-body);
		color: var(--text-strong);
		overflow-wrap: anywhere;
	}
	.entry-name.struck {
		text-decoration: line-through;
		color: var(--grey-600);
	}
	.summary {
		font: var(--type-small);
		color: var(--text-body);
	}
	.rows {
		list-style: none;
		margin: var(--space-1) 0 0;
		padding: 0;
		display: flex;
		flex-direction: column;
		gap: var(--space-2);
	}
	.rows > li {
		display: flex;
		flex-wrap: wrap;
		align-items: baseline;
		gap: var(--space-1) var(--space-2);
		font: var(--type-small);
	}
	.row-label {
		flex: none;
		width: 84px;
		color: var(--text-muted);
		overflow-wrap: anywhere;
	}
	.values {
		flex: 1;
		min-width: 0;
		display: flex;
		flex-wrap: wrap;
		align-items: baseline;
		gap: var(--space-1);
	}
	.before,
	.after {
		padding: 1px 6px;
		border-radius: var(--radius-sm);
		white-space: nowrap;
	}
	.values.free .before,
	.values.free .after {
		white-space: normal;
		overflow-wrap: anywhere;
	}
	.before {
		background: var(--red-100);
		color: var(--red-500);
	}
	.after {
		background: var(--green-100);
		color: var(--green-700);
		font-weight: 700;
	}
	.arrow {
		color: var(--text-muted);
	}
	.row-note {
		flex-basis: 100%;
		padding-left: calc(84px + var(--space-2));
		color: var(--text-muted);
	}
	.rows > li.hidden .before,
	.rows > li.hidden .after {
		opacity: 0.75;
	}
	.note {
		margin-top: var(--space-3);
		font: var(--type-small);
		color: var(--text-muted);
	}
	@media (max-width: 480px) {
		.row-note {
			padding-left: 0;
		}
	}
</style>
