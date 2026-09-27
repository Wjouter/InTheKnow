<script lang="ts">
	import PresetEditor from '$lib/components/PresetEditor.svelte';
	import { dicts } from '$lib/i18n';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
</script>

<svelte:head><title>{data.preset.name} · InTheKnow</title></svelte:head>

<div class="stack">
	<p><a href="/admin/presets">{t.presets.back}</a></p>
	<h2>{data.preset.name}</h2>
	<PresetEditor preset={data.preset} {form} showFollowers={false} />
</div>

<style>
	h2 {
		font: var(--type-h3);
	}
</style>
