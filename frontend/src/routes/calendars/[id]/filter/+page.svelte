<script lang="ts">
	import { enhance } from '$app/forms';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import RuleEditor from '$lib/components/RuleEditor.svelte';
	import Toast from '$lib/components/Toast.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const r = $derived(refusal(form));
	const refusedIn = (name: string) => (r?.values?.form === name ? r : null);
	const saved = $derived(form && 'saved' in form ? form.saved : null);
	const rulesForm = submitter();
	const presetForm = submitter();
	const asPresetForm = submitter();
</script>

<div class="stack">
	<section class="section">
		<h2>{t.filter.title}</h2>
		<p>{data.source.preset ? t.filter.followsPreset(data.source.preset) : t.filter.custom}</p>
		{#if data.rules.length}
			<ul class="sentences">
				{#each data.rules as rule, i (i)}
					<li class:inactive={rule.inactive}>
						{rule.sentence}
						{#if rule.inactiveReason}<span class="reason">{rule.inactiveReason}</span>{/if}
					</li>
				{/each}
			</ul>
		{:else}
			<p class="muted">{t.rules.editor.noRules}</p>
		{/if}
	</section>

	<form method="POST" action="?/applyPreset" use:enhance={presetForm.enhance} class="section">
		<h2>{t.filter.usePreset}</h2>
		{#if refusedIn('preset')}<p class="notice error" role="alert">{refusedIn('preset')?.message}</p>{/if}
		<Field label={t.filter.preset} id="presetId">
			{#snippet children({ id })}
				<select {id} name="presetId" value={String(data.source.presetId ?? '')}>
					{#each data.presets as p (p.id)}<option value={String(p.id)}>{p.name}{p.scope === 'global' ? ` (${t.filter.forEveryone})` : ''}</option>{/each}
				</select>
			{/snippet}
		</Field>
		<div class="row">
			<Button disabled={presetForm.pending}>{t.filter.apply}</Button>
			<a href="/presets">{t.filter.managePresets}</a>
		</div>
	</form>

	<form method="POST" action="?/saveRules" use:enhance={rulesForm.enhance} class="stack">
		<h2>{t.filter.edit}</h2>
		<p class="muted">{t.filter.editHint}</p>
		{#key data.rulesKey}
			<RuleEditor rules={data.rules} activeConditions={data.activeConditions} values={data.values} error={refusedIn('rules')} />
		{/key}
		<div><Button disabled={rulesForm.pending}>{t.filter.saveRules}</Button></div>
	</form>

	<form method="POST" action="?/saveAsPreset" use:enhance={asPresetForm.enhance} class="section">
		<h2>{t.filter.saveAsPreset}</h2>
		<Field
			label={t.filter.presetName}
			id="preset-name"
			name="name"
			required
			maxlength={100}
			hint={t.filter.saveAsPresetHint}
			error={refusedIn('asPreset')?.message}
			value={refusedIn('asPreset')?.values?.name ?? ''}
		/>
		<div><Button variant="secondary" disabled={asPresetForm.pending}>{t.filter.saveAsPreset}</Button></div>
	</form>
</div>
<Toast message={saved === 'asPreset' ? t.filter.presetSaved : saved ? t.saved : null} />

<style>
	h2 {
		font: var(--type-h4);
	}
	.sentences {
		margin: 0;
		padding-left: var(--space-5);
		display: flex;
		flex-direction: column;
		gap: var(--space-2);
	}
	.inactive {
		color: var(--grey-600);
	}
	.reason {
		display: block;
		font: var(--type-small);
		color: var(--amber-700);
	}
</style>
