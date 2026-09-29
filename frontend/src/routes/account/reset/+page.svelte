<script lang="ts">
	import { enhance } from '$app/forms';
	import AuthPanel from '$lib/components/AuthPanel.svelte';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import PasswordRules from '$lib/components/PasswordRules.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { fieldError, formError, refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const s = submitter();
	const r = $derived(refusal(form));
	const passwordReasons = ['password_length', 'password_common', 'password_is_email'];
</script>

<svelte:head><title>{t.reset.title} · InTheKnow</title></svelte:head>

{#if form && 'reset' in form}
	<AuthPanel title={t.reset.doneTitle}>
		<p class="notice success" role="status">{t.reset.done}</p>
		<div><Button href="/sign-in">{t.nav.signIn}</Button></div>
	</AuthPanel>
{:else if !data.hasToken || r?.reason === 'token_invalid'}
	<AuthPanel title={t.reset.title}>
		<p class="notice error" role="alert">{t.reasons.token_invalid}</p>
		<p><a href="/account/forgot-password">{t.reset.newLink}</a></p>
	</AuthPanel>
{:else}
	<AuthPanel title={t.reset.title} intro={t.reset.intro}>
		<form method="POST" use:enhance={s.enhance} class="stack">
			{#if formError(form, ...passwordReasons)}<p class="notice error" role="alert">{formError(form, ...passwordReasons)}</p>{/if}
			<Field
				label={t.auth.newPassword}
				id="password"
				name="password"
				type="password"
				autocomplete="new-password"
				required
				minlength={12}
				maxlength={128}
				aria-describedby="password-rules"
				error={fieldError(form, ...passwordReasons)}
			/>
			<PasswordRules id="password-rules" reason={r?.reason} />
			<div><Button disabled={s.pending}>{t.reset.submit}</Button></div>
		</form>
	</AuthPanel>
{/if}
