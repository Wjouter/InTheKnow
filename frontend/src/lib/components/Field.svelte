<script lang="ts">
	import type { Snippet } from 'svelte';
	import type { HTMLInputAttributes } from 'svelte/elements';
	import { page } from '$app/state';
	import Eye from '@lucide/svelte/icons/eye';
	import EyeOff from '@lucide/svelte/icons/eye-off';
	import { dicts } from '$lib/i18n';

	// A labelled input, or a custom control through `children` (which receives the ids to wire up).
	type Props = Omit<HTMLInputAttributes, 'children'> & {
		label: string;
		id: string;
		hint?: string;
		error?: string;
		value?: string | null;
		children?: Snippet<[{ id: string; describedby: string | undefined; invalid: boolean }]>;
	};
	let { label, id, hint, error, value = $bindable(), children, ...rest }: Props = $props();

	const t = $derived(dicts[page.data.lang]);
	let shown = $state(false);
	const describedby = $derived([hint && `${id}-hint`, error && `${id}-error`].filter(Boolean).join(' ') || undefined);
</script>

<div class="field">
	<label for={id}>{label}</label>
	{#if children}
		{@render children({ id, describedby, invalid: Boolean(error) })}
	{:else if rest.type === 'password'}
		<div class="reveal">
			<input {id} bind:value aria-describedby={describedby} aria-invalid={error ? 'true' : undefined} {...rest} type={shown ? 'text' : 'password'} />
			<button type="button" aria-label={t.auth.showPassword} aria-pressed={shown} aria-controls={id} onclick={() => (shown = !shown)}>
				{#if shown}<EyeOff size={20} aria-hidden="true" />{:else}<Eye size={20} aria-hidden="true" />{/if}
			</button>
		</div>
	{:else}
		<input {id} bind:value aria-describedby={describedby} aria-invalid={error ? 'true' : undefined} {...rest} />
	{/if}
	{#if hint}<span class="hint" id="{id}-hint">{hint}</span>{/if}
	{#if error}<span class="error" id="{id}-error">{error}</span>{/if}
</div>

<style>
	.field {
		display: flex;
		flex-direction: column;
		gap: 6px;
		min-width: 0;
	}
	label {
		font: var(--type-label);
		color: var(--text-strong);
	}
	/* The 1px --grey-500 border reaches 3:1 non-text contrast; the design's --grey-300 is 1.6:1 (research R18). */
	.field :global(input:not([type='checkbox']):not([type='radio'])),
	.field :global(select),
	.field :global(textarea) {
		width: 100%;
		min-height: 48px;
		padding: 0 14px;
		font: var(--type-body);
		color: var(--text-strong);
		background: var(--white);
		border: var(--border-w) solid var(--grey-500);
		border-radius: var(--radius-md);
		transition: box-shadow var(--dur-fast);
	}
	.field :global(textarea) {
		padding: var(--space-3) 14px;
		min-height: 120px;
	}
	.field :global(input:focus),
	.field :global(select:focus),
	.field :global(textarea:focus) {
		border-color: var(--brand);
	}
	.field :global([aria-invalid='true']) {
		border-color: var(--status-cancelled);
	}
	.field :global(input:disabled) {
		background: var(--grey-100);
	}
	.reveal {
		position: relative;
	}
	/* Outweighs the general input rule above, which sets padding on both sides. */
	.field .reveal :global(input:not([type='checkbox'])) {
		padding-right: 48px;
	}
	.reveal button {
		position: absolute;
		inset: 0 0 0 auto;
		width: 48px;
		display: grid;
		place-items: center;
		color: var(--text-muted);
		background: none;
		border: 0;
		border-radius: var(--radius-md);
		cursor: pointer;
	}
	.reveal button:hover {
		color: var(--text-strong);
	}
	.hint {
		font: var(--type-small);
		color: var(--text-muted);
	}
	.error {
		font: var(--type-small);
		color: var(--status-cancelled);
	}
</style>
