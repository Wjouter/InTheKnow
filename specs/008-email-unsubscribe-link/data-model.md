# Data model: Unsubscribe link in the email footer

No schema change and no new stored data.

| Entity / value | Use in this feature | Change |
|---|---|---|
| `channel.unsubscribe_token` | already one per channel; builds the unsubscribe URL | none |
| `channel.status` | `active` → `unsubscribed` only on POST to the page (003) | none |
| `Message.UnsubscribeUrl` | now rendered in the footer of Changes and Test, besides the header | used in one more place |
| `Texts` `changes.footer` (en, nl) | footer sentence, now mentions the link | text rewritten |
| `Texts` `page.unsubscribe.title` (en, nl) | reused as the footer link label | none |

Personal data: none added (Principle II).
