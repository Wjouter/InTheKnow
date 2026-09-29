<script lang="ts">
	import { page } from '$app/state';
	import ChangeGroup from './ChangeGroup.svelte';
	import { dicts } from '$lib/i18n';
	import type { ChangeGroup as Group } from '$lib/server/changes';

	let { groups }: { groups: Group[] } = $props();
	const t = $derived(dicts[page.data.lang]);
</script>

{#if groups.length}
	<div class="feed">
		{#each groups as group (group.key)}<ChangeGroup {group} />{/each}
	</div>
{:else}
	<p class="empty">{t.changes.empty}</p>
{/if}

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
