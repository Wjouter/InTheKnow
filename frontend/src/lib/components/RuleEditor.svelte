<script lang="ts">
	import ArrowDown from '@lucide/svelte/icons/arrow-down';
	import ArrowUp from '@lucide/svelte/icons/arrow-up';
	import Trash from '@lucide/svelte/icons/trash-2';
	import X from '@lucide/svelte/icons/x';
	import { untrack } from 'svelte';
	import { page } from '$app/state';
	import Button from './Button.svelte';
	import { dicts } from '$lib/i18n';

	// One editor for calendar filters, personal presets and global presets (research R14). The rules live in $state and
	// go to the server as JSON in a hidden `rules` field of the enclosing form; the API validates them.
	type Rule = { outcome: string; when: Record<string, unknown>; error?: string | null; inactiveReason?: string | null };
	let {
		rules: initial,
		activeConditions = null,
		values = {},
		formError
	}: {
		rules: Rule[];
		/** The condition keys usable on this calendar; null for presets, which have no calendar. */
		activeConditions?: string[] | null;
		values?: Record<string, string[]>;
		formError?: string;
	} = $props();

	const t = $derived(dicts[page.data.lang]);
	const e = $derived(t.rules.editor);
	const uid = $props.id();

	type Draft = { outcome: string; when: Record<string, unknown> };
	let rules = $state<Draft[]>(untrack(() => initial.map((r) => ({ outcome: r.outcome, when: structuredClone(r.when ?? {}) }))));
	// The server's notes per rule stay with their rule while rules move.
	let notes = $state(untrack(() => initial.map((r) => ({ error: r.error ?? null, inactive: r.inactiveReason ?? null }))));
	let drafts = $state<Record<string, string>>({});
	let adding = $state<Record<number, string>>({});

	const outcomes = ['showOnly', 'hide', 'lowPriority'] as const;
	const statuses = ['confirmed', 'tentative', 'cancelled', 'none'] as const;
	const listKeys = ['activityType', 'group', 'room', 'teacher'] as const;
	type ListKey = (typeof listKeys)[number];
	const conditionKeys = ['changeKind', 'eventEnded', 'status', 'nameContains', ...listKeys] as const;
	type ConditionKey = (typeof conditionKeys)[number];

	const usable = (key: string) => activeConditions === null || activeConditions.includes(key);
	const kinds = $derived([
		'added',
		'removed',
		'name',
		'time',
		'status',
		'location',
		'description',
		...(usable('activityType') ? ['activityType', 'groups'] : []),
		...(usable('teacher') ? ['teachers'] : [])
	]);

	const has = (r: Draft, key: ConditionKey) => (key === 'status' ? 'statusFrom' in r.when || 'statusTo' in r.when : key in r.when);
	const missing = (r: Draft) => conditionKeys.filter((k) => !has(r, k));
	const list = (r: Draft, key: string): string[] => (Array.isArray(r.when[key]) ? (r.when[key] as string[]) : []);

	function addCondition(i: number) {
		const r = rules[i];
		const key = (adding[i] as ConditionKey | undefined) ?? missing(r)[0];
		if (!key) return;
		if (key === 'eventEnded') r.when.eventEnded = true;
		else if (key === 'status') r.when.statusTo = 'cancelled';
		else if (key === 'nameContains') r.when.nameContains = '';
		else r.when[key] = [];
		adding[i] = missing(r)[0] ?? '';
	}
	function removeCondition(r: Draft, key: ConditionKey) {
		if (key === 'status') {
			delete r.when.statusFrom;
			delete r.when.statusTo;
		} else delete r.when[key];
	}
	function toggleKind(r: Draft, kind: string, on: boolean) {
		const current = list(r, 'changeKind');
		r.when.changeKind = on ? [...current, kind] : current.filter((k) => k !== kind);
	}
	function setStatus(r: Draft, key: 'statusFrom' | 'statusTo', value: string) {
		if (value) r.when[key] = value;
		else delete r.when[key];
	}
	function addValue(i: number, key: ListKey) {
		const id = `${i}-${key}`;
		const value = (drafts[id] ?? '').trim();
		if (!value) return;
		const current = list(rules[i], key);
		if (!current.includes(value)) rules[i].when[key] = [...current, value];
		drafts[id] = '';
	}
	function move(i: number, by: -1 | 1) {
		const j = i + by;
		[rules[i], rules[j]] = [rules[j], rules[i]];
		[notes[i], notes[j]] = [notes[j], notes[i]];
	}
	function remove(i: number) {
		rules.splice(i, 1);
		notes.splice(i, 1);
	}
	function addRule() {
		rules.push({ outcome: 'hide', when: {} });
		notes.push({ error: null, inactive: null });
	}
	const json = $derived(JSON.stringify(rules));
</script>

