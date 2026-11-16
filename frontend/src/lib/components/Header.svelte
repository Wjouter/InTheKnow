<script lang="ts">
	import LogOut from '@lucide/svelte/icons/log-out';
	import Moon from '@lucide/svelte/icons/moon';
	import Sun from '@lucide/svelte/icons/sun';
	import { page } from '$app/state';
	import { dicts } from '$lib/i18n';
	import { setTheme } from '$lib/theme';
	import { navItems } from './nav';

	const t = $derived(dicts[page.data.lang]);
	const me = $derived(page.data.me);
	const items = $derived(me ? navItems(t, me.role === 'admin') : []);
	const here = $derived(page.url.pathname + page.url.search);
	const other = $derived(page.data.lang === 'en' ? { lang: 'nl', label: 'Nederlands' } : { lang: 'en', label: 'English' });
</script>

<header class:visitor={!me}>
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
				<!-- Without script this posts and comes back in the new theme; with script it changes at once (FR-021). -->
				<form
					method="POST"
					action="/theme"
					onsubmit={(e) => {
						e.preventDefault();
						setTheme((e.submitter as HTMLButtonElement).value as 'light' | 'dark');
					}}
				>
					<input type="hidden" name="next" value={here} />
					<button type="submit" name="theme" value="dark" class="toggle theme-light-only" aria-label={t.theme.toDark}><Moon size={20} aria-hidden="true" /></button>
					<button type="submit" name="theme" value="light" class="toggle theme-dark-only" aria-label={t.theme.toLight}><Sun size={20} aria-hidden="true" /></button>
				</form>
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
	/* No display here: base.css shows the one that applies. */
	.toggle {
		width: var(--hit-min);
		height: var(--hit-min);
		padding: 0;
		border: 0;
		background: transparent;
		color: var(--text-strong);
		cursor: pointer;
		border-radius: var(--radius-md);
	}
	.toggle:hover {
		background: var(--brand-soft);
		color: var(--text-brand);
	}
	/* The visitor header holds five controls since the theme toggle (011 T038): tighter at phone width so they fit in
	   390px. The signed-in header is not touched. */
	@media (max-width: 767px) {
		.visitor .inner {
			gap: var(--space-2);
		}
		.visitor .plain {
			padding: 0 6px;
		}
	}
	@media (max-width: 419px) {
		.visitor .inner {
			padding: 0 var(--space-3);
		}
		.visitor .wordmark {
			font-size: 21px;
		}
		.visitor .end {
			gap: 2px;
		}
		.visitor .plain {
			padding: 0 var(--space-1);
		}
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
