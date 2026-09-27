<script lang="ts">
	import CircleCheck from '@lucide/svelte/icons/circle-check';
	import CirclePlus from '@lucide/svelte/icons/circle-plus';
	import CircleX from '@lucide/svelte/icons/circle-x';
	import Clock from '@lucide/svelte/icons/clock';
	import MapPin from '@lucide/svelte/icons/map-pin';
	import Pencil from '@lucide/svelte/icons/pencil';
	import { page } from '$app/state';
	import Badge from './Badge.svelte';
	import { dicts } from '$lib/i18n';
	import type { ChangeGroup } from '$lib/server/changes';

	// The design's ChangeNotice, for a group of changes to one event (research R11, R18). Not clickable, no unread dot.
	let { group }: { group: ChangeGroup } = $props();
	const t = $derived(dicts[page.data.lang]);
	const icons = {
		'plus-circle': CirclePlus,
		'x-circle': CircleX,
		'map-pin': MapPin,
		clock: Clock,
		'check-circle': CircleCheck,
		pencil: Pencil
	};
	const GroupIcon = $derived(icons[group.icon]);
	const quiet = $derived(group.items.every((i) => i.outcome !== 'normal'));
</script>

<article class="change {group.tone}" class:quiet class:hidden={group.items.every((i) => i.outcome === 'hidden')}>
	<span class="icon" aria-hidden="true"><GroupIcon size={20} /></span>
	<div class="body">
		<div class="head">
			<h3>{group.eventName}</h3>
			<span class="when">{group.when}</span>
		</div>
		{#if group.calendarName}<span class="calendar">{group.calendarName}</span>{/if}
		<ul>
			{#each group.items as item, i (i)}
				<li class={item.outcome}>
					<span>{item.sentence}</span>
					{#if item.outcome === 'lowPriority'}<Badge>{t.changes.lowPriority}</Badge>{/if}
					{#if item.outcome === 'hidden'}<Badge>{t.changes.hidden}</Badge>{/if}
				</li>
			{/each}
		</ul>
	</div>
</article>

<style>
	.change {
		display: flex;
		gap: 14px;
		padding: var(--space-4);
		background: var(--surface-card);
		border-bottom: 1px solid var(--border-subtle);
	}
	.icon {
		width: 40px;
		height: 40px;
		flex: none;
		border-radius: 50%;
		display: inline-flex;
		align-items: center;
		justify-content: center;
	}
	.cancelled .icon {
		background: var(--status-cancelled-soft);
		color: var(--status-cancelled);
	}
	.moved .icon {
		background: var(--status-moved-soft);
		color: var(--amber-700);
	}
	.new .icon {
		background: var(--status-new-soft);
		color: var(--green-700);
	}
	.changed .icon {
		background: var(--status-info-soft);
		color: var(--status-info);
	}
	.quiet .icon {
		background: var(--grey-100);
		color: var(--grey-600);
	}
	.change.hidden {
		border: 1px dashed var(--border-strong);
		background: var(--surface-subtle);
	}
	.body {
		flex: 1;
		min-width: 0;
		display: flex;
		flex-direction: column;
		gap: 3px;
	}
	.head {
		display: flex;
		gap: var(--space-2);
		align-items: baseline;
		flex-wrap: wrap;
	}
	h3 {
		flex: 1;
		min-width: 0;
		font: 700 var(--fs-16) / 1.3 var(--font-body);
		overflow-wrap: anywhere;
	}
	.when {
		font: var(--type-small);
		color: var(--text-muted);
		white-space: nowrap;
	}
	.change.hidden .when,
	.change.hidden .calendar {
		color: var(--grey-600);
	}
	.calendar {
		font: var(--type-small);
		color: var(--text-muted);
	}
	ul {
		list-style: none;
		margin: var(--space-1) 0 0;
		padding: 0;
		display: flex;
		flex-direction: column;
		gap: var(--space-1);
	}
	li {
		display: flex;
		flex-wrap: wrap;
		align-items: center;
		gap: var(--space-2);
		font: var(--type-small);
		color: var(--text-body);
		overflow-wrap: anywhere;
	}
	li.lowPriority {
		color: var(--text-muted);
	}
	li.hidden {
		color: var(--grey-600);
	}
</style>
