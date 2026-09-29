<script lang="ts">
	import { enhance } from '$app/forms';
	import AuthPanel from '$lib/components/AuthPanel.svelte';
	import BotCheck from '$lib/components/BotCheck.svelte';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import { page } from '$app/state';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';
	import type { BotCheck as Provider } from '$lib/server/actions';

	// The "send me an email" forms: a new confirmation link, or a password reset. One result for every address.
	let {
		title,
		intro,
		submit,
		done,
		botCheck,
		form
	}: { title: string; intro: string; submit: string; done: string; botCheck: Provider; form: unknown } = $props();
	const t = $derived(dicts[page.data.lang]);
	let bot: BotCheck | undefined = $state();
	const s = submitter({ onResult: () => bot?.reset() });
	const r = $derived(refusal(form));
</script>

{#if form && typeof form === 'object' && 'done' in form}
	<AuthPanel {title}>
		<p class="notice success" role="status">{done}</p>
		<p><a href="/sign-in">{t.nav.signIn}</a></p>
	</AuthPanel>
{:else}
	<AuthPanel {title} {intro}>
		<form method="POST" use:enhance={s.enhance} class="stack">
			{#if r}<p class="notice error" role="alert">{r.message}</p>{/if}
			<Field label={t.auth.email} id="email" name="email" type="email" autocomplete="email" required value={r?.values?.email ?? ''} />
			{#if botCheck}<BotCheck bind:this={bot} {...botCheck} />{/if}
			<div><Button disabled={s.pending}>{submit}</Button></div>
		</form>
	</AuthPanel>
{/if}
