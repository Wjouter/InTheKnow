<script lang="ts">
	import { enhance } from '$app/forms';
	import { page } from '$app/state';
	import Badge from './Badge.svelte';
	import Button from './Button.svelte';
	import Field from './Field.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	type Item = { id: number; name: string; isDefault: boolean; followers: number | null; sentences: string[] };
	// A list of presets as cards: editable ones link to their page; others can only be copied (US3-7).
	let { items, href, copy = false, form }: { items: Item[]; href?: string; copy?: boolean; form?: unknown } = $props();
	const t = $derived(dicts[page.data.lang]);
	const s = submitter();
	const r = $derived(refusal(form));
</script>

<ul class="presets">
	{#each items as p (p.id)}
		<li class="section">
			<div class="head">
				<h3>{#if href}<a href="{href}/{p.id}">{p.name}</a>{:else}{p.name}{/if}</h3>
				{#if p.isDefault}<Badge tone="magenta">{t.presets.defaultBadge}</Badge>{/if}
			</div>
			{#if p.followers !== null}<p class="muted">{t.presets.followers(p.followers)}</p>{/if}
			{#if p.sentences.length}
				<ul class="sentences">{#each p.sentences as sentence, i (i)}<li>{sentence}</li>{/each}</ul>
			{:else}
				<p class="muted">{t.rules.editor.noRules}</p>
			{/if}
			{#if copy}
				<form method="POST" action="?/copy" use:enhance={s.enhance} class="row copy">
					<input type="hidden" name="copyOf" value={String(p.id)} />
					<Field
						label={t.presets.copyLabel}
						id="copy-{p.id}"
						name="name"
						required
						maxlength={100}
						value={r?.values?.form === `copy-${p.id}` ? (r.values.name ?? '') : t.presets.copyName(p.name)}
						error={r?.values?.form === `copy-${p.id}` ? r.message : undefined}
					/>
					<Button variant="secondary" disabled={s.pending}>{t.presets.copy}</Button>
				</form>
			{/if}
		</li>
	{/each}
</ul>

<style>
	.presets {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		flex-direction: column;
		gap: var(--space-4);
	}
	.head {
		display: flex;
		gap: var(--space-3);
		align-items: center;
		flex-wrap: wrap;
	}
	h3 {
		font: var(--type-h4);
		overflow-wrap: anywhere;
	}
	.sentences {
		margin: 0;
		padding-left: var(--space-5);
		display: flex;
		flex-direction: column;
		gap: var(--space-1);
	}
	.copy {
		align-items: flex-end;
	}
	.copy :global(.field) {
		flex: 1;
		min-width: 200px;
	}
</style>
