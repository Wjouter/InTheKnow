<script lang="ts">
	import { enhance } from '$app/forms';
	import Badge from '$lib/components/Badge.svelte';
	import Button from '$lib/components/Button.svelte';
	import ConfirmAction from '$lib/components/ConfirmAction.svelte';
	import Field from '$lib/components/Field.svelte';
	import PasswordRules from '$lib/components/PasswordRules.svelte';
	import RecoveryCodes from '$lib/components/RecoveryCodes.svelte';
	import StepUp from '$lib/components/StepUp.svelte';
	import Toast from '$lib/components/Toast.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const w = $derived(t.security);
	const s = submitter();
	const r = $derived(refusal(form));
	const refusedIn = (name: string) => (r && !r.stepUp && r.values?.form === name ? r : null);
	const done = $derived(form && 'done' in form ? (form.done as keyof typeof w.done) : null);
	const codes = $derived(form && 'recoveryCodes' in form ? (form.recoveryCodes as string[]) : null);
	const passwordReasons = ['password_length', 'password_common', 'password_is_email'];
</script>

<svelte:head><title>{t.settings.security} · InTheKnow</title></svelte:head>

<div class="stack">
	<form method="POST" action="?/setPassword" use:enhance={s.enhance} class="section">
		<h2>{w.password}</h2>
		<Field
			label={data.hasPassword ? w.newPassword : w.addPasswordLabel}
			id="password"
			name="password"
			type="password"
			autocomplete="new-password"
			required
			minlength={12}
			maxlength={128}
			aria-describedby="password-rules"
			error={refusedIn('password')?.message}
		/>
		<PasswordRules id="password-rules" reason={refusedIn('password')?.reason} />
		<p class="muted">{w.passwordHint}</p>
		<div><Button disabled={s.pending}>{data.hasPassword ? w.changePassword : w.addPassword}</Button></div>
	</form>

	<section class="section">
		<h2>{w.twoFactor}</h2>
		<p><Badge tone={data.twoFactor ? 'green' : 'neutral'}>{data.twoFactor ? w.on : w.off}</Badge></p>
		{#if refusedIn('twoFactor')}<p class="notice error" role="alert">{refusedIn('twoFactor')?.message}</p>{/if}
		{#if data.twoFactor}
			<p>{w.codesLeft(data.recoveryCodesLeft)}</p>
			{#if codes}
				<RecoveryCodes {codes} />
			{:else}
				<form method="POST" action="?/newRecoveryCodes" use:enhance={s.enhance} class="stack">
					<Field
						label={w.codeForNew}
						id="recovery-code"
						name="code"
						autocomplete="one-time-code"
						inputmode="numeric"
						required
						error={refusedIn('recovery')?.message}
					/>
					<div><Button variant="secondary" disabled={s.pending}>{w.newCodes}</Button></div>
				</form>
			{/if}
			<div>
				<ConfirmAction label={w.turnOff} title={w.turnOffTitle} confirm={w.turnOffConfirm} action="?/disable2fa" enhanceWith={s.enhance} pending={s.pending}>
					<p>{w.turnOffBody}</p>
				</ConfirmAction>
			</div>
		{:else}
			<p>{w.twoFactorIntro}</p>
			<div><Button href="/settings/security/two-factor">{w.turnOn}</Button></div>
		{/if}
	</section>

	<section class="section">
		<h2>{w.methods}</h2>
		{#if refusedIn('methods')}<p class="notice error" role="alert">{refusedIn('methods')?.message}</p>{/if}
		<ul class="list">
			{#each data.methods as m (m.id)}
				<li>
					<div>
						<strong>{m.type === 'password' ? w.methodPassword : w.methodOther}</strong>
						<span class="muted">{w.added(m.added)} · {m.lastUsed ? w.lastUsed(m.lastUsed) : w.neverUsed}</span>
					</div>
					<ConfirmAction
						label={w.remove}
						title={w.removeMethodTitle}
						confirm={w.remove}
						action="?/removeMethod"
						fields={{ methodId: String(m.id) }}
						enhanceWith={s.enhance}
						pending={s.pending}
						variant="ghost"
						size="sm"
					>
						<p>{w.removeMethodBody}</p>
					</ConfirmAction>
				</li>
			{/each}
		</ul>
	</section>

	<section class="section">
		<h2>{w.devices}</h2>
		{#if refusedIn('sessions')}<p class="notice error" role="alert">{refusedIn('sessions')?.message}</p>{/if}
		<ul class="list">
			{#each data.sessions as d (d.id)}
				<li>
					<div>
						<strong>{d.device}</strong>
						{#if d.current}<Badge tone="green">{w.thisDevice}</Badge>{/if}
						<span class="muted">{w.lastUsed(d.lastUsed)}</span>
					</div>
					{#if !d.current}
						<form method="POST" action="?/endSession" use:enhance={s.enhance}>
							<input type="hidden" name="sessionId" value={String(d.id)} />
							<Button size="sm" variant="ghost" disabled={s.pending}>{w.signOutDevice}</Button>
						</form>
					{/if}
				</li>
			{/each}
		</ul>
		<div>
			<ConfirmAction label={w.everywhere} title={w.everywhereTitle} confirm={w.everywhere} action="?/signOutEverywhere" enhanceWith={s.enhance} pending={s.pending}>
				<p>{w.everywhereBody}</p>
			</ConfirmAction>
		</div>
	</section>
</div>
<StepUp bind:open={s.stepUpOpen} onConfirmed={s.resubmit} />
<Toast message={done ? w.done[done] : null} />

<style>
	h2 {
		font: var(--type-h4);
	}
	.list {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		flex-direction: column;
	}
	.list li {
		display: flex;
		justify-content: space-between;
		align-items: center;
		flex-wrap: wrap;
		gap: var(--space-2);
		padding: var(--space-3) 0;
		border-bottom: 1px solid var(--border-subtle);
	}
	.list li > div {
		display: flex;
		flex-wrap: wrap;
		gap: var(--space-1) var(--space-3);
		align-items: center;
		min-width: 0;
	}
	.list .muted {
		flex-basis: 100%;
		font: var(--type-small);
	}
	strong {
		color: var(--text-strong);
		overflow-wrap: anywhere;
	}
</style>
