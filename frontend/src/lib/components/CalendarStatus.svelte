<script lang="ts">
	import { page } from '$app/state';
	import Badge from './Badge.svelte';
	import { dicts } from '$lib/i18n';
	import type { CalendarView } from '$lib/server/changes';

	// A calendar's check status (US2-4). With `href`, the name links to the calendar's page.
	let { calendar, href }: { calendar: CalendarView; href?: string } = $props();
	const t = $derived(dicts[page.data.lang]);
	const tones = { ok: 'green', lastFailed: 'amber', failing: 'red', notChecked: 'neutral' } as const;
</script>

<article class="calendar" class:link={href}>
	<div class="head">
		<h3>{#if href}<a {href}>{calendar.name}</a>{:else}{calendar.name}{/if}</h3>
		<Badge tone={tones[calendar.state]}>{t.calendars.states[calendar.state]}</Badge>
	</div>
	{#if calendar.stateText}<p class="reason">{calendar.stateText}</p>{/if}
	{#if calendar.state === 'failing'}<p class="reason">{t.calendars.failures(calendar.failures)}</p>{/if}
	<dl>
		{#if calendar.lastChecked}
			<div><dt>{t.calendars.lastChecked}</dt><dd>{calendar.lastChecked}</dd></div>
		{/if}
		<div><dt>{t.calendars.nextCheck}</dt><dd>{calendar.nextCheck}</dd></div>
		{#if !calendar.notify}<div><dt>{t.calendars.notifications}</dt><dd>{t.calendars.notifyOff}</dd></div>{/if}
	</dl>
</article>

<style>
	.calendar {
		position: relative;
		box-shadow: inset 0 0 0 1px var(--border-subtle);
		border-radius: var(--radius-sm);
		padding: var(--space-5);
		display: flex;
		flex-direction: column;
		gap: var(--space-2);
		background: var(--surface-card);
		transition: box-shadow var(--dur-base);
	}
	.calendar.link:hover {
		box-shadow: var(--shadow-2);
	}
	.head {
		display: flex;
		gap: var(--space-3);
		align-items: flex-start;
		justify-content: space-between;
		flex-wrap: wrap;
	}
	h3 {
		font: var(--type-h4);
		min-width: 0;
		overflow-wrap: anywhere;
	}
	h3 a {
		color: var(--text-strong);
		text-decoration: none;
	}
	h3 a::after {
		content: '';
		position: absolute;
		inset: 0;
	}
	h3 a:hover {
		text-decoration: underline;
	}
	.reason {
		color: var(--text-body);
	}
	dl {
		margin: 0;
		display: flex;
		flex-wrap: wrap;
		gap: var(--space-1) var(--space-6);
		font: var(--type-small);
	}
	dt {
		color: var(--text-muted);
	}
	dd {
		margin: 0;
		font-family: var(--font-body);
		color: var(--text-strong);
	}
</style>
