<script lang="ts">
	import Button from '$lib/components/Button.svelte';
	import Feed from '$lib/components/Feed.svelte';
	import PageIntro from '$lib/components/PageIntro.svelte';
	import { dicts } from '$lib/i18n';

	let { data } = $props();
	const t = $derived(dicts[data.lang]);
	const hidden = $derived(data.showHidden ? '&showHidden=1' : '');
	let form: HTMLFormElement | undefined = $state();
</script>

<svelte:head><title>{t.changes.title} · InTheKnow</title></svelte:head>

<PageIntro title={t.changes.title} intro={t.changes.intro} />
<div class="wrap stack">
	<!-- A GET form: it changes nothing, and without scripting its button applies it (US2-8). -->
	<form method="GET" bind:this={form} class="row filter">
		<label class="choice">
			<input type="checkbox" name="showHidden" value="1" checked={data.showHidden} onchange={() => form?.requestSubmit()} />
			{t.changes.showHidden}
		</label>
		<noscript><Button size="sm" variant="secondary">{t.changes.apply}</Button></noscript>
	</form>
	<Feed groups={data.groups} />
	<nav class="row" aria-label={t.changes.title}>
		{#if data.before}<Button href="/changes{data.showHidden ? '?showHidden=1' : ''}" variant="secondary">{t.changes.newest}</Button>{/if}
		{#if data.next}<Button href="/changes?before={data.next}{hidden}" variant="secondary">{t.changes.older}</Button>{/if}
	</nav>
</div>
