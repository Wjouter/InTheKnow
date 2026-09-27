const unsafe = (s: string) => !s.startsWith('/') || s.startsWith('//') || s.startsWith('/\\');

/** The return address after sign-in: only a path on this app, else `/` (FR-011, research R6). */
export function safeNext(next: string | null | undefined, origin: string): string {
	if (!next) return '/';
	try {
		// Also judge the decoded form, so an encoded `//` or `/\` can't slip through a later decode.
		if (unsafe(next) || unsafe(decodeURIComponent(next))) return '/';
		return new URL(next, origin).origin === origin ? next : '/';
	} catch {
		return '/';
	}
}
