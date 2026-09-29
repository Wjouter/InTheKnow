<script lang="ts">
	import { enhance } from '$app/forms';
	import { page } from '$app/state';
	import type { SubmitFunction } from '@sveltejs/kit';
	import type { Snippet } from 'svelte';
	import Button from './Button.svelte';
	import Dialog from './Dialog.svelte';
	import { dicts } from '$lib/i18n';

	// A destructive action behind a confirmation dialog (US2-10, US5-8, US8-4). The dialog's form posts the action.
	let {
		label,
		title,
		confirm,
		action,
		fields = {},
		enhanceWith,
		pending = false,
		variant = 'secondary',
		size = 'md',
		children
	}: {
		label: string;
		title: string;
		confirm: string;
		action: string;
		fields?: Record<string, string>;
		enhanceWith: SubmitFunction;
		pending?: boolean;
		variant?: 'primary' | 'secondary' | 'ghost' | 'dark';
		size?: 'sm' | 'md';
		children?: Snippet;
	} = $props();
	const t = $derived(dicts[page.data.lang]);
	let open = $state(false);
</script>

<Button type="button" {variant} {size} onclick={() => (open = true)}>{label}</Button>
<Dialog bind:open {title} {children}>
	{#snippet actions()}
		<Button type="button" variant="ghost" onclick={() => (open = false)}>{t.cancel}</Button>
		<form method="POST" {action} use:enhance={enhanceWith} onsubmit={() => (open = false)}>
			{#each Object.entries(fields) as [name, value] (name)}<input type="hidden" {name} {value} />{/each}
			<Button variant="dark" disabled={pending}>{confirm}</Button>
		</form>
	{/snippet}
</Dialog>
