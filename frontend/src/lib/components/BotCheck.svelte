<script module lang="ts">
	type Widget = {
		render(el: HTMLElement, opts: { sitekey: string }): string | number;
		reset(id?: string | number): void;
		remove?(id: string | number): void;
	};
	declare global {
		interface Window {
			turnstile?: Widget;
			grecaptcha?: Widget;
			itkRecaptchaLoaded?: () => void;
		}
	}

	// One script per page session, shared by every form that shows the widget (research R15).
	const loading = new Map<string, Promise<Widget>>();

	function load(provider: 'turnstile' | 'recaptcha'): Promise<Widget> {
		let p = loading.get(provider);
		if (p) return p;
		p = new Promise<Widget>((resolve, reject) => {
			const script = document.createElement('script');
			script.async = true;
			script.onerror = () => {
				loading.delete(provider);
				reject(new Error('bot check script failed'));
			};
			if (provider === 'turnstile') {
				script.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
				script.onload = () => (window.turnstile ? resolve(window.turnstile) : reject(new Error('no turnstile')));
			} else {
				window.itkRecaptchaLoaded = () => (window.grecaptcha ? resolve(window.grecaptcha) : reject(new Error('no recaptcha')));
				script.src = 'https://www.google.com/recaptcha/api.js?onload=itkRecaptchaLoaded&render=explicit';
			}
			document.head.append(script);
		});
		loading.set(provider, p);
		return p;
	}
</script>

<script lang="ts">
	import { onMount } from 'svelte';
	import { page } from '$app/state';
	import { dicts } from '$lib/i18n';

	let { provider, siteKey }: { provider: 'turnstile' | 'recaptcha'; siteKey: string } = $props();
	const t = $derived(dicts[page.data.lang]);

	let container: HTMLDivElement | undefined = $state();
	let failed = $state(false);
	let widget: Widget | undefined;
	let id: string | number | undefined;

	/** The result is single-use, so each form resets the widget after every action result. */
	export function reset() {
		if (widget && id !== undefined) widget.reset(id);
	}

	onMount(() => {
		let gone = false;
		const timer = setTimeout(() => {
			if (id === undefined) failed = true;
		}, 10_000);
		load(provider)
			.then((w) => {
				if (gone || !container) return;
				widget = w;
				id = w.render(container, { sitekey: siteKey });
			})
			.catch(() => (failed = true));
		return () => {
			gone = true;
			clearTimeout(timer);
			if (widget && id !== undefined) {
				if (widget.remove) widget.remove(id);
				else widget.reset(id);
			}
		};
	});
</script>

<div bind:this={container} class="widget"></div>
{#if failed}
	<p class="notice warning" role="alert">{provider === 'turnstile' ? t.auth.botCheckTurnstile : t.auth.botCheckRecaptcha}</p>
{/if}

<style>
	.widget {
		min-height: 65px;
	}
</style>
