<script lang="ts">
	import Badge from '$lib/components/Badge.svelte';
	import Button from '$lib/components/Button.svelte';
	import Field from '$lib/components/Field.svelte';
	import { dicts } from '$lib/i18n';

	let { data } = $props();
	const t = $derived(dicts[data.lang]);
	const w = $derived(t.admin.calendars);
	const nextHref = $derived(
		`?${new URLSearchParams({ ...(data.status ? { status: data.status } : {}), ...(data.overdue ? { overdue: 'true' } : {}), after: String(data.next) })}`
	);
</script>

<svelte:head><title>{w.tab} · InTheKnow</title></svelte:head>

<div class="stack">
	<form method="GET" class="row filters">
		<Field label={w.status} id="status">
			{#snippet children({ id })}
				<select {id} name="status" value={data.status}>
					<option value="">{w.all}</option>
					<option value="ok">{w.ok}</option>
					<option value="failing">{w.failing}</option>
				</select>
			{/snippet}
		</Field>
		<label class="choice"><input type="checkbox" name="overdue" value="true" checked={data.overdue} />{w.overdue}</label>
		<Button variant="secondary">{w.filter}</Button>
	</form>

	{#if data.calendars.length}
		<ul class="cards">
			{#each data.calendars as c (c.id)}
				<li class="section">
					<div class="head">
						<strong>{c.name}</strong>
						<Badge tone={c.status === 'ok' ? 'green' : 'red'}>{c.status === 'ok' ? w.ok : w.failing}</Badge>
					</div>
					{#if c.error}<p>{c.error}{c.failures ? ` ${t.calendars.failures(c.failures)}` : ''}</p>{/if}
					<p class="muted">
						{w.counts(c.subscribers, c.validLinks, c.totalLinks)}
						· {c.lastChecked ? `${t.calendars.lastChecked} ${c.lastChecked}` : t.calendars.states.notChecked}
						· {t.calendars.nextCheck} {c.nextCheck}
					</p>
				</li>
			{/each}
		</ul>
	{:else}
		<p class="muted">{w.none}</p>
	{/if}
	{#if data.next}<div><Button href={nextHref} variant="secondary">{t.admin.nextPage}</Button></div>{/if}
</div>

<style>
	.filters {
		align-items: flex-end;
	}
	.cards {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		flex-direction: column;
		gap: var(--space-3);
	}
	.head {
		display: flex;
		flex-wrap: wrap;
		gap: var(--space-2);
		align-items: center;
	}
	strong {
		color: var(--text-strong);
		overflow-wrap: anywhere;
	}
</style>
