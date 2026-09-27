<script lang="ts">
	import { enhance } from '$app/forms';
	import AuthPanel from '$lib/components/AuthPanel.svelte';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { fieldError, refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const s = submitter();
	const r = $derived(refusal(form));
</script>

<svelte:head><title>{t.confirm.title} · InTheKnow</title></svelte:head>

{#if form && 'confirmed' in form}
	<AuthPanel title={t.confirm.doneTitle}>
		<p class="notice success" role="status">{t.confirm.done}</p>
		<div><Button href="/sign-in">{t.nav.signIn}</Button></div>
	</AuthPanel>
{:else if !data.hasToken || r?.reason === 'token_invalid'}
	<AuthPanel title={t.confirm.title}>
		<p class="notice error" role="alert">{t.reasons.token_invalid}</p>
		<p><a href="/account/confirmation">{t.confirm.newLink}</a></p>
	</AuthPanel>
{:else}
	<AuthPanel title={t.confirm.title} intro={t.confirm.intro}>
		<form method="POST" use:enhance={s.enhance} class="stack">
			{#if r && r.reason !== 'password_incorrect'}<p class="notice error" role="alert">{r.message}</p>{/if}
			<Field
				label={t.auth.password}
				id="password"
				name="password"
				type="password"
				autocomplete="current-password"
				required
				error={fieldError(form, 'password_incorrect')}
			/>
			<div><Button disabled={s.pending}>{t.confirm.submit}</Button></div>
		</form>
	</AuthPanel>
{/if}
