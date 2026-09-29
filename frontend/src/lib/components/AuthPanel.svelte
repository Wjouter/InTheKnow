<script lang="ts">
	import type { Snippet } from 'svelte';
	import { page } from '$app/state';
	import { dicts } from '$lib/i18n';

	// The design system's sign-in layout: the solid magenta block (no photo, research R18) beside the form.
	let { title, intro, children }: { title: string; intro?: string; children: Snippet } = $props();
	const t = $derived(dicts[page.data.lang]);
</script>

<div class="panel">
	<div class="brand">
		<div class="block">
			<p class="headline">{t.auth.headline}</p>
			<p>{t.auth.tagline}</p>
		</div>
	</div>
	<div class="content">
		<h1>{title}</h1>
		{#if intro}<p>{intro}</p>{/if}
		{@render children()}
	</div>
</div>

<style>
	.panel {
		display: grid;
		grid-template-columns: minmax(0, 1fr);
	}
	.brand {
		background: var(--surface-section);
		padding: var(--space-4);
	}
	.block {
		background: var(--brand);
		color: var(--text-inverse);
		padding: var(--space-5) var(--space-6);
	}
	.headline {
		font: var(--type-h3);
		color: var(--text-inverse);
	}
	.block p + p {
		margin-top: var(--space-2);
		font: var(--type-body);
	}
	.content {
		padding: var(--space-8) var(--space-4) var(--space-12);
		display: flex;
		flex-direction: column;
		gap: var(--space-5);
		max-width: 520px;
		width: 100%;
	}
	h1 {
		font: var(--type-h3);
	}
	@media (min-width: 900px) {
		.panel {
			grid-template-columns: minmax(0, 1.2fr) minmax(0, 1fr);
			min-height: calc(100vh - var(--header-h));
		}
		.brand {
			display: flex;
			align-items: flex-end;
			padding: var(--space-12);
		}
		.block {
			padding: 28px var(--space-8);
			max-width: 440px;
		}
		.headline {
			font: var(--type-h1);
		}
		.block p + p {
			font: var(--type-body-lg);
		}
		.content {
			justify-content: center;
			padding: var(--space-12) var(--space-16);
		}
	}
</style>
