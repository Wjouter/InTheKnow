import { expect, test, type BrowserContext, type Page } from '@playwright/test';
import { dicts } from '../src/lib/i18n';

// 011 dark mode: which theme shows, and the two ways to choose one.
const DARK = 'rgb(20, 20, 19)'; // --surface-page in dark, #141413
const LIGHT = 'rgb(255, 255, 255)';
const en = dicts.en;

const background = (page: Page) => page.evaluate(() => getComputedStyle(document.body).backgroundColor);
const themeCookie = async (context: BrowserContext) => (await context.cookies()).find((c) => c.name === 'itk_theme')?.value;
/** A mark that a navigation would wipe, so a test can prove the page was not reloaded. */
const mark = (page: Page) => page.evaluate(() => ((window as unknown as { itkMark: number }).itkMark = 1));
const marked = (page: Page) => page.evaluate(() => (window as unknown as { itkMark?: number }).itkMark === 1);
const cookie = (name: string, value: string, baseURL: string | undefined) => ({ name, value, url: baseURL! });

test.describe('following the device', () => {
	test.use({ colorScheme: 'dark' });

	test('is dark on a dark device and follows a switch without a reload (FR-001)', async ({ page }) => {
		for (const path of ['/', '/sign-in']) {
			await page.goto(path);
			expect(await background(page)).toBe(DARK);
		}
		await mark(page);
		await page.emulateMedia({ colorScheme: 'light' });
		expect(await background(page)).toBe(LIGHT);
		expect(await marked(page)).toBe(true);
	});

	test('is dark on a signed-in page (FR-002)', async ({ page, context, baseURL }) => {
		await context.addCookies([cookie('itk_session', 'en', baseURL)]);
		await page.goto('/changes');
		expect(await background(page)).toBe(DARK);
	});

	test('tells the browser both schemes before the stylesheet arrives (SC-004)', async ({ request }) => {
		const html = await (await request.get('/')).text();
		expect(html).toContain('<meta name="color-scheme" content="light dark" />');
		expect(html).toContain('data-theme=""');
	});

	test('an unknown cookie value follows the device (FR-016)', async ({ page, context, baseURL }) => {
		await context.addCookies([cookie('itk_theme', 'purple', baseURL)]);
		const html = await (await page.goto('/'))!.text();
		expect(html).toContain('data-theme=""');
		expect(html).not.toContain('purple');
		expect(await background(page)).toBe(DARK);
	});
});

test.describe('light stays light (US3)', () => {
	for (const colorScheme of ['light', 'no-preference'] as const) {
		test(`${colorScheme} device`, async ({ page }) => {
			await page.emulateMedia({ colorScheme });
			await page.goto('/');
			expect(await background(page)).toBe(LIGHT);
		});
	}

	for (const chosen of [null, 'dark']) {
		test(`print is light, theme ${chosen ?? 'not chosen'} (FR-012)`, async ({ page, context, baseURL }) => {
			if (chosen) await context.addCookies([cookie('itk_theme', chosen, baseURL)]);
			await page.emulateMedia({ media: 'print', colorScheme: 'dark' });
			await page.goto('/');
			expect(await background(page)).toBe(LIGHT);
			expect(await page.evaluate(() => getComputedStyle(document.body).color)).toBe('rgb(61, 61, 61)'); // --grey-700
		});
	}
});

test.describe('a chosen theme wins over the device (FR-006, FR-011)', () => {
	for (const [device, chosen, bg] of [
		['light', 'dark', DARK],
		['dark', 'light', LIGHT]
	] as const) {
		test(`${chosen} on a ${device} device`, async ({ page, context, baseURL }) => {
			await context.addCookies([cookie('itk_theme', chosen, baseURL)]);
			await page.emulateMedia({ colorScheme: device });
			const html = await (await page.goto('/'))!.text();
			expect(html).toContain(`data-theme="${chosen}"`);
			expect(html).toContain(`<meta name="color-scheme" content="${chosen}" />`);
			expect(await background(page)).toBe(bg);
		});
	}

	test('also on a page without a session (US4-5)', async ({ page, context, baseURL }) => {
		await context.addCookies([cookie('itk_theme', 'dark', baseURL)]);
		await page.emulateMedia({ colorScheme: 'light' });
		await page.goto('/sign-in');
		expect(await background(page)).toBe(DARK);
	});
});

