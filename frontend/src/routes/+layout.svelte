<script lang="ts">
	import '@fontsource/lato/400.css';
	import '@fontsource/lato/700.css';
	import '@fontsource/lato/900.css';
	import '@fontsource/jetbrains-mono/600.css';
	import '$lib/styles/tokens.css';
	import '$lib/styles/base.css';
	import Header from '$lib/components/Header.svelte';
	import PhoneNav from '$lib/components/PhoneNav.svelte';
	import Button from '$lib/components/Button.svelte';
	import { page } from '$app/state';
	import { dicts } from '$lib/i18n';

	let { data, children } = $props();
	const t = $derived(dicts[data.lang]);
</script>

<svelte:head><title>InTheKnow</title></svelte:head>

<Header />
<main class:with-phone-nav={data.me}>
	{#if data.unavailable}
		<div class="wrap stack">
			<h1>{t.errors.unavailableTitle}</h1>
			<p>{t.errors.unavailable}</p>
			<div><Button href={page.url.pathname + page.url.search}>{t.errors.tryAgain}</Button></div>
		</div>
	{:else}
		{@render children()}
	{/if}
</main>
{#if data.me}<PhoneNav />{/if}

<style>
	main.with-phone-nav {
		padding-bottom: var(--tabbar-h);
	}
	h1 {
		font: var(--type-h3);
	}
	@media (min-width: 768px) {
		main.with-phone-nav {
			padding-bottom: 0;
		}
	}
</style>
