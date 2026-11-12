<script lang="ts">
	import ArrowLeft from '@lucide/svelte/icons/arrow-left';
	import { onMount } from 'svelte';
	import History from '$lib/components/History.svelte';
	import PageIntro from '$lib/components/PageIntro.svelte';
	import { dicts } from '$lib/i18n';

	let { data } = $props();
	const t = $derived(dicts[data.lang]);

	// FR-006: the opened change in view.
	onMount(() => document.querySelector('[data-opened]')?.scrollIntoView({ block: 'center' }));
</script>

<svelte:head><title>{data.history.name} · {t.history.title} · InTheKnow</title></svelte:head>

<PageIntro title={data.history.name} overline={t.history.title} intro={data.history.calendarName} />
<div class="wrap stack">
	<a class="back" href="/changes"><ArrowLeft size={16} aria-hidden="true" />{t.history.back}</a>
	<div>
		<History history={data.history} openedId={data.openedId} all={data.all} />
	</div>
</div>

<style>
	.back {
		display: inline-flex;
		align-items: center;
		gap: var(--space-1);
		min-height: var(--hit-min);
		align-self: flex-start;
		font-weight: 700;
		color: var(--text-link);
	}
</style>
