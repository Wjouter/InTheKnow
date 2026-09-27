<script lang="ts">
	import { enhance } from '$app/forms';
	import Button from '$lib/components/Button.svelte';
	import ConfirmAction from '$lib/components/ConfirmAction.svelte';
	import Field from '$lib/components/Field.svelte';
	import StepUp from '$lib/components/StepUp.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const w = $derived(t.account);
	const s = submitter();
	const r = $derived(refusal(form));
	const shown = $derived(r && !r.stepUp ? r : null);
</script>

<svelte:head><title>{t.settings.account} · InTheKnow</title></svelte:head>

<div class="stack">
	<section class="section">
		<h2>{w.address}</h2>
		<p class="email">{data.email}</p>
		{#if data.pendingEmail}<p class="notice">{w.pending(data.pendingEmail)}</p>{/if}
	</section>

	<form method="POST" action="?/changeEmail" use:enhance={s.enhance} class="section">
		<h2>{w.change}</h2>
		{#if form && 'done' in form}<p class="notice success" role="status">{w.emailSent}</p>{/if}
		<Field
			label={w.newAddress}
			id="new-email"
			name="email"
			type="email"
			autocomplete="email"
			required
			hint={w.changeHint}
			error={shown?.values?.form === 'email' ? shown.message : undefined}
			value={shown?.values?.email ?? ''}
		/>
		<div><Button disabled={s.pending}>{w.changeSubmit}</Button></div>
	</form>

	<section class="section">
		<h2>{w.delete}</h2>
		<p>{w.deleteIntro}</p>
		{#if shown?.values?.form === 'delete'}<p class="notice error" role="alert">{shown.message}</p>{/if}
		<div>
			<ConfirmAction label={w.delete} title={w.deleteTitle} confirm={w.deleteConfirm} action="?/deleteAccount" enhanceWith={s.enhance} pending={s.pending}>
				<p>{w.deleteBody}</p>
				<ul>
					{#each w.deleteList as item (item)}<li>{item}</li>{/each}
				</ul>
			</ConfirmAction>
		</div>
	</section>
</div>
<StepUp bind:open={s.stepUpOpen} onConfirmed={s.resubmit} />

<style>
	h2 {
		font: var(--type-h4);
	}
	.email {
		font-weight: var(--fw-bold);
		color: var(--text-strong);
		overflow-wrap: anywhere;
	}
	ul {
		margin: 0;
		padding-left: var(--space-5);
	}
</style>
