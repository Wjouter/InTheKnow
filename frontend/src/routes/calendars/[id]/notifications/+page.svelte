<script lang="ts">
	import { enhance } from '$app/forms';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import Switch from '$lib/components/Switch.svelte';
	import Toast from '$lib/components/Toast.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { fieldError, formError, refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const s = submitter();
	const r = $derived(refusal(form));
</script>

<form method="POST" action="?/save" use:enhance={s.enhance} class="stack">
	<h2>{t.calendarNotifications.title}</h2>
	{#if formError(form, 'invalid_label')}<p class="notice error" role="alert">{formError(form, 'invalid_label')}</p>{/if}
	<Switch
		id="notify"
		name="notify"
		label={t.calendarNotifications.notify}
		description={t.calendarNotifications.notifyHint}
		checked={r?.values?.notify !== undefined ? r.values.notify === 'true' : data.notify}
	/>
	<Field
		label={t.calendarNotifications.label}
		id="label"
		name="label"
		maxlength={100}
		hint={t.calendarNotifications.labelHint(data.name)}
		error={fieldError(form, 'invalid_label')}
		value={r?.values?.label ?? data.label ?? ''}
	/>
	<div><Button disabled={s.pending}>{t.save}</Button></div>
	<p class="notice">{t.calendarNotifications.where} <a href="/notifications">{t.calendarNotifications.whereLink}</a></p>
</form>
<Toast message={form && 'saved' in form ? t.saved : null} />

<style>
	h2 {
		font: var(--type-h4);
	}
</style>
