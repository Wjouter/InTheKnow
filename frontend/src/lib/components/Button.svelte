<script lang="ts">
	import ArrowRight from '@lucide/svelte/icons/arrow-right';
	import type { Snippet } from 'svelte';

	type Props = {
		variant?: 'primary' | 'secondary' | 'dark' | 'ghost' | 'inverse';
		size?: 'sm' | 'md' | 'lg';
		href?: string;
		type?: 'submit' | 'button' | 'reset';
		disabled?: boolean;
		name?: string;
		value?: string;
		formaction?: string;
		form?: string;
		fullWidth?: boolean;
		onclick?: (e: MouseEvent) => void;
		children: Snippet;
	};
	let { variant = 'primary', size = 'md', href, type = 'submit', fullWidth = false, children, ...rest }: Props = $props();
</script>

{#if href}
	<a {href} class="btn {variant} {size}" class:full={fullWidth}>{@render children()}<ArrowRight size={size === 'sm' ? 16 : 18} aria-hidden="true" /></a>
{:else}
	<button {type} class="btn {variant} {size}" class:full={fullWidth} {...rest}>{@render children()}</button>
{/if}

<style>
	.btn {
		display: inline-flex;
		align-items: center;
		justify-content: center;
		gap: var(--space-2);
		font-family: var(--font-body);
		font-weight: var(--fw-bold);
		border-radius: var(--radius-md);
		border: var(--border-w-strong) solid transparent;
		cursor: pointer;
		text-decoration: none;
		text-align: center;
		transition:
			background var(--dur-fast) var(--ease-standard),
			transform var(--dur-fast);
	}
	.btn:active:not(:disabled) {
		transform: translateY(1px);
	}
	.btn:disabled {
		opacity: 0.4;
		cursor: not-allowed;
	}
	.full {
		width: 100%;
	}
	/* sm stays at the 44px hit minimum (FR-043); the design's 36px would be too small to tap. */
	.sm {
		min-height: var(--hit-min);
		padding: 0 14px;
		font-size: var(--fs-14);
	}
	.md {
		min-height: var(--hit-min);
		padding: 0 var(--space-5);
		font-size: var(--fs-16);
	}
	.lg {
		min-height: 52px;
		padding: 0 26px;
		font-size: var(--fs-18);
	}
	.primary {
		background: var(--brand);
		color: var(--text-inverse);
		border-color: var(--brand);
	}
	.primary:hover:not(:disabled) {
		background: var(--brand-hover);
		border-color: var(--brand-hover);
		color: var(--text-inverse);
	}
	.primary:active:not(:disabled) {
		background: var(--brand-press);
		border-color: var(--brand-press);
	}
	.secondary {
		background: transparent;
		color: var(--text-brand);
		border-color: var(--brand);
	}
	.secondary:hover:not(:disabled),
	.ghost:hover:not(:disabled) {
		background: var(--brand-soft);
		color: var(--text-brand);
	}
	.dark {
		background: var(--surface-dark);
		color: var(--text-inverse);
		border-color: var(--surface-dark);
	}
	.dark:hover:not(:disabled) {
		background: var(--grey-700);
		border-color: var(--grey-700);
		color: var(--text-inverse);
	}
	.ghost {
		background: transparent;
		color: var(--text-brand);
	}
	.inverse {
		background: var(--white);
		color: var(--text-brand);
		border-color: var(--white);
	}
	.inverse:hover:not(:disabled) {
		background: var(--magenta-50);
		border-color: var(--magenta-50);
	}
</style>
