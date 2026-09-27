<script lang="ts">
	import { enhance } from '$app/forms';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import RecoveryCodes from '$lib/components/RecoveryCodes.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const w = $derived(t.twoFactor);
	const s = submitter();
	const r = $derived(refusal(form));
	// The setup stays on screen after a wrong code: it lives only in this page, never on the server's side.
	let setup = $state<{ secret: string; qr: string } | null>(null);
	$effect(() => {
		if (form && 'secret' in form && form.secret && form.qr) setup = { secret: form.secret, qr: form.qr };
	});
	const codes = $derived(form && 'recoveryCodes' in form ? form.recoveryCodes : null);
	const grouped = (secret: string) => secret.match(/.{1,4}/g)?.join(' ') ?? secret;
</script>

<svelte:head><title>{w.title} · InTheKnow</title></svelte:head>

<div class="stack">
	<h2>{w.title}</h2>
	{#if codes}
		<p class="notice success" role="status">{w.enabled}</p>
		<RecoveryCodes {codes} />
		{#if data.admin}<p><a href="/admin/accounts">{w.adminLink}</a></p>{/if}
		<p><a href="/settings/security">{t.settings.security}</a></p>
	{:else if data.twoFactor}
		<p>{w.alreadyOn}</p>
		<p><a href="/settings/security">{t.settings.security}</a></p>
	{:else if setup}
		<ol class="steps">
			<li>
				<p>{w.scan}</p>
				<img src={setup.qr} alt={w.qrAlt} width="200" height="200" />
				<p>{w.orType}</p>
				<p class="secret">{grouped(setup.secret)}</p>
			</li>
			<li>
				<form method="POST" action="?/enable" use:enhance={s.enhance} class="stack">
					<Field
						label={w.code}
						id="code"
						name="code"
						autocomplete="one-time-code"
						inputmode="numeric"
						required
						error={r?.values?.form === 'enable' ? r.message : undefined}
					/>
					<div><Button disabled={s.pending}>{w.enable}</Button></div>
				</form>
			</li>
		</ol>
	{:else}
		<p>{w.intro}</p>
		{#if r}<p class="notice error" role="alert">{r.message}</p>{/if}
		<form method="POST" action="?/start" use:enhance={s.enhance}>
			<Button disabled={s.pending}>{w.start}</Button>
		</form>
	{/if}
</div>

<style>
	h2 {
		font: var(--type-h3);
	}
	.steps {
		margin: 0;
		padding-left: var(--space-5);
		display: flex;
		flex-direction: column;
		gap: var(--space-6);
	}
	.steps li {
		display: flex;
		flex-direction: column;
		gap: var(--space-3);
	}
	img {
		background: var(--white);
		padding: var(--space-2);
		box-shadow: inset 0 0 0 1px var(--border-subtle);
		max-width: 100%;
		height: auto;
	}
	.secret {
		font: var(--type-time);
		color: var(--text-strong);
		letter-spacing: 0.05em;
		overflow-wrap: anywhere;
	}
</style>
