<script lang="ts">
	import { applyAction, enhance } from '$app/forms';
	import { page } from '$app/state';
	import type { SubmitFunction } from '@sveltejs/kit';
	import Button from './Button.svelte';
	import Dialog from './Dialog.svelte';
	import Field from './Field.svelte';
	import { dicts } from '$lib/i18n';

	// Confirm it's you (FR-020, research R8): the password, plus a code when 2FA is on. On success the page's
	// submitter sends the original form again, whose fields are still filled in.
	let { open = $bindable(false), onConfirmed }: { open?: boolean; onConfirmed: () => void } = $props();
	const t = $derived(dicts[page.data.lang]);
	const twoFactor = $derived(page.data.me?.twoFactor ?? false);
	let pending = $state(false);
	let message = $state<string | null>(null);
	let formEl: HTMLFormElement | undefined = $state();

	// Its own result handling, so a wrong password shows here and the page's form result stays as it was.
	const submit: SubmitFunction = () => {
		pending = true;
		return async ({ result }) => {
			pending = false;
			if (result.type === 'success' && result.data?.reauthenticated) {
				message = null;
				formEl?.reset();
				open = false;
				onConfirmed();
			} else if (result.type === 'failure') {
				message = typeof result.data?.message === 'string' ? result.data.message : t.reasons.unknown;
			} else {
				await applyAction(result);
			}
		};
	};
</script>

<Dialog bind:open title={t.stepUp.title}>
	<form method="POST" action="?/reauthenticate" use:enhance={submit} bind:this={formEl} class="stack">
		<p>{t.stepUp.intro}</p>
		{#if message}<p class="notice error" role="alert">{message}</p>{/if}
		<Field label={t.auth.password} id="step-up-password" name="password" type="password" autocomplete="current-password" required />
		{#if twoFactor}
			<Field label={t.code.label} id="step-up-code" name="code" autocomplete="one-time-code" inputmode="numeric" required hint={t.code.hint} />
		{/if}
		<div class="row">
			<Button disabled={pending}>{t.stepUp.submit}</Button>
			<Button type="button" variant="ghost" onclick={() => (open = false)}>{t.cancel}</Button>
		</div>
	</form>
</Dialog>
