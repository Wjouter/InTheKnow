import type { Lang } from '$lib/i18n';

const locales: Record<Lang, string> = { en: 'en-GB', nl: 'nl-NL' };

/** Dates and times in the user's zone, 24-hour, the year only when it isn't the current year there (research R12). */
export function formatter(lang: Lang, timeZone: string, now: Date = new Date()) {
	const locale = locales[lang];
	const parts = (d: Date, opts: Intl.DateTimeFormatOptions) =>
		Object.fromEntries(
			new Intl.DateTimeFormat(locale, { timeZone, hourCycle: 'h23', ...opts })
				.formatToParts(d)
				.map((p) => [p.type, p.value.replace(/\.$/, '')])
		);
	const yearOf = (d: Date) => parts(d, { year: 'numeric' }).year;
	const thisYear = yearOf(now);

	const date = (iso: string) => {
		const d = new Date(iso);
		const p = parts(d, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' });
		return `${p.weekday} ${p.day} ${p.month}${p.year === thisYear ? '' : ` ${p.year}`}`;
	};
	const time = (iso: string) => {
		const p = parts(new Date(iso), { hour: '2-digit', minute: '2-digit' });
		return `${p.hour}:${p.minute}`;
	};
	const unit = (n: number, u: 'second' | 'minute' | 'hour') =>
		new Intl.NumberFormat(locale, { style: 'unit', unit: u, unitDisplay: 'long' }).format(n);

	return {
		date,
		time,
		dateTime: (iso: string) => `${date(iso)} ${time(iso)}`,
		range: (startIso: string, endIso: string) =>
			date(startIso) === date(endIso)
				? `${date(startIso)} ${time(startIso)}–${time(endIso)}`
				: `${date(startIso)} ${time(startIso)} – ${date(endIso)} ${time(endIso)}`,
		wait: (seconds: number) =>
			seconds < 60
				? unit(Math.max(1, Math.ceil(seconds)), 'second')
				: seconds < 3600
					? unit(Math.ceil(seconds / 60), 'minute')
					: unit(Math.ceil(seconds / 3600), 'hour')
	};
}
export type Formatter = ReturnType<typeof formatter>;
