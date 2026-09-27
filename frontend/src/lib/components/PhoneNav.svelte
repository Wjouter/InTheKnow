<script lang="ts">
	import Bell from '@lucide/svelte/icons/bell';
	import Calendar from '@lucide/svelte/icons/calendar';
	import Settings from '@lucide/svelte/icons/settings';
	import Shield from '@lucide/svelte/icons/shield';
	import { page } from '$app/state';
	import { dicts } from '$lib/i18n';
	import { navItems } from './nav';

	const icons = { changes: Bell, calendars: Calendar, settings: Settings, admin: Shield };
	const t = $derived(dicts[page.data.lang]);
	const items = $derived(navItems(t, page.data.me?.role === 'admin'));
</script>

<nav aria-label={t.nav.label}>
	<ul>
		{#each items as item (item.href)}
			{@const Icon = icons[item.icon]}
			<li>
				<a href={item.href} aria-current={item.active(page.url.pathname) ? 'page' : undefined}>
					<Icon size={24} aria-hidden="true" />{item.label}
				</a>
			</li>
		{/each}
	</ul>
</nav>

<style>
	nav {
		position: fixed;
		left: 0;
		right: 0;
		bottom: 0;
		z-index: 30;
		height: var(--tabbar-h);
		background: var(--surface-page);
		border-top: 1px solid var(--border-subtle);
	}
	ul {
		list-style: none;
		margin: 0;
		padding: 0;
		display: flex;
		height: 100%;
	}
	li {
		flex: 1;
		min-width: 0;
	}
	a {
		height: 100%;
		display: flex;
		flex-direction: column;
		align-items: center;
		justify-content: center;
		gap: var(--space-1);
		color: var(--text-muted);
		font: 600 var(--fs-12) / 1 var(--font-body);
		text-decoration: none;
		overflow-wrap: anywhere;
		text-align: center;
	}
	a[aria-current='page'] {
		color: var(--brand);
		font-weight: var(--fw-bold);
	}
	@media (min-width: 768px) {
		nav {
			display: none;
		}
	}
</style>
