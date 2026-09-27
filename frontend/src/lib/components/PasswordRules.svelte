<script lang="ts">
	import { page } from '$app/state';
	import { dicts } from '$lib/i18n';

	// The password rules, shown before submitting (FR-017). The broken rule is marked after a refusal (US1-3).
	let { id, reason }: { id: string; reason?: string } = $props();
	const t = $derived(dicts[page.data.lang]);
	const rules = $derived([
		{ reason: 'password_length', text: t.auth.ruleLength },
		{ reason: 'password_common', text: t.auth.ruleCommon },
		{ reason: 'password_is_email', text: t.auth.ruleEmail }
	]);
</script>

<div class="rules" {id}>
	<span>{t.auth.rulesTitle}</span>
	<ul>
		{#each rules as rule (rule.reason)}
			<li class:broken={rule.reason === reason}>
				{rule.text}{#if rule.reason === reason}<span class="visually-hidden"> ({t.auth.ruleBroken})</span>{/if}
			</li>
		{/each}
	</ul>
</div>

<style>
	.rules {
		font: var(--type-small);
		color: var(--text-muted);
	}
	ul {
		margin: var(--space-1) 0 0;
		padding-left: var(--space-5);
	}
	.broken {
		color: var(--status-cancelled);
		font-weight: var(--fw-bold);
	}
</style>
