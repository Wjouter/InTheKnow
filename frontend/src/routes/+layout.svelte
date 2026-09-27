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
	import Toast from '$lib/components/Toast.svelte';
	import { replaceState } from '$app/navigation';
	import { page } from '$app/state';
	import { dicts } from '$lib/i18n';

	let { data, children } = $props();
	const t = $derived(dicts[data.lang]);
	// Svelte sets document.title from <svelte:head> only when the value changes, and pages set their own. So this reruns
	// only when the unavailable view comes or goes, not on every navigation.
	const unavailable = $derived(Boolean(data.unavailable));

	// A notice handed over by a redirecting action (enhance.svelte.ts). Taken once and cleared from the history entry,
	// so Back doesn't show it again; a new object each time, so the same text twice shows twice.
	let notice = $state<{ text: string } | null>(null);
	$effect(() => {
		if (!page.state.notice) return;
		notice = { text: page.state.notice };
		replaceState('', {});
	});
</script>

<svelte:head><title>{unavailable ? `${t.errors.unavailableTitle} · InTheKnow` : 'InTheKnow'}</title></svelte:head>

<Header />
<main class:with-phone-nav={data.me}>
	{#if data.unavailable}
		<div class="wrap stack">
			<h1>{t.errors.unavailableTitle}</h1>
			<p>{t.errors.unavailable}</p>
			<!-- A full load: a client-side visit to the same address reruns no load. -->
			<div data-sveltekit-reload><Button href={page.url.pathname + page.url.search}>{t.errors.tryAgain}</Button></div>
		</div>
	{:else}
		{@render children()}
	{/if}
</main>
{#if data.me}<PhoneNav />{/if}
{#key notice}<Toast message={notice?.text} />{/key}

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
