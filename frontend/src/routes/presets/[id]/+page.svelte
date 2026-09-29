<script lang="ts">
	import PageIntro from '$lib/components/PageIntro.svelte';
	import PresetEditor from '$lib/components/PresetEditor.svelte';
	import { dicts } from '$lib/i18n';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
</script>

<svelte:head><title>{data.preset.name} · InTheKnow</title></svelte:head>

<PageIntro title={data.preset.name} overline={t.presets.title} />
<div class="wrap stack">
	<p><a href="/presets">{t.presets.back}</a></p>
	<p class="muted">{t.presets.followers(data.preset.followers)}</p>
	<PresetEditor preset={data.preset} {form} showFollowers />
</div>
