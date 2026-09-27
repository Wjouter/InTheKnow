<script lang="ts">
	import type { Snippet } from 'svelte';

	let { tone = 'neutral', children }: { tone?: 'neutral' | 'green' | 'amber' | 'red' | 'blue' | 'magenta'; children: Snippet } =
		$props();
</script>

<span class="badge {tone}">{@render children()}</span>

<style>
	.badge {
		display: inline-flex;
		align-items: center;
		min-height: 22px;
		padding: 0 var(--space-2);
		border-radius: var(--radius-sm);
		font: var(--type-overline);
		letter-spacing: var(--ls-caps);
		text-transform: uppercase;
		white-space: nowrap;
	}
	.neutral {
		color: var(--grey-700);
		background: var(--grey-100);
	}
	/* Amber and green use the 4.5:1 shades (research R18). */
	.green {
		color: var(--green-700);
		background: var(--status-new-soft);
	}
	.amber {
		color: var(--amber-700);
		background: var(--status-moved-soft);
	}
	.red {
		color: var(--status-cancelled);
		background: var(--status-cancelled-soft);
	}
	.blue {
		color: var(--status-info);
		background: var(--status-info-soft);
	}
	.magenta {
		color: var(--text-brand);
		background: var(--brand-soft);
	}
</style>
