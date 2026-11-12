<script lang="ts">
	import { goto, preloadData, pushState } from '$app/navigation';
	import { page } from '$app/state';
	import ChangeGroup from './ChangeGroup.svelte';
	import HistoryCard from './HistoryCard.svelte';
	import { dicts } from '$lib/i18n';
	import { opensCard } from '$lib/history';
	import type { ChangeGroup as Group } from '$lib/server/changes';

	let { groups }: { groups: Group[] } = $props();
	const t = $derived(dicts[page.data.lang]);

	// A plain click opens the history as a card over the feed (shallow routing); anything else, and any failed load, is
	// a normal visit to /changes/{id} (010 research R8).
	let opener: HTMLAnchorElement | null = null;
	async function open(e: MouseEvent) {
		if (!opensCard(e)) return;
		e.preventDefault();
		const link = e.currentTarget as HTMLAnchorElement;
		opener = link;
		const href = link.getAttribute('href')!;
		const r = await preloadData(href);
		if (r.type === 'loaded' && r.status === 200) {
			const d = r.data as NonNullable<App.PageState['history']>;
			pushState(href, { history: { history: d.history, openedId: d.openedId, all: d.all } });
		} else await goto(href);
	}

	// FR-005a: focus goes back to the change that opened the card.
	let wasOpen = false;
	$effect(() => {
		const isOpen = !!page.state.history;
		if (wasOpen && !isOpen) opener?.focus();
		wasOpen = isOpen;
	});
</script>

{#if groups.length}
	<div class="feed">
		{#each groups as group (group.key)}<ChangeGroup {group} onopen={open} />{/each}
	</div>
{:else}
	<p class="empty">{t.changes.empty}</p>
{/if}
{#if page.state.history}<HistoryCard data={page.state.history} />{/if}

<style>
	.feed {
		box-shadow: inset 0 0 0 1px var(--border-subtle);
		display: flex;
		flex-direction: column;
		gap: 0;
	}
	.empty {
		padding: var(--space-8) var(--space-4);
		text-align: center;
		background: var(--surface-subtle);
		color: var(--grey-600);
	}
</style>
