<script lang="ts">
	import X from '@lucide/svelte/icons/x';
	import type { Snippet } from 'svelte';
	import { page } from '$app/state';
	import { dicts } from '$lib/i18n';

	// A native modal <dialog>: focus trap, Esc and the scrim come with it (research R18).
	let {
		open = $bindable(false),
		title,
		children,
		actions
	}: { open?: boolean; title: string; children?: Snippet; actions?: Snippet } = $props();

	const t = $derived(dicts[page.data.lang]);
	let el: HTMLDialogElement | undefined = $state();
	const id = $props.id();

	$effect(() => {
		if (!el) return;
		if (open && !el.open) el.showModal();
		if (!open && el.open) el.close();
	});
</script>

<dialog bind:this={el} onclose={() => (open = false)} aria-labelledby="{id}-title">
	<div class="head">
		<h2 id="{id}-title">{title}</h2>
		<button type="button" class="close" onclick={() => (open = false)} aria-label={t.close}><X size={20} aria-hidden="true" /></button>
	</div>
	{#if children}<div class="body">{@render children()}</div>{/if}
	{#if actions}<div class="actions">{@render actions()}</div>{/if}
</dialog>

<style>
	dialog {
		width: calc(100% - 2 * var(--space-4));
		max-width: 480px;
		padding: 0;
		border: 0;
		border-top: 6px solid var(--brand);
		border-radius: var(--radius-md);
		box-shadow: var(--shadow-3);
		background: var(--white);
		color: var(--text-body);
	}
	dialog::backdrop {
		background: var(--overlay-scrim);
	}
	.head {
		display: flex;
		align-items: flex-start;
		gap: var(--space-2);
		padding: var(--space-5) var(--space-3) 0 var(--space-6);
	}
	h2 {
		flex: 1;
		font: var(--type-h3);
		margin-top: 6px;
	}
	.close {
		flex: none;
		width: var(--hit-min);
		height: var(--hit-min);
		display: inline-flex;
		align-items: center;
		justify-content: center;
		border: 0;
		background: transparent;
		color: var(--text-strong);
		border-radius: var(--radius-md);
		cursor: pointer;
	}
	.close:hover {
		background: var(--brand-soft);
	}
	.body {
		padding: var(--space-3) var(--space-6) var(--space-6);
		display: flex;
		flex-direction: column;
		gap: var(--space-3);
	}
	.actions {
		display: flex;
		gap: var(--space-3);
		justify-content: flex-end;
		flex-wrap: wrap;
		padding: 0 var(--space-6) var(--space-6);
	}
</style>
