<script lang="ts">
	import { enhance } from '$app/forms';
	import { beforeNavigate } from '$app/navigation';
	import Badge from '$lib/components/Badge.svelte';
	import Button from '$lib/components/Button.svelte';
	import ConfirmAction from '$lib/components/ConfirmAction.svelte';
	import Field from '$lib/components/Field.svelte';
	import Switch from '$lib/components/Switch.svelte';
	import Toast from '$lib/components/Toast.svelte';
	import { submitter } from '$lib/enhance.svelte';
	import { dicts } from '$lib/i18n';
	import { refusal } from '$lib/refusal';

	let { data, form } = $props();
	const t = $derived(dicts[data.lang]);
	const w = $derived(t.admin.signUp);
	const s = submitter();
	const r = $derived(refusal(form));
	const refusedIn = (name: string) => (r?.values?.form === name ? r : null);
	const done = $derived(form && 'done' in form ? (form.done as keyof typeof w.done) : null);
	let kind = $state('named');
	let link = $state<string | null>(null);
	let copied = $state(false);
	$effect(() => {
		link = form && 'link' in form && typeof form.link === 'string' ? form.link : null;
		copied = false;
	});

	// The new shareable link can't be shown again: warn before leaving while it is on screen (spec edge case).
	beforeNavigate(({ cancel, willUnload }) => {
		if (link && !willUnload && !confirm(w.leave)) cancel();
	});
	$effect(() => {
		if (!link) return;
		const warn = (e: BeforeUnloadEvent) => e.preventDefault();
		addEventListener('beforeunload', warn);
		return () => removeEventListener('beforeunload', warn);
	});
</script>

<svelte:head><title>{w.tab} · InTheKnow</title></svelte:head>

<div class="stack">
	{#if r && !r.values?.form}<p class="notice error" role="alert">{r.message}</p>{/if}

	<form method="POST" action="?/saveSettings" use:enhance={s.enhance} class="section">
		<h2>{w.settings}</h2>
		<Switch id="open" name="open" label={w.open} description={w.openHint} checked={data.settings.open} />
		<Field label={w.domains} id="domains" hint={w.domainsHint} error={refusedIn('settings')?.message}>
			{#snippet children({ id, describedby, invalid })}
				<textarea {id} name="allowedDomains" aria-describedby={describedby} aria-invalid={invalid ? 'true' : undefined}
					>{refusedIn('settings')?.values?.allowedDomains ?? data.settings.allowedDomains.join('\n')}</textarea
				>
			{/snippet}
		</Field>
		<div><Button disabled={s.pending}>{t.save}</Button></div>
	</form>

	{#if link}
		<section class="section" aria-labelledby="link-title">
			<h2 id="link-title">{w.linkTitle}</h2>
			<p class="notice warning">{w.linkWarning}</p>
			<p class="link">{link}</p>
			<div class="row">
				<Button type="button" variant="secondary" onclick={() => navigator.clipboard.writeText(link!).then(() => (copied = true))}
					>{copied ? t.recovery.copied : t.recovery.copy}</Button
				>
				<Button type="button" onclick={() => (link = null)}>{w.linkDone}</Button>
			</div>
		</section>
	{/if}

	<form method="POST" action="?/createInvite" use:enhance={s.enhance} class="section">
		<h2>{w.create}</h2>
		{#if refusedIn('invite')}<p class="notice error" role="alert">{refusedIn('invite')?.message}</p>{/if}
		<fieldset class="stack">
			<legend>{w.kind}</legend>
			<label class="choice"><input type="radio" name="kind" value="named" bind:group={kind} />{w.named}</label>
			<label class="choice"><input type="radio" name="kind" value="shareable" bind:group={kind} />{w.shareable}</label>
		</fieldset>
		{#if kind === 'named'}
			<Field label={t.auth.email} id="invite-email" name="email" type="email" required value={refusedIn('invite')?.values?.email ?? ''} />
			<label class="choice"><input type="checkbox" name="grantsAdmin" />{w.grantsAdmin}</label>
		{:else}
			<Field label={w.maxUses} id="max-uses" name="maxUses" type="number" min={1} max={1000} required value={refusedIn('invite')?.values?.maxUses ?? '30'} />
		{/if}
		<Field label={t.auth.language} id="invite-language">
			{#snippet children({ id })}
				<select {id} name="language" value={refusedIn('invite')?.values?.language ?? data.lang}>
					<option value="en" lang="en">English</option>
					<option value="nl" lang="nl">Nederlands</option>
				</select>
			{/snippet}
		</Field>
		<Field label={w.validDays} id="valid-days" name="validDays" type="number" min={1} max={90} required value={refusedIn('invite')?.values?.validDays ?? '14'} />
		<div><Button disabled={s.pending}>{w.createSubmit}</Button></div>
	</form>

	<section class="stack">
		<h2>{w.pending}</h2>
		{#if data.invites.length}
			<ul class="cards">
				{#each data.invites as i (i.id)}
					<li class="section">
						<div class="head">
							<strong>{i.kind === 'named' ? i.email : w.shareableLink}</strong>
							{#if i.grantsAdmin}<Badge tone="magenta">{t.admin.accounts.admin}</Badge>{/if}
						</div>
						<p class="muted">{i.kind === 'shareable' ? w.usesLeft(i.left, i.maxUses) : ''} {w.expires(i.expires)}</p>
						<div class="row">
							{#if i.kind === 'named'}
								<form method="POST" action="?/resend" use:enhance={s.enhance}>
									<input type="hidden" name="inviteId" value={String(i.id)} />
									<Button size="sm" variant="secondary" disabled={s.pending}>{w.resend}</Button>
								</form>
							{/if}
							<ConfirmAction label={w.revoke} title={w.revokeTitle} confirm={w.revoke} action="?/revoke" fields={{ inviteId: String(i.id) }} enhanceWith={s.enhance} pending={s.pending} variant="ghost" size="sm">
								<p>{w.revokeBody}</p>
							</ConfirmAction>
						</div>
					</li>
				{/each}
			</ul>
		{:else}
			<p class="muted">{w.none}</p>
		{/if}
	</section>
</div>
<Toast message={done && done !== 'invite' ? w.done[done] : done === 'invite' && !link ? w.done.invite : null} />

<style>
	h2 {
		font: var(--type-h4);
	}
	fieldset {
		border: 0;
		margin: 0;
		padding: 0;
		gap: 0;
	}
	legend {
		font: var(--type-label);
		color: var(--text-strong);
		margin-bottom: var(--space-2);
	}
	.link {
		font-family: var(--font-mono);
		overflow-wrap: anywhere;
		color: var(--text-strong);
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