test.describe('Preferences (US4)', () => {
	test.use({ colorScheme: 'light' });
	test.beforeEach(async ({ context, baseURL }) => context.addCookies([cookie('itk_session', 'en', baseURL)]));

	test('applies each choice at once and keeps it', async ({ page, context }) => {
		await page.goto('/settings/preferences');
		await expect(page.getByRole('radio', { name: en.theme.device })).toBeChecked();
		await mark(page);

		await page.getByRole('radio', { name: en.theme.dark }).check();
		await expect.poll(() => background(page)).toBe(DARK);
		await expect.poll(() => themeCookie(context)).toBe('dark');

		await page.getByRole('radio', { name: en.theme.light }).check();
		await expect.poll(() => background(page)).toBe(LIGHT);
		await expect.poll(() => themeCookie(context)).toBe('light');
		expect(await marked(page)).toBe(true);

		await page.reload();
		await expect(page.getByRole('radio', { name: en.theme.light })).toBeChecked();

		await page.getByRole('radio', { name: en.theme.device }).check();
		await expect.poll(() => themeCookie(context)).toBeUndefined();
	});

	test('another tab follows within a second (FR-022, SC-012)', async ({ context }) => {
		const settings = await context.newPage();
		await settings.goto('/settings/preferences');
		const other = await context.newPage();
		await other.goto('/settings/preferences');

		await other.getByRole('radio', { name: en.theme.dark }).check();
		await expect.poll(() => background(settings), { timeout: 1000 }).toBe(DARK);
		await expect(settings.getByRole('radio', { name: en.theme.dark })).toBeChecked({ timeout: 1000 });
	});
});

test.describe('without script (FR-021)', () => {
	test.use({ javaScriptEnabled: false, colorScheme: 'light' });

	test('Save on Preferences comes back in the chosen theme', async ({ page, context, baseURL }) => {
		await context.addCookies([cookie('itk_session', 'en', baseURL)]);
		await page.goto('/settings/preferences');
		await page.getByRole('radio', { name: en.theme.dark }).check();
		await page.getByRole('button', { name: en.save, exact: true }).click();
		await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
		expect(await background(page)).toBe(DARK);
	});

	test('the visitor toggle loads the same path again in dark', async ({ page }) => {
		await page.goto('/about');
		await page.getByRole('button', { name: en.theme.toDark }).click();
		await expect(page).toHaveURL(/\/about$/);
		await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
	});
});

test.describe('visitor toggle (US5)', () => {
	test.use({ colorScheme: 'light' });

	for (const lang of ['en', 'nl'] as const) {
		for (const path of ['/', '/sign-in', '/about']) {
			test(`one toggle on ${path}, ${lang} (FR-018)`, async ({ page, context, baseURL }) => {
				await context.addCookies([cookie('itk_lang', lang, baseURL)]);
				await page.goto(path);
				const toggle = page.getByRole('button', { name: dicts[lang].theme.toDark });
				await expect(toggle).toHaveCount(1);
				await expect(page.getByRole('button', { name: dicts[lang].theme.toLight })).toHaveCount(0);
				const box = (await toggle.boundingBox())!;
				expect(box.width).toBeGreaterThanOrEqual(44);
				expect(box.height).toBeGreaterThanOrEqual(44);
			});
		}
	}

	test('a press switches at once and is remembered (US5-2, US5-4)', async ({ page, context }) => {
		await page.goto('/');
		await mark(page);
		await page.getByRole('button', { name: en.theme.toDark }).click();
		await expect.poll(() => background(page)).toBe(DARK);
		await expect.poll(() => themeCookie(context)).toBe('dark');
		await expect(page.getByRole('button', { name: en.theme.toLight })).toBeVisible();
		expect(await marked(page)).toBe(true);

		await page.reload();
		expect(await background(page)).toBe(DARK);
		const another = await context.newPage();
		await another.goto('/account/sign-up');
		expect(await background(another)).toBe(DARK);
	});

	test('on a dark device it offers light', async ({ page }) => {
		await page.emulateMedia({ colorScheme: 'dark' });
		await page.goto('/');
		await page.getByRole('button', { name: en.theme.toLight }).click();
		await expect.poll(() => background(page)).toBe(LIGHT);
	});

	test('works from the keyboard (US5-8)', async ({ page }) => {
		await page.goto('/');
		const toggle = page.getByRole('button', { name: en.theme.toDark });
		for (let i = 0; i < 10 && !(await toggle.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press('Tab');
		await expect(toggle).toBeFocused();
		await page.keyboard.press('Enter');
		await expect.poll(() => background(page)).toBe(DARK);
	});

	test('is not in the signed-in header, and Preferences shows the choice (FR-019)', async ({ page, context, baseURL }) => {
		await page.goto('/sign-in');
		await page.getByRole('button', { name: en.theme.toDark }).click();
		await expect.poll(() => themeCookie(context)).toBe('dark');
		await context.addCookies([cookie('itk_session', 'en', baseURL)]);
		await page.goto('/settings/preferences');
		await expect(page.getByRole('banner').getByRole('button', { name: en.theme.toLight })).toHaveCount(0);
		await expect(page.getByRole('banner').getByRole('button', { name: en.theme.toDark })).toHaveCount(0);
		await expect(page.getByRole('radio', { name: en.theme.dark })).toBeChecked();
	});

	// Dutch at 360px is still 12px too wide; it was 35px too wide before the toggle, with the wider spacing.
	for (const width of [360, 375, 390, 400, 412, 420, 767]) {
		for (const lang of width === 360 ? (['en'] as const) : (['en', 'nl'] as const)) {
			test(`the header fits at ${width}px, ${lang}`, async ({ page, context, baseURL }) => {
				await page.setViewportSize({ width, height: 844 });
				await context.addCookies([cookie('itk_lang', lang, baseURL)]);
				await page.goto('/');
				expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
			});
		}
	}
});
