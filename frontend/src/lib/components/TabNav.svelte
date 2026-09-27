<script lang="ts">
	import type { Snippet } from 'svelte';
	import { page } from '$app/state';

	// Each tab is its own address, so this is a nav of links, not an ARIA tab widget (research R18, FR-048, FR-049).
	let { label, items, children }: { label: string; items: { href: string; label: string }[]; children: Snippet } = $props();
</script>

<div class="tabs">
	<nav aria-label={label}>
		<ul>
			{#each items as item (item.href)}
				<li>
					<a href={item.href} aria-current={page.url.pathname === item.href ? 'page' : undefined}>{item.label}</a>
				</li>
			{/each}
		</ul>
	</nav>
	<div class="panel">{@render children()}</div>
</div>

<style>
	.tabs {
		display: flex;
		flex-direction: column;
		gap: var(--space-6);
	}
	ul {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		flex-wrap: wrap;
		gap: var(--space-1) var(--space-2);
		border-bottom: 1px solid var(--border-subtle);
	}
	a {
		position: relative;
		display: inline-flex;
		align-items: center;
		min-height: var(--hit-min);
		padding: 0 var(--space-3);
		font: 700 var(--fs-16) / 1.2 var(--font-body);
		color: var(--text-muted);
		text-decoration: none;
	}
	a:hover {
		color: var(--text-strong);
	}
	a[aria-current='page'] {
		color: var(--text-strong);
	}
	a[aria-current='page']::after {
		content: '';
		position: absolute;
		left: var(--space-2);
		right: var(--space-2);
		bottom: -1px;
		height: 3px;
		background: var(--brand);
	}
	.panel {
		min-width: 0;
	}
	@media (min-width: 768px) {
		.tabs {
			display: grid;
			grid-template-columns: 220px minmax(0, 1fr);
			gap: var(--space-8);
			align-items: start;
		}
		ul {
			flex-direction: column;
			border-bottom: 0;
			border-left: 1px solid var(--border-subtle);
		}
		a {
			width: 100%;
			padding: 0 var(--space-4);
		}
		a[aria-current='page']::after {
			left: -1px;
			right: auto;
			top: var(--space-2);
			bottom: var(--space-2);
			width: 3px;
			height: auto;
		}
	}
</style>
