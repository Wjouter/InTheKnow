<script lang="ts">
	import { enhance } from '$app/forms';
	import AuthPanel from '$lib/components/AuthPanel.svelte';
	import Button from '$lib/components/Button.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const s = submitter();
	const r = $derived(refusal(form));
</script>

<svelte:head><title>{t.emailChange.title} · InTheKnow</title></svelte:head>

{#if form && 'changed' in form}
	<AuthPanel title={t.emailChange.title}>
		<p class="notice success" role="status">{t.emailChange.done}</p>
		<div><Button href="/">{t.errors.home}</Button></div>
	</AuthPanel>
{:else if !data.hasToken || r}
	<AuthPanel title={t.emailChange.title}>
		<p class="notice error" role="alert">{r?.message ?? t.reasons.token_invalid}</p>
		<p><a href="/settings/account">{t.emailChange.again}</a></p>
	</AuthPanel>
{:else}
	<AuthPanel title={t.emailChange.title} intro={t.emailChange.intro}>
		<form method="POST" use:enhance={s.enhance}>
			<Button disabled={s.pending}>{t.emailChange.submit}</Button>
		</form>
	</AuthPanel>
{/if}
