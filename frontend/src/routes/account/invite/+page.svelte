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

<svelte:head><title>{t.invite.title} · InTheKnow</title></svelte:head>

{#if form && 'admin' in form}
	<AuthPanel title={t.invite.doneTitle}>
		<p class="notice">{t.invite.adminLocked}</p>
		<div><Button href="/settings/security/two-factor">{t.errors.lockedSetUp}</Button></div>
	</AuthPanel>
{:else if !data.hasToken || r?.reason === 'invite_invalid'}
	<AuthPanel title={t.invite.title}>
		<p class="notice error" role="alert">{t.reasons.invite_invalid}</p>
	</AuthPanel>
{:else}
	<AuthPanel title={t.invite.title} intro={t.invite.intro}>
		<form method="POST" use:enhance={s.enhance} class="stack">
			{#if formError(form, 'invalid_language', ...passwordReasons)}
				<p class="notice error" role="alert">{formError(form, 'invalid_language', ...passwordReasons)}</p>
			{/if}
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
					<select {id} name="language" aria-describedby={describedby} value={r?.values?.language ?? ''}>
						<option value="">{t.invite.inviteLanguage}</option>
						<option value="en" lang="en">English</option>
						<option value="nl" lang="nl">Nederlands</option>
					</select>
				{/snippet}
			</Field>
			<div><Button disabled={s.pending}>{t.invite.submit}</Button></div>
		</form>
	</AuthPanel>
{/if}
