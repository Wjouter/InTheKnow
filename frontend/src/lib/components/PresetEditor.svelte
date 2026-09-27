<script lang="ts">
	import { enhance } from '$app/forms';
	import { page } from '$app/state';
	import Button from './Button.svelte';
	import ConfirmAction from './ConfirmAction.svelte';
	import Dialog from './Dialog.svelte';
	import Field from './Field.svelte';
	import RuleEditor from './RuleEditor.svelte';
	import Toast from './Toast.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	type Preset = {
		name: string;
		isDefault: boolean;
		followers: number;
		rules: { outcome: string; when: Record<string, unknown> }[];
		rulesKey: string;
	};
	// Edit a preset's name and rules (US3-6, FR-038). `followers` is shown only for personal presets.
	let { preset, form, showFollowers }: { preset: Preset; form: unknown; showFollowers: boolean } = $props();
	const t = $derived(dicts[page.data.lang]);
	const save = submitter();
	const remove = submitter();
	const r = $derived(refusal(form));
	let confirmSave = $state(false);
	let formEl: HTMLFormElement | undefined = $state();
	const id = $props.id();
</script>

<form method="POST" action="?/save" use:enhance={save.enhance} class="stack" bind:this={formEl} id="{id}-form">
	<Field
		label={t.presets.name}
		id="{id}-name"
		name="name"
		required
		maxlength={100}
		value={r?.values?.name ?? preset.name}
		error={r?.reason === 'invalid_name' || r?.reason === 'name_taken' ? r.message : undefined}
	/>
	{#key preset.rulesKey}
		<RuleEditor rules={preset.rules} error={r && r.reason !== 'invalid_name' && r.reason !== 'name_taken' ? r : null} />
	{/key}
	<div class="row">
		{#if showFollowers && preset.followers > 0}
			<Button type="button" disabled={save.pending} onclick={() => (confirmSave = true)}>{t.presets.save}</Button>
		{:else}
			<Button disabled={save.pending}>{t.presets.save}</Button>
		{/if}
	</div>
</form>

<Dialog bind:open={confirmSave} title={t.presets.saveTitle}>
	<p>{t.presets.saveBody(preset.followers)}</p>
	{#snippet actions()}
		<Button type="button" variant="ghost" onclick={() => (confirmSave = false)}>{t.cancel}</Button>
		<Button
			type="button"
			variant="dark"
			onclick={() => {
				confirmSave = false;
				formEl?.requestSubmit();
			}}>{t.presets.save}</Button
		>
	{/snippet}
</Dialog>

{#if !preset.isDefault}
	<div class="delete">
		<ConfirmAction
			label={t.presets.delete}
			title={t.presets.deleteTitle}
			confirm={t.presets.deleteConfirm}
			action="?/delete"
			enhanceWith={remove.enhance}
			pending={remove.pending}
		>
			{#if showFollowers}<p>{t.presets.deleteBody(preset.followers)}</p>{/if}
		</ConfirmAction>
	</div>
{/if}
<Toast message={form && typeof form === 'object' && 'saved' in form ? t.saved : null} />

<style>
	.delete {
		margin-top: var(--space-8);
		padding-top: var(--space-6);
		border-top: 1px solid var(--border-subtle);
	}
</style>
