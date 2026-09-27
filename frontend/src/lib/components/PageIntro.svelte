<script lang="ts">
	import type { Snippet } from 'svelte';

	let { title, overline, intro, children }: { title: string; overline?: string; intro?: string; children?: Snippet } = $props();
</script>

<div class="intro">
	<div class="inner">
		<div class="text">
			{#if overline}<div class="overline">{overline}</div>{/if}
			<h1>{title}</h1>
			{#if intro}<p>{intro}</p>{/if}
		</div>
		{#if children}{@render children()}{/if}
	</div>
</div>

<style>
	.intro {
		background: var(--brand);
		color: var(--text-inverse);
	}
	.inner {
		max-width: var(--container-max);
		margin: 0 auto;
		padding: var(--space-8) var(--space-4) var(--space-6);
		display: flex;
		align-items: flex-end;
		gap: var(--space-6);
		flex-wrap: wrap;
	}
	.text {
		flex: 1;
		min-width: 0;
	}
	.overline {
		font: var(--type-overline);
		letter-spacing: var(--ls-caps);
		text-transform: uppercase;
	}
	h1 {
		font: var(--type-h2);
		color: var(--text-inverse);
		margin-top: var(--space-2);
		overflow-wrap: anywhere;
	}
	p {
		margin-top: 10px;
		font: var(--type-body-lg);
	}
	@media (min-width: 768px) {
		.inner {
			padding: 36px var(--gutter) var(--space-8);
		}
		h1 {
			font: var(--type-h1);
		}
	}
</style>
