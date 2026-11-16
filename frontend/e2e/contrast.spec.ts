import AxeBuilder from '@axe-core/playwright';
import { expect, test, type Page } from '@playwright/test';
import { dicts } from '../src/lib/i18n';

// SC-001, SC-002: zero colour-contrast failures in dark, on every page of FR-002, at phone and desktop width, in
// English and Dutch. Exported so the light screenshot comparison (T043) uses the same list.
// `shows`: elements that must be on the page, so a fixture change cannot silently drop a state from the check.
export const pages: { path: string; signedIn: boolean; status?: number; shows?: string[] }[] = [
	{ path: '/', signedIn: false },
	{ path: '/sign-in', signedIn: false },
	{ path: '/sign-in/code', signedIn: false },
	{ path: '/account/sign-up', signedIn: false },
	{ path: '/account/confirm?token=placeholder', signedIn: false },
	{ path: '/account/confirmation', signedIn: false },
	{ path: '/account/email?token=placeholder', signedIn: false },
	{ path: '/account/forgot-password', signedIn: false },
	{ path: '/account/reset?token=placeholder', signedIn: false },
	{ path: '/account/invite?token=placeholder', signedIn: false },
	{ path: '/about', signedIn: false },
	{ path: '/', signedIn: true },
	{ path: '/changes', signedIn: true, shows: ['a.change'] },
	{ path: '/changes?showHidden=1', signedIn: true, shows: ['a.change.hidden'] },
	{ path: '/changes/3', signedIn: true, shows: ['.current.present', '.entry.low', '.entry.new'] },
	{ path: '/changes/3?all=1', signedIn: true, shows: ['.entry.hidden', '.entry.low'] },
	{ path: '/changes/4', signedIn: true, shows: ['.current.removed', '.entry.removed'] },
	{ path: '/changes/6', signedIn: true, shows: ['.current.gone'] },
	{ path: '/calendars', signedIn: true },
	{ path: '/calendars/1', signedIn: true },
	{ path: '/calendars/2', signedIn: true },
	{ path: '/calendars/1/filter', signedIn: true },
	{ path: '/calendars/1/notifications', signedIn: true },
	{ path: '/presets', signedIn: true },
	{ path: '/presets/1', signedIn: true },
	{ path: '/settings/account', signedIn: true },
	{ path: '/settings/preferences', signedIn: true },
	{ path: '/settings/security', signedIn: true },
	{ path: '/settings/security/two-factor', signedIn: true },
	{ path: '/notifications', signedIn: true },
	{ path: '/admin/accounts', signedIn: true },
	{ path: '/admin/calendars', signedIn: true },
	{ path: '/admin/email', signedIn: true },
	{ path: '/admin/presets', signedIn: true },
	{ path: '/admin/presets/2', signedIn: true },
	{ path: '/admin/sign-up', signedIn: true },
	// A visitor is sent to sign-in from any unknown address, so only the signed-in not-found page exists.
	{ path: '/no-such-page', signedIn: true, status: 404 }
];

const viewports = [
	{ width: 390, height: 844 },
	{ width: 1280, height: 800 }
];

async function noContrastFailures(page: Page) {
	// A toast or dialog that is still fading in has blended colours, which axe would measure.
	await page.waitForFunction(() => document.getAnimations().every((a) => a.playState !== 'running'));
	const { violations } = await new AxeBuilder({ page }).withRules(['color-contrast']).analyze();
	const found = violations.flatMap((v) => v.nodes.map((n) => `${n.target.join(' ')}: ${n.failureSummary}`));
	expect(found).toEqual([]);
}

for (const lang of ['en', 'nl'] as const) {
	const t = dicts[lang];
	for (const viewport of viewports) {
		test.describe(`dark, ${lang}, ${viewport.width}px`, () => {
			test.use({ colorScheme: 'dark', viewport });

			for (const p of pages) {
				test(`${p.signedIn ? 'signed in' : 'visitor'} ${p.path}`, async ({ page, context, baseURL }) => {
					const url = baseURL!;
					await context.addCookies([
						p.signedIn ? { name: 'itk_session', value: lang, url } : { name: 'itk_lang', value: lang, url },
						// The second sign-in step needs the awaiting-code cookie.
						...(p.path === '/sign-in/code' ? [{ name: 'itk_pending', value: 'placeholder', domain: 'localhost', path: '/sign-in' }] : [])
					]);
					const response = await page.goto(p.path);
					expect(response?.status()).toBe(p.status ?? 200);
					// A missing stub answer shows the unavailable view, which would pass while testing nothing.
					await expect(page.getByRole('heading', { name: t.errors.unavailableTitle })).toHaveCount(0);
					for (const selector of p.shows ?? []) await expect(page.locator(selector).first()).toBeAttached();
					await noContrastFailures(page);

					if (p.path === '/changes') {
						await page.locator('a.change').first().click();
						await expect(page.getByRole('dialog')).toBeVisible();
						await noContrastFailures(page);
					}
					if (p.path === '/settings/account') {
						await page.getByRole('button', { name: t.account.delete }).click();
						await expect(page.getByRole('dialog')).toBeVisible();
						await noContrastFailures(page);
					}
					if (p.path === '/settings/preferences') {
						await page.getByRole('button', { name: t.save, exact: true }).click();
						await expect(page.getByRole('status').getByText(t.saved, { exact: true })).toBeVisible();
						await noContrastFailures(page);
					}
				});
			}
		});
	}
}
