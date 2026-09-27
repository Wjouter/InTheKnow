<script lang="ts">
	import CircleCheck from '@lucide/svelte/icons/circle-check';
	import { fade } from 'svelte/transition';

	// The result of an action ("Saved"). The status region is always there, so screen readers announce the message.
	let { message }: { message?: string | null } = $props();

	let shown = $state<string | null>(null);
	$effect(() => {
		// A newer result without a message (a refusal) hides the old one: its timer is cleared with the previous run.
		shown = message ?? null;
		if (!message) return;
		const timer = setTimeout(() => (shown = null), 4000);
		return () => clearTimeout(timer);
	});
</script>

<div class="region" role="status">
	{#if shown}
		<div class="toast" transition:fade={{ duration: 200 }}>
			<CircleCheck size={22} aria-hidden="true" />
			<span>{shown}</span>
		</div>
	{/if}
</div>

<style>
	.region {
		position: fixed;
		left: var(--space-4);
		right: var(--space-4);
		bottom: calc(var(--tabbar-h) + var(--space-4));
		display: flex;
		justify-content: center;
		pointer-events: none;
		z-index: 40;
	}
	.toast {
		display: flex;
		gap: var(--space-3);
		align-items: center;
		padding: 14px var(--space-4);
		background: var(--surface-dark);
		color: var(--white);
		border-radius: var(--radius-md);
		box-shadow: var(--shadow-3);
		max-width: 420px;
		font: 700 var(--fs-16) / 1.3 var(--font-body);
	}
	.toast :global(svg) {
		color: var(--green-100);
		flex: none;
	}
	@media (min-width: 768px) {
		.region {
			bottom: var(--space-6);
		}
	}
</style>
