<script lang="ts">
	import { enhance } from '$app/forms';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import PresetList from '$lib/components/PresetList.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const s = submitter();
	const r = $derived(refusal(form));
</script>

<svelte:head><title>{t.admin.presets.tab} · InTheKnow</title></svelte:head>

<div class="stack">
	<p>{t.admin.presets.intro}</p>
	<PresetList items={data.presets} href="/admin/presets" />
	<form method="POST" action="?/create" use:enhance={s.enhance} class="section">
		<h2>{t.presets.create}</h2>
		<Field label={t.presets.name} id="create-name" name="name" required maxlength={100} value={r?.values?.name ?? ''} error={r?.message} />
		<div><Button disabled={s.pending}>{t.presets.createSubmit}</Button></div>
	</form>
</div>

<style>
	h2 {
		font: var(--type-h4);
	}
</style>