<fieldset class="editor">
	<legend>{e.label}</legend>
	<input type="hidden" name="rules" value={json} />
	{#if formError}<p class="notice error" role="alert">{formError}</p>{/if}
	{#if !rules.length}<p class="muted">{e.noRules}</p>{/if}

	{#each rules as rule, i (rule)}
		{@const id = `${uid}-${i}`}
		<section class="rule" aria-labelledby="{id}-title" class:invalid={notes[i]?.error}>
			<div class="rule-head">
				<h3 id="{id}-title">{e.rule(i + 1)}</h3>
				<div class="tools">
					<button type="button" class="icon" disabled={i === 0} onclick={() => move(i, -1)} aria-label="{e.moveUp}: {e.rule(i + 1)}"><ArrowUp size={18} aria-hidden="true" /></button>
					<button type="button" class="icon" disabled={i === rules.length - 1} onclick={() => move(i, 1)} aria-label="{e.moveDown}: {e.rule(i + 1)}"><ArrowDown size={18} aria-hidden="true" /></button>
					<button type="button" class="icon" onclick={() => remove(i)} aria-label="{e.removeRule}: {e.rule(i + 1)}"><Trash size={18} aria-hidden="true" /></button>
				</div>
			</div>
			{#if notes[i]?.inactive}<p class="notice warning">{notes[i].inactive}</p>{/if}
			{#if notes[i]?.error}<p class="form-error" role="alert">{notes[i].error}</p>{/if}

			<label class="stacked">
				<span>{e.outcome}</span>
				<select class="select" bind:value={rule.outcome}>
					{#each outcomes as o (o)}<option value={o}>{t.rules.outcomes[o]}</option>{/each}
				</select>
			</label>

			<div class="conditions">
				<span class="label">{e.conditions}</span>
				{#if has(rule, 'changeKind')}
					<fieldset class="condition">
						<legend>{e.changeKind}</legend>
						<div class="kinds">
							{#each kinds as kind (kind)}
								<label class="choice">
									<input type="checkbox" checked={list(rule, 'changeKind').includes(kind)} onchange={(ev) => toggleKind(rule, kind, ev.currentTarget.checked)} />
									{t.rules.kinds[kind as keyof typeof t.rules.kinds]}
								</label>
							{/each}
						</div>
						<button type="button" class="link" onclick={() => removeCondition(rule, 'changeKind')}>{e.removeCondition}</button>
					</fieldset>
				{/if}
				{#if has(rule, 'eventEnded')}
					<div class="condition row">
						<span>{e.eventEnded}</span>
						<button type="button" class="link" onclick={() => removeCondition(rule, 'eventEnded')}>{e.removeCondition}</button>
					</div>
				{/if}
				{#if has(rule, 'status')}
					<fieldset class="condition">
						<legend>{e.status}</legend>
						<div class="row">
							{#each ['statusFrom', 'statusTo'] as const as key (key)}
								<label class="stacked">
									<span>{key === 'statusFrom' ? e.statusFrom : e.statusTo}</span>
									<select class="select" value={String(rule.when[key] ?? '')} onchange={(ev) => setStatus(rule, key, ev.currentTarget.value)}>
										<option value="">{e.any}</option>
										{#each statuses as s (s)}<option value={s}>{t.rules.statuses[s]}</option>{/each}
									</select>
								</label>
							{/each}
						</div>
						<button type="button" class="link" onclick={() => removeCondition(rule, 'status')}>{e.removeCondition}</button>
					</fieldset>
				{/if}
				{#if has(rule, 'nameContains')}
					<div class="condition">
						<label class="stacked">
							<span>{e.nameContains}</span>
							<input type="text" maxlength="100" value={String(rule.when.nameContains ?? '')} oninput={(ev) => (rule.when.nameContains = ev.currentTarget.value)} />
						</label>
						<button type="button" class="link" onclick={() => removeCondition(rule, 'nameContains')}>{e.removeCondition}</button>
					</div>
				{/if}
				{#each listKeys as key (key)}
					{#if has(rule, key)}
						{@const field = `${id}-${key}`}
						<fieldset class="condition">
							<legend>{e[key]}</legend>
							{#if list(rule, key).length}
								<ul class="chips">
									{#each list(rule, key) as value (value)}
										<li>
											<span>{key === 'activityType' ? (t.rules.types[value as keyof typeof t.rules.types] ?? value) : value}</span>
											<button
												type="button"
												onclick={() => (rule.when[key] = list(rule, key).filter((v) => v !== value))}
												aria-label={e.removeValue(value)}><X size={16} aria-hidden="true" /></button
											>
										</li>
									{/each}
								</ul>
							{/if}
							<div class="row add-value">
								<label class="visually-hidden" for={field}>{e[key]}</label>
								<input
									id={field}
									type="text"
									maxlength="100"
									list="{field}-values"
									bind:value={drafts[`${i}-${key}`]}
									aria-describedby="{field}-hint"
									onkeydown={(ev) => {
										if (ev.key === 'Enter') {
											ev.preventDefault();
											addValue(i, key);
										}
									}}
								/>
								<datalist id="{field}-values">
									{#each values[key] ?? [] as v (v)}<option value={v}></option>{/each}
								</datalist>
								<Button type="button" size="sm" variant="secondary" onclick={() => addValue(i, key)}>{e.addValue}</Button>
							</div>
							<span class="hint" id="{field}-hint">{e.valueHint}</span>
							<button type="button" class="link" onclick={() => removeCondition(rule, key)}>{e.removeCondition}</button>
						</fieldset>
					{/if}
				{/each}
				{#if missing(rule).length}
					<div class="row">
						<label class="visually-hidden" for="{id}-add">{e.conditionToAdd}</label>
						<select id="{id}-add" class="select narrow" bind:value={adding[i]}>
							{#each missing(rule) as key (key)}<option value={key}>{e[key]}</option>{/each}
						</select>
						<Button type="button" size="sm" variant="secondary" onclick={() => addCondition(i)}>{e.addCondition}</Button>
					</div>
				{/if}
			</div>
		</section>
	{/each}
	<div><Button type="button" variant="secondary" onclick={addRule}>{e.addRule}</Button></div>
</fieldset>

<style>
	.editor {
		border: 0;
		margin: 0;
		padding: 0;
		display: flex;
		flex-direction: column;
		gap: var(--space-4);
		min-width: 0;
	}
	legend {
		font: var(--type-label);
		color: var(--text-strong);
		padding: 0;
	}
	.editor > legend {
		font: var(--type-h4);
		margin-bottom: var(--space-2);
	}
	.rule {
		box-shadow: inset 0 0 0 1px var(--border-subtle);
		padding: var(--space-4);
		display: flex;
		flex-direction: column;
		gap: var(--space-3);
	}
	.rule.invalid {
		box-shadow: inset 0 0 0 2px var(--status-cancelled);
	}
	.rule-head {
		display: flex;
		justify-content: space-between;
		align-items: center;
		gap: var(--space-2);
	}
	h3 {
		font: var(--type-h4);
	}
	.tools {
		display: flex;
		gap: var(--space-1);
	}
	.icon {
		width: var(--hit-min);
		height: var(--hit-min);
		display: inline-flex;
		align-items: center;
		justify-content: center;
		border: 1px solid var(--border-default);
		border-radius: var(--radius-md);
		background: var(--white);
		color: var(--text-strong);
		cursor: pointer;
	}
	.icon:hover:not(:disabled) {
		background: var(--brand-soft);
	}
	.icon:disabled {
		opacity: 0.4;
		cursor: not-allowed;
	}
	.stacked {
		display: flex;
		flex-direction: column;
		gap: 6px;
		font: var(--type-label);
		color: var(--text-strong);
		min-width: 0;
		flex: 1;
	}
	.conditions {
		display: flex;
		flex-direction: column;
		gap: var(--space-3);
	}
	.label {
		font: var(--type-label);
		color: var(--text-strong);
	}
	.condition {
		border: 0;
		margin: 0;
		padding: var(--space-3);
		background: var(--surface-subtle);
		display: flex;
		flex-direction: column;
		gap: var(--space-2);
		min-width: 0;
	}
	.condition.row {
		flex-direction: row;
		flex-wrap: wrap;
		justify-content: space-between;
		align-items: center;
	}
	.condition legend {
		float: left;
		width: 100%;
		margin-bottom: var(--space-1);
	}
	.kinds {
		display: grid;
		grid-template-columns: repeat(auto-fill, minmax(180px, 1fr));
		gap: 0 var(--space-4);
	}
	input[type='text'] {
		width: 100%;
		min-height: var(--hit-min);
		padding: 0 var(--space-3);
		font: var(--type-body);
		color: var(--text-strong);
		border: var(--border-w) solid var(--grey-500);
		border-radius: var(--radius-md);
		background: var(--white);
	}
	.add-value input {
		flex: 1;
		min-width: 0;
		width: auto;
	}
	.add-value {
		flex-wrap: nowrap;
	}
	.narrow {
		width: auto;
		flex: 1;
		min-width: 0;
	}
	.chips {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		flex-wrap: wrap;
		gap: var(--space-2);
	}
	.chips li {
		display: inline-flex;
		align-items: center;
		gap: var(--space-1);
		padding-left: var(--space-3);
		border-radius: var(--radius-pill);
		background: var(--brand-soft);
		color: var(--text-brand);
		font: var(--type-label);
		overflow-wrap: anywhere;
	}
	.chips button {
		width: var(--hit-min);
		height: var(--hit-min);
		display: inline-flex;
		align-items: center;
		justify-content: center;
		border: 0;
		background: transparent;
		color: inherit;
		cursor: pointer;
		border-radius: var(--radius-pill);
	}
	.hint {
		font: var(--type-small);
		color: var(--grey-600);
	}
	.link {
		align-self: flex-start;
		min-height: var(--hit-min);
		padding: 0;
		border: 0;
		background: transparent;
		color: var(--text-link);
		text-decoration: underline;
		font: var(--type-small);
		cursor: pointer;
	}
</style>
