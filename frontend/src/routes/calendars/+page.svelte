<script lang="ts">
	import AddCalendar from '$lib/components/AddCalendar.svelte';
	import CalendarStatus from '$lib/components/CalendarStatus.svelte';
	import PageIntro from '$lib/components/PageIntro.svelte';
	import { dicts } from '$lib/i18n';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
</script>

<svelte:head><title>{t.calendars.title} · InTheKnow</title></svelte:head>

<PageIntro title={t.calendars.title} intro={t.calendars.intro} />
<div class="wrap layout">
	<section class="stack">
		{#if data.calendars.length}
			{#each data.calendars as calendar (calendar.calendarId)}
				<CalendarStatus {calendar} href="/calendars/{calendar.calendarId}" />
			{/each}
		{:else}
			<p>{t.calendars.emptyIntro}</p>
		{/if}
	</section>
	<aside class="stack">
		<AddCalendar {form} />
		<div class="section">
			<h2><a href="/presets">{t.calendars.presets}</a></h2>
			<p>{t.calendars.presetsHint}</p>
		</div>
	</aside>
</div>

<style>
	.layout {
		display: grid;
		gap: var(--space-8);
		grid-template-columns: minmax(0, 1fr);
	}
	@media (min-width: 900px) {
		.layout {
			grid-template-columns: minmax(0, 1fr) 360px;
			align-items: start;
		}
	}
</style>
