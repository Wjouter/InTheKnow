<script lang="ts">
	import { enhance } from '$app/forms';
	import { page } from '$app/state';
	import Button from './Button.svelte';
	import Field from './Field.svelte';
	import Toast from './Toast.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	// Posts to ?/addLink on the page that includes it (FR-024, US2-1 to US2-3).
	let { form }: { form: unknown } = $props();
	const t = $derived(dicts[page.data.lang]);
	const s = submitter();
	const r = $derived(refusal(form));
	const added = $derived(form && typeof form === 'object' && 'added' in form);
</script>

<form method="POST" action="?/addLink" use:enhance={s.enhance} class="stack add">
	<h2>{t.calendars.addTitle}</h2>
	<Field
		label={t.calendars.link}
		id="url"
		name="url"
		type="url"
		inputmode="url"
		autocomplete="off"
		required
		hint={t.calendars.linkHint}
		error={r?.message}
		value={r?.values?.url ?? ''}
	/>
	<div><Button disabled={s.pending}>{s.pending ? t.calendars.checking : t.calendars.add}</Button></div>
</form>
<Toast message={added ? t.calendars.added : null} />

<style>
	.add h2 {
		font: var(--type-h4);
	}
</style>
