<script lang="ts">
	import { enhance } from '$app/forms';
	import AuthPanel from '$lib/components/AuthPanel.svelte';
	import BotCheck from '$lib/components/BotCheck.svelte';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import PasswordRules from '$lib/components/PasswordRules.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { fieldError, formError, refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	let bot: BotCheck | undefined = $state();
	const s = submitter({ onResult: () => bot?.reset() });
	const r = $derived(refusal(form));
	const passwordReasons = ['password_length', 'password_common', 'password_is_email'];
</script>

<svelte:head><title>{t.signUp.title} · InTheKnow</title></svelte:head>

{#if form && 'done' in form}
	<AuthPanel title={t.signUp.doneTitle}>
		<p>{t.signUp.done}</p>
		<p>{t.signUp.doneHint} <a href="/account/confirmation">{t.signUp.resend}</a></p>
	</AuthPanel>
{:else}
	<AuthPanel title={t.signUp.title} intro={t.signUp.intro}>
		{#if data.invite}<p class="notice">{t.signUp.withInvite}</p>{/if}
		<form method="POST" use:enhance={s.enhance} class="stack">
			{#if formError(form, 'invalid_email', 'invalid_language', ...passwordReasons)}
				<p class="notice error" role="alert">{formError(form, 'invalid_email', 'invalid_language', ...passwordReasons)}</p>
			{/if}
			<Field
				label={t.auth.email}
				id="email"
				name="email"
				type="email"
				autocomplete="email"
				required
				value={r?.values?.email ?? ''}
				error={fieldError(form, 'invalid_email')}
			/>
			<Field
				label={t.auth.password}
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
			<Field label={t.auth.language} id="language" error={fieldError(form, 'invalid_language')}>
				{#snippet children({ id, describedby })}
					<select {id} name="language" aria-describedby={describedby} value={r?.values?.language ?? data.lang}>
						<option value="en" lang="en">English</option>
						<option value="nl" lang="nl">Nederlands</option>
					</select>
				{/snippet}
			</Field>
			{#if data.botCheck}<BotCheck bind:this={bot} {...data.botCheck} />{/if}
			<div><Button disabled={s.pending}>{t.signUp.submit}</Button></div>
		</form>
		<p>{t.signUp.haveAccount} <a href="/sign-in">{t.nav.signIn}</a></p>
	</AuthPanel>
{/if}
