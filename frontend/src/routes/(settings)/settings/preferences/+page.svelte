<script lang="ts">
	import { enhance } from '$app/forms';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import Toast from '$lib/components/Toast.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { fieldError, formError, refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const s = submitter();
	const r = $derived(refusal(form));
	const homePage = $derived(r?.values?.homePage ?? data.homePage);
</script>

<svelte:head><title>{t.settings.preferences} · InTheKnow</title></svelte:head>

<form method="POST" action="?/save" use:enhance={s.enhance} class="stack">
	<h2>{t.settings.preferences}</h2>
	{#if formError(form, 'invalid_language', 'invalid_time_zone', 'invalid_home_page')}
		<p class="notice error" role="alert">{formError(form, 'invalid_language', 'invalid_time_zone', 'invalid_home_page')}</p>
	{/if}
	<Field label={t.auth.language} id="language" error={fieldError(form, 'invalid_language')}>
		{#snippet children({ id, describedby })}
			<select {id} name="language" aria-describedby={describedby} value={r?.values?.language ?? data.language}>
				<option value="en" lang="en">English</option>
				<option value="nl" lang="nl">Nederlands</option>
			</select>
		{/snippet}
	</Field>
	<Field label={t.preferences.timeZone} id="timeZone" error={fieldError(form, 'invalid_time_zone')}>
		{#snippet children({ id, describedby })}
			<select {id} name="timeZone" aria-describedby={describedby} value={r?.values?.timeZone ?? data.timeZone ?? ''}>
				<option value="">{data.defaultTimeZone ? t.preferences.defaultZone(data.defaultTimeZone) : t.preferences.defaultZoneUnnamed}</option>
				{#each data.timeZones as zone (zone)}<option value={zone}>{zone}</option>{/each}
			</select>
		{/snippet}
	</Field>
	<p class="muted">{t.preferences.emailsFollow}</p>
	<fieldset class="stack">
		<legend>{t.preferences.homePage}</legend>
		<label class="choice"><input type="radio" name="homePage" value="changes" checked={homePage === 'changes'} />{t.preferences.homeChanges}</label>
		<label class="choice"><input type="radio" name="homePage" value="combined" checked={homePage === 'combined'} />{t.preferences.homeCombined}</label>
		{#if fieldError(form, 'invalid_home_page')}<span class="form-error">{fieldError(form, 'invalid_home_page')}</span>{/if}
	</fieldset>
	<div><Button disabled={s.pending}>{t.save}</Button></div>
</form>
<Toast message={form && 'saved' in form ? t.saved : null} />

<style>
	h2 {
		font: var(--type-h3);
	}
	fieldset {
		border: 0;
		padding: 0;
		margin: 0;
		gap: 0;
	}
	legend {
		font: var(--type-label);
		color: var(--text-strong);
		margin-bottom: var(--space-2);
	}
</style>
