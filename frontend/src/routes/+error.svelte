<script lang="ts">
	import { page } from '$app/state';
	import { dicts } from '$lib/i18n';
	import Button from '$lib/components/Button.svelte';

	// One view per kind of refusal. Never the status or technical details (FR-041).
	const t = $derived(dicts[page.data.lang ?? 'en']);
	const kind = $derived(page.error?.kind);
</script>

<div class="wrap stack">
	{#if kind === 'locked'}
		<h1>{t.errors.lockedTitle}</h1>
		<p>{t.errors.lockedBody}</p>
		<div class="row">
			<Button href="/settings/security/two-factor">{t.errors.lockedSetUp}</Button>
			<form method="POST" action="/sign-out"><Button variant="secondary">{t.errors.lockedSignOut}</Button></form>
		</div>
	{:else if kind === 'notAllowed'}
		<h1>{t.errors.notAllowedTitle}</h1>
		<p>{t.errors.notAllowedBody}</p>
		<div><Button href="/">{t.errors.home}</Button></div>
	{:else if kind === 'notInAccount'}
		<h1>{t.errors.notInAccountTitle}</h1>
		<p>{t.errors.notInAccount(page.error?.email ?? page.data.me?.email ?? '')}</p>
		<p>{t.errors.notInAccountHint}</p>
		<div><Button href="/calendars">{t.nav.calendars}</Button></div>
	{:else if kind === 'unavailable'}
		<h1>{t.errors.unavailableTitle}</h1>
		<p>{t.errors.unavailable}</p>
		<div><Button href={page.url.pathname + page.url.search}>{t.errors.tryAgain}</Button></div>
	{:else if kind === 'notFound' || page.status === 404}
		<h1>{t.errors.notFoundTitle}</h1>
		<p>{t.errors.notFound}</p>
		<div><Button href="/">{t.errors.home}</Button></div>
	{:else}
		<h1>{t.errors.generic}</h1>
		<p>{t.errors.genericBody}</p>
		<div><Button href="/">{t.errors.home}</Button></div>
	{/if}
</div>

<style>
	h1 {
		font: var(--type-h3);
	}
</style>
