<script lang="ts">
	import AddCalendar from '$lib/components/AddCalendar.svelte';
	import AuthPanel from '$lib/components/AuthPanel.svelte';
	import Button from '$lib/components/Button.svelte';
	import CalendarStatus from '$lib/components/CalendarStatus.svelte';
	import Feed from '$lib/components/Feed.svelte';
	import PageIntro from '$lib/components/PageIntro.svelte';
	import { dicts } from '$lib/i18n';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
</script>

<svelte:head><title>{t.home.title}</title></svelte:head>

{#if data.view === 'visitor'}
	<AuthPanel title={t.home.visitorTitle} intro={t.home.visitorIntro}>
		<ul class="points">
			{#each t.home.visitorPoints as point (point)}<li>{point}</li>{/each}
		</ul>
		<div class="row">
			<Button href="/account/sign-up" size="lg">{t.nav.signUp}</Button>
			<Button href="/sign-in" size="lg" variant="secondary">{t.nav.signIn}</Button>
		</div>
	</AuthPanel>
{:else if data.view === 'empty'}
	<PageIntro title={t.home.emptyTitle} intro={t.calendars.emptyIntro} />
	<div class="wrap narrow"><AddCalendar {form} /></div>
{:else}
	<PageIntro title={data.view === 'combined' ? t.home.yourCalendars : t.changes.title} intro={t.changes.intro} />
	<div class="wrap stack">
		{#if data.view === 'combined'}
			<section class="stack">
				{#each data.calendars as calendar (calendar.calendarId)}
					<CalendarStatus {calendar} href="/calendars/{calendar.calendarId}" />
				{/each}
			</section>
			<h2>{t.home.latest}</h2>
		{/if}
		<Feed groups={data.groups} />
		{#if data.next}<div><Button href="/changes?before={data.next}" variant="secondary">{t.changes.older}</Button></div>{/if}
	</div>
{/if}

<style>
	.points {
		margin: 0;
		padding-left: var(--space-5);
		display: flex;
		flex-direction: column;
		gap: var(--space-2);
	}
	.narrow {
		max-width: 640px;
	}
	h2 {
		font: var(--type-h3);
		margin-top: var(--space-4);
	}
</style>
