<script lang="ts">
	import { enhance } from '$app/forms';
	import AuthPanel from '$lib/components/AuthPanel.svelte';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { fieldError, formError } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const s = submitter();
</script>

<svelte:head><title>{t.code.title} · InTheKnow</title></svelte:head>

<AuthPanel title={t.code.title} intro={t.code.intro}>
	<form method="POST" use:enhance={s.enhance} class="stack">
		{#if formError(form, 'code_invalid')}<p class="notice error" role="alert">{formError(form, 'code_invalid')}</p>{/if}
		<Field
			label={t.code.label}
			id="code"
			name="code"
			autocomplete="one-time-code"
			inputmode="numeric"
			required
			hint={t.code.hint}
			error={fieldError(form, 'code_invalid')}
		/>
		<div><Button disabled={s.pending}>{t.code.submit}</Button></div>
	</form>
</AuthPanel>
