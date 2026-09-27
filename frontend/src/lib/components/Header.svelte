<script lang="ts">
	import LogOut from '@lucide/svelte/icons/log-out';
	import { page } from '$app/state';
	import { dicts } from '$lib/i18n';
	import { navItems } from './nav';

	const t = $derived(dicts[page.data.lang]);
	const me = $derived(page.data.me);
	const items = $derived(me ? navItems(t, me.role === 'admin') : []);
	const here = $derived(page.url.pathname + page.url.search);
	const other = $derived(page.data.lang === 'en' ? { lang: 'nl', label: 'Nederlands' } : { lang: 'en', label: 'English' });
</script>

<header>
	<div class="inner">
		<a class="wordmark" href="/" aria-label={t.wordmarkLabel}>InThe<span>Know</span></a>
		{#if me}
			<nav aria-label={t.nav.label}>
				<ul>
					{#each items as item (item.href)}
						<li><a href={item.href} aria-current={item.active(page.url.pathname) ? 'page' : undefined}>{item.label}</a></li>
					{/each}
				</ul>
			</nav>
			<form method="POST" action="/sign-out" class="end">
				<button type="submit" class="plain"><LogOut size={18} aria-hidden="true" />{t.nav.signOut}</button>
			</form>
		{:else}
			<div class="end">
				<form method="POST" action="/language">
					<input type="hidden" name="lang" value={other.lang} />
					<input type="hidden" name="next" value={here} />
					<button type="submit" class="plain" lang={other.lang}>{other.label}</button>
				</form>
				<a class="plain" href="/sign-in">{t.nav.signIn}</a>
				<a class="plain signup" href="/account/sign-up">{t.nav.signUp}</a>
			</div>
		{/if}
	</div>
</header>

<style>
	header {
		background: var(--white);
		border-bottom: 1px solid var(--border-subtle);
		position: sticky;
		top: 0;
		z-index: 30;
	}
	.inner {
		max-width: var(--container-max);
		margin: 0 auto;
		padding: 0 var(--space-4);
		min-height: 60px;
		display: flex;
		align-items: center;
		gap: var(--space-6);
	}
	.wordmark {
		font: var(--fw-black) 24px/1 var(--font-display);
		color: var(--text-strong);
		text-decoration: none;
		letter-spacing: -0.01em;
		white-space: nowrap;
	}
	.wordmark span {
		color: var(--brand);
	}
	nav {
		display: none;
		flex: 1;
		align-self: stretch;
	}
	ul {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		gap: var(--space-1);
		height: 100%;
	}
	nav a {
		position: relative;
		display: inline-flex;
		align-items: center;
		height: 100%;
		padding: 0 14px;
		font: 700 var(--fs-16) / 1 var(--font-body);
		color: var(--text-strong);
		text-decoration: none;
	}
	nav a[aria-current='page'] {
		color: var(--text-brand);
	}
	nav a[aria-current='page']::after {
		content: '';
		position: absolute;
		left: 14px;
		right: 14px;
		bottom: 0;
		height: 4px;
		background: var(--brand);
	}
	.end {
		margin-left: auto;
		display: flex;
		align-items: center;
		gap: var(--space-1);
	}
	.plain {
		display: inline-flex;
		align-items: center;
		gap: 6px;
		min-height: var(--hit-min);
		padding: 0 var(--space-2);
		border: 0;
		background: transparent;
		font: 700 var(--fs-14) / 1 var(--font-body);
		color: var(--text-strong);
		text-decoration: none;
		cursor: pointer;
		border-radius: var(--radius-md);
	}
	.plain:hover {
		background: var(--brand-soft);
		color: var(--text-brand);
	}
	.signup {
		color: var(--text-brand);
	}
	@media (min-width: 768px) {
		.inner {
			min-height: var(--header-h);
			padding: 0 var(--gutter);
			gap: var(--space-10);
		}
		.wordmark {
			font-size: 26px;
		}
		nav {
			display: block;
		}
		nav + .end {
			margin-left: 0;
		}
	}
</style>
