<script lang="ts">
	import PageIntro from '$lib/components/PageIntro.svelte';
	import TabNav from '$lib/components/TabNav.svelte';
	import { dicts } from '$lib/i18n';

	let { data, children } = $props();
	const t = $derived(dicts[data.lang]);
	const base = $derived(`/calendars/${data.calendar.calendarId}`);
</script>

<svelte:head><title>{data.calendar.name} · InTheKnow</title></svelte:head>

<PageIntro title={data.calendar.name} overline={t.calendars.title} />
<div class="wrap">
	<TabNav
		label={t.calendars.tabsLabel}
		items={[
			{ href: base, label: t.calendars.overview },
			{ href: `${base}/filter`, label: t.calendars.filter },
			{ href: `${base}/notifications`, label: t.calendars.notificationsTab }
		]}
	>
		{@render children()}
	</TabNav>
</div>
