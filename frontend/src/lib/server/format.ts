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
	const unit = (n: number, u: 'second' | 'minute' | 'hour' | 'day' | 'year') =>
		new Intl.NumberFormat(locale, { style: 'unit', unit: u, unitDisplay: 'long' }).format(n);
	const dateTime = (iso: string) => `${date(iso)} ${time(iso)}`;
	/** Date with the year always, and the time: the exact value behind a relative one. */
	const fullDateTime = (iso: string) => {
		const p = parts(new Date(iso), { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' });
		return `${p.weekday} ${p.day} ${p.month} ${p.year} ${time(iso)}`;
	};
	// 'always' never words "yesterday" or "today" (010 FR-002, research R1).
	const rtf = new Intl.RelativeTimeFormat(locale, { numeric: 'always' });

	return {
		date,
		time,
		dateTime,
		fullDateTime,
		/** The parts of a calendar date tile (010 contracts/pages.md § Current details). */
		tile: (iso: string) => {
			const p = parts(new Date(iso), { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' });
			return { month: p.month, day: p.day, weekday: p.weekday, full: `${p.weekday} ${p.day} ${p.month} ${p.year}` };
		},
		/** When something was found: relative under 24 hours, else the date and time (010 research R1). */
		found: (iso: string) => {
			// Clamped: the API's clock may be a little ahead of ours.
			const s = Math.max(0, Math.floor((now.getTime() - new Date(iso).getTime()) / 1000));
			const relative = s < 86400;
			const text =
				s < 60
					? rtf.format(-s, 'second')
					: s < 3600
						? rtf.format(-Math.floor(s / 60), 'minute')
						: s < 86400
							? rtf.format(-Math.floor(s / 3600), 'hour')
							: dateTime(iso);
			return { text, exact: fullDateTime(iso), iso, relative };
		},
		/** The retention period: whole years, else days (010 research R11). */
		retention: (days: number) => (days > 0 && days % 365 === 0 ? unit(days / 365, 'year') : unit(days, 'day')),
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
