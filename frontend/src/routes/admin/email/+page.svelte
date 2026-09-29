<script lang="ts">
	import Badge from '$lib/components/Badge.svelte';
	import { dicts } from '$lib/i18n';

	let { data } = $props();
	const t = $derived(dicts[data.lang]);
	const w = $derived(t.admin.email);
</script>

<svelte:head><title>{w.tab} · InTheKnow</title></svelte:head>

<div class="stack">
	{#each data.types as c (c.type)}
		<section class="section">
			<div class="head">
				<h2>{c.type === 'email' ? w.email : c.type}</h2>
				<Badge tone={c.configured ? 'green' : 'neutral'}>{c.configured ? w.configured : w.notConfigured}</Badge>
			</div>
			<dl>
				<div><dt>{w.lastAttempt}</dt><dd>{c.lastAttempt ?? w.noAttempt}</dd></div>
				{#if c.lastAttempt}
					<div><dt>{w.result}</dt><dd>{c.lastOk ? w.ok : (c.lastReason ?? w.failed)}</dd></div>
				{/if}
				<div><dt>{w.failures}</dt><dd>{c.failures}</dd></div>
				{#if c.failingSince}<div><dt>{w.failingSince}</dt><dd>{c.failingSince}</dd></div>{/if}
				<div><dt>{w.waiting}</dt><dd>{c.waiting}</dd></div>
			</dl>
		</section>
	{:else}
		<p class="muted">{w.none}</p>
	{/each}
</div>

<style>
	.head {
		display: flex;
		gap: var(--space-3);
		align-items: center;
		flex-wrap: wrap;
	}
	h2 {
		font: var(--type-h4);
	}
	dl {
		margin: 0;
		display: grid;
		grid-template-columns: minmax(0, 1fr);
		gap: var(--space-2);
	}
	dl div {
		display: flex;
		flex-wrap: wrap;
		gap: var(--space-1) var(--space-3);
	}
	dt {
		color: var(--text-muted);
		min-width: 180px;
	}
	dd {
		margin: 0;
		color: var(--text-strong);
	}
</style>
