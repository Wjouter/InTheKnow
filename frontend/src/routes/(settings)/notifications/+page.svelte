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
	const w = $derived(t.channels);
	const s = submitter();
	const r = $derived(refusal(form));
	const done = $derived(form && 'done' in form ? (form.done as keyof typeof w.done) : null);
	const tones = { awaitingConfirmation: 'blue', active: 'green', paused: 'neutral', unsubscribed: 'neutral', failing: 'red' } as const;
</script>

<svelte:head><title>{t.settings.notifications} · InTheKnow</title></svelte:head>

<div class="stack">
	<h2>{w.title}</h2>
	<p>{w.intro}</p>
	{#if r && r.values?.form !== 'add'}<p class="notice error" role="alert">{r.message}</p>{/if}
	{#if done === 'added'}<p class="notice success" role="status">{w.done.added}</p>{/if}

	<ul class="channels">
		{#each data.channels as c (c.id)}
			<li class="section">
				<div class="head">
					<span class="target">{c.target}</span>
					<Badge tone={tones[c.status]}>{w.status[c.status]}</Badge>
					{#if c.accountAddress}<Badge tone="magenta">{w.accountAddress}</Badge>{/if}
				</div>
				{#if c.failure}<p>{c.failure}</p>{/if}
				{#if c.lastMessage}<p class="muted">{w.lastMessage(c.lastMessage)}</p>{/if}
				<div class="row">
					{#each c.actions.filter((a) => a !== 'remove') as action (action)}
						<form method="POST" action="?/{action}" use:enhance={s.enhance}>
							<input type="hidden" name="channelId" value={String(c.id)} />
							<Button size="sm" variant="secondary" disabled={s.pending}>{w.actions[action]}</Button>
						</form>
					{/each}
					{#if !c.accountAddress}
					<ConfirmAction
						label={w.actions.remove}
						title={w.removeTitle}
						confirm={w.removeConfirm}
						action="?/remove"
						fields={{ channelId: String(c.id) }}
						enhanceWith={s.enhance}
						pending={s.pending}
						variant="ghost"
						size="sm"
					>
						<p>{w.removeBody(c.target)}</p>
					</ConfirmAction>
					{/if}
				</div>
			</li>
		{/each}
	</ul>

	{#if data.emailAvailable}
		<form method="POST" action="?/add" use:enhance={s.enhance} class="section">
			<h3>{w.add}</h3>
			<Field
				label={w.address}
				id="target"
				name="target"
				type="email"
				autocomplete="email"
				required
				maxlength={254}
				error={r?.values?.form === 'add' ? r.message : undefined}
				value={r?.values?.form === 'add' ? (r.values.target ?? '') : ''}
			/>
			<div><Button disabled={s.pending}>{w.addSubmit}</Button></div>
		</form>
	{:else}
		<p class="notice">{w.unavailable}</p>
	{/if}

	<p class="muted">{w.languageNote} <a href="/settings/preferences">{t.settings.preferences}</a></p>
	<p class="muted">{w.perCalendarNote} <a href="/calendars">{t.nav.calendars}</a></p>
</div>
<Toast message={done && done !== 'added' ? w.done[done] : null} />

<style>
	h2 {
		font: var(--type-h3);
	}
	h3 {
		font: var(--type-h4);
	}
	.channels {
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
	.target {
		font-weight: var(--fw-bold);
		color: var(--text-strong);
		overflow-wrap: anywhere;
		margin-right: var(--space-2);
	}
</style>
