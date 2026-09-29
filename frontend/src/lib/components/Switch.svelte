<script lang="ts">
	// A native checkbox with role="switch", so it posts with the form (research R18).
	let {
		id,
		name,
		label,
		description,
		checked = $bindable(false),
		value = 'true',
		disabled = false,
		onchange
	}: {
		id: string;
		name?: string;
		label: string;
		description?: string;
		checked?: boolean;
		value?: string;
		disabled?: boolean;
		onchange?: (e: Event) => void;
	} = $props();
</script>

<div class="switch" class:disabled>
	<label for={id}>
		<span class="label">{label}</span>
		{#if description}<span class="description" id="{id}-description">{description}</span>{/if}
	</label>
	<input
		type="checkbox"
		role="switch"
		{id}
		{name}
		{value}
		{disabled}
		bind:checked
		{onchange}
		aria-describedby={description ? `${id}-description` : undefined}
	/>
</div>

<style>
	.switch {
		display: flex;
		align-items: center;
		justify-content: space-between;
		gap: var(--space-4);
		min-height: var(--hit-min);
	}
	.disabled {
		opacity: 0.45;
	}
	label {
		display: flex;
		flex-direction: column;
		gap: 2px;
		cursor: pointer;
	}
	.label {
		font: 600 var(--fs-16) / 1.3 var(--font-body);
		color: var(--text-strong);
	}
	.description {
		font: var(--type-small);
		color: var(--text-muted);
	}
	input {
		appearance: none;
		flex: none;
		width: 48px;
		height: 28px;
		margin: 8px 0;
		border-radius: var(--radius-pill);
		padding: 3px;
		cursor: pointer;
		background: var(--grey-500);
		transition: background var(--dur-base) var(--ease-standard);
		position: relative;
	}
	input::before {
		content: '';
		display: block;
		width: 22px;
		height: 22px;
		border-radius: 50%;
		background: var(--white);
		box-shadow: var(--shadow-1);
		transition: transform var(--dur-base) var(--ease-standard);
	}
	input:checked {
		background: var(--brand);
	}
	input:checked::before {
		transform: translateX(20px);
	}
	input:disabled {
		cursor: not-allowed;
	}
	@media (forced-colors: active) {
		input {
			border: 1px solid ButtonText;
		}
		input::before {
			background: ButtonText;
		}
	}
</style>
