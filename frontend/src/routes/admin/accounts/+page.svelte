<script lang="ts">
	import { enhance } from '$app/forms';
	import Badge from '$lib/components/Badge.svelte';
	import Button from '$lib/components/Button.svelte';
	import ConfirmAction from '$lib/components/ConfirmAction.svelte';
	import Field from '$lib/components/Field.svelte';
	import Toast from '$lib/components/Toast.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const w = $derived(t.admin.accounts);
	const s = submitter();
	const r = $derived(refusal(form));
	const done = $derived(form && 'done' in form ? (form.done as keyof typeof w.done) : null);
</script>

<svelte:head><title>{w.tab} · InTheKnow</title></svelte:head>

<div class="stack">
	<form method="GET" class="row search">
		<Field label={w.search} id="q" name="q" type="search" value={data.q} />
		<Button variant="secondary">{w.searchSubmit}</Button>
	</form>
	{#if r}<p class="notice error" role="alert">{r.message}</p>{/if}

	{#if data.accounts.length}
		<ul class="cards">
			{#each data.accounts as a (a.id)}
				<li class="section">
					<div class="head">
						<strong>{a.email}</strong>
						{#if a.role === 'admin'}<Badge tone="magenta">{w.admin}</Badge>{/if}
						<Badge tone={a.confirmed ? 'green' : 'amber'}>{a.confirmed ? w.confirmed : w.unconfirmed}</Badge>
						{#if a.disabled}<Badge tone="red">{w.disabled}</Badge>{/if}
						<Badge tone={a.twoFactor ? 'green' : 'neutral'}>{a.twoFactor ? w.twoFactorOn : w.twoFactorOff}</Badge>
					</div>
					<p class="muted">{w.created(a.created)}</p>
					<div class="row">
						{#if a.role === 'user'}
							<form method="POST" action="?/setRole" use:enhance={s.enhance}>
								<input type="hidden" name="accountId" value={String(a.id)} />
								<input type="hidden" name="role" value="admin" />
								<Button size="sm" variant="secondary" disabled={s.pending}>{w.makeAdmin}</Button>
							</form>
						{:else}
							<ConfirmAction label={w.makeUser} title={w.makeUserTitle} confirm={w.makeUser} action="?/setRole" fields={{ accountId: String(a.id), role: 'user' }} enhanceWith={s.enhance} pending={s.pending} size="sm">
								<p>{w.makeUserBody(a.email)}</p>
							</ConfirmAction>
						{/if}
						{#if a.disabled}
							<form method="POST" action="?/enable" use:enhance={s.enhance}>
								<input type="hidden" name="accountId" value={String(a.id)} />
								<Button size="sm" variant="secondary" disabled={s.pending}>{w.enable}</Button>
							</form>
						{:else}
							<ConfirmAction label={w.disable} title={w.disableTitle} confirm={w.disable} action="?/disable" fields={{ accountId: String(a.id) }} enhanceWith={s.enhance} pending={s.pending} size="sm">
								<p>{w.disableBody(a.email)}</p>
							</ConfirmAction>
						{/if}
						{#if a.twoFactor}
							<ConfirmAction label={w.remove2fa} title={w.remove2faTitle} confirm={w.remove2fa} action="?/remove2fa" fields={{ accountId: String(a.id) }} enhanceWith={s.enhance} pending={s.pending} size="sm">
								<p>{w.remove2faBody(a.email)}</p>
							</ConfirmAction>
						{/if}
						<ConfirmAction label={w.delete} title={w.deleteTitle} confirm={w.delete} action="?/delete" fields={{ accountId: String(a.id) }} enhanceWith={s.enhance} pending={s.pending} variant="ghost" size="sm">
							<p>{w.deleteBody(a.email)}</p>
						</ConfirmAction>
					</div>
				</li>
			{/each}
		</ul>
	{:else}
		<p class="muted">{w.none}</p>
	{/if}
	{#if data.next}
		<div><Button href="?{new URLSearchParams({ ...(data.q ? { q: data.q } : {}), after: String(data.next) })}" variant="secondary">{t.admin.nextPage}</Button></div>
	{/if}
</div>
<Toast message={done ? w.done[done] : null} />

<style>
	.search {
		align-items: flex-end;
	}
	.search :global(.field) {
		flex: 1;
		min-width: 200px;
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
		margin-right: var(--space-2);
	}
</style>
