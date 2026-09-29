<script lang="ts">
	import { enhance } from '$app/forms';
	import AuthPanel from '$lib/components/AuthPanel.svelte';
	import BotCheck from '$lib/components/BotCheck.svelte';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	let bot: BotCheck | undefined = $state();
	const s = submitter({ onResult: () => bot?.reset() });
	const r = $derived(refusal(form));
</script>

<svelte:head><title>{t.signIn.title} · InTheKnow</title></svelte:head>

<AuthPanel title={t.signIn.title} intro={t.signIn.intro}>
	{#if data.expired}<p class="notice warning">{t.signIn.expired}</p>{/if}
	<form method="POST" use:enhance={s.enhance} class="stack">
		{#if r}
			<div class="notice error" role="alert">
				<p>{r.message}</p>
				{#if r.reason === 'not_confirmed'}<p><a href="/account/confirmation">{t.signIn.resend}</a></p>{/if}
			</div>
		{/if}
		<Field label={t.auth.email} id="email" name="email" type="email" autocomplete="username" required value={r?.values?.email ?? ''} />
		<Field label={t.auth.password} id="password" name="password" type="password" autocomplete="current-password" required />
		{#if data.botCheck}<BotCheck bind:this={bot} {...data.botCheck} />{/if}
		<div><Button disabled={s.pending}>{t.signIn.submit}</Button></div>
	</form>
	<p><a href="/account/forgot-password">{t.signIn.forgot}</a></p>
	<p>{t.signIn.noAccount} <a href="/account/sign-up">{t.nav.signUp}</a></p>
</AuthPanel>
