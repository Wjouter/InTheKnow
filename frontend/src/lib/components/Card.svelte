<script lang="ts">
	import type { Snippet } from 'svelte';

	let { overline, title, href, children }: { overline?: string; title?: string; href?: string; children?: Snippet } = $props();
</script>

<article class="card" class:link={href}>
	{#if overline}<div class="overline">{overline}</div>{/if}
	{#if title}
		<h3>{#if href}<a {href}>{title}</a>{:else}{title}{/if}</h3>
	{/if}
	{#if children}<div class="body">{@render children()}</div>{/if}
</article>

<style>
	.card {
		position: relative;
		background: var(--surface-card);
		box-shadow: inset 0 0 0 1px var(--border-subtle);
		border-radius: var(--radius-sm);
		padding: var(--space-6);
		display: flex;
		flex-direction: column;
		gap: var(--space-3);
		transition: box-shadow var(--dur-base);
	}
	.card.link:hover {
		box-shadow: var(--shadow-2);
	}
	.overline {
		font: var(--type-overline);
		letter-spacing: var(--ls-caps);
		text-transform: uppercase;
		color: var(--text-brand);
	}
	h3 {
		font: var(--type-h4);
		overflow-wrap: anywhere;
	}
	h3 a {
		color: var(--text-strong);
		text-decoration: none;
	}
	/* The whole card is the link's hit area; the link text stays the accessible name. */
	h3 a::after {
		content: '';
		position: absolute;
		inset: 0;
	}
	h3 a:hover {
		text-decoration: underline;
	}
	.body {
		display: flex;
		flex-direction: column;
		gap: var(--space-2);
	}
</style>
