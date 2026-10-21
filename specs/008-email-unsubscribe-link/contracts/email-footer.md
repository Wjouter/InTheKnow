# Contract: Unsubscribe footer

Amends the Changes and Test rows of [007 email-html.md](../../007-branded-email-layout/contracts/email-html.md) and
the Changes/Test sections of [003 messages.md](../../003-notification-channels/contracts/messages.md).

## Which emails

| Kind | Footer | Unsubscribe link |
|---|---|---|
| Changes | `changes.footer` | yes |
| Test | `changes.footer` (new) | yes |
| Confirmation | none | no |
| Account | `account.footer` | no |

## Text part (after the body)

```
{changes.footer}

{page.unsubscribe.title}: {UnsubscribeUrl}

```

## HTML part (below the card, in the existing 12px #5a5a5a footer block)

```
<p style="margin:0 0 12px">{changes.footer, encoded}</p>
<p style="margin:0 0 12px"><a href="{UnsubscribeUrl, encoded}" style="color:#5a5a5a;text-decoration:underline">{page.unsubscribe.title, encoded}</a></p>
```

## Guarantees (checked by tests)

- The footer link's `href` equals the `List-Unsubscribe` header value without `<` `>` (FR-002), in both languages.
- In both parts of Changes and Test, the only `/n/` URL is `UnsubscribeUrl`; no `/n/confirm` or `/n/decline` (SC-003).
- Confirmation and Account emails contain no `/n/unsubscribe/` URL; their text snapshots are unchanged (FR-007, SC-005).
- `GET /n/unsubscribe/{token}` leaves the channel `active`; only `POST` unsubscribes (FR-003; new assertion in `PublicPagesTests`, the code path is unchanged).
- 007's "text part byte-for-byte equal to the snapshots" guarantee no longer holds for Changes and Test; their
  snapshots are regenerated once by this feature.
