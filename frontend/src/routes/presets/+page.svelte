<script lang="ts">
	import { enhance } from '$app/forms';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import PageIntro from '$lib/components/PageIntro.svelte';
	import PresetList from '$lib/components/PresetList.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const s = submitter();
	const r = $derived(refusal(form));
</script>

<svelte:head><title>{t.presets.title} · InTheKnow</title></svelte:head>

<PageIntro title={t.presets.title} intro={t.presets.intro} />
<div class="wrap stack">
	<h2>{t.presets.yours}</h2>
	{#if data.personal.length}<PresetList items={data.personal} href="/presets" />{:else}<p class="muted">{t.presets.none}</p>{/if}

	<form method="POST" action="?/create" use:enhance={s.enhance} class="section">
		<h2>{t.presets.create}</h2>
		<Field
			label={t.presets.name}
			id="create-name"
			name="name"
			required
			maxlength={100}
			value={r?.values?.form === 'create' ? (r.values.name ?? '') : ''}
			error={r?.values?.form === 'create' ? r.message : undefined}
		/>
		<div><Button disabled={s.pending}>{t.presets.createSubmit}</Button></div>
	</form>

	<h2>{t.presets.forEveryone}</h2>
	<PresetList items={data.global} copy {form} />
</div>

<style>
	h2 {
		font: var(--type-h3);
	}
</style>
