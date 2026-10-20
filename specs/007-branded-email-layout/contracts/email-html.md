# Contract: Email HTML part

Amends the "Body" row of [003 messages.md](../../003-notification-channels/contracts/messages.md): the HTML part
uses inline styles plus **one optional `<style>` block** (phone-only sizes, light mode, iOS data detectors) and
one MSO-only `<style>` (font fallback). Still no images, scripts, remote resources or web fonts, and every
inserted value is HTML-encoded. The plain-text part, subject, headers and recipients do not change (FR-003).

## Document

```
<!DOCTYPE html>
<html lang="{en|nl}">
<head>
  meta charset utf-8, viewport, x-apple-disable-message-reformatting,
  format-detection (telephone=no,date=no,address=no,email=no),
  color-scheme light, supported-color-schemes light
  <style> phone block + :root color-scheme + a[x-apple-data-detectors] </style>
  <!--[if mso]><style> Arial fallback </style><![endif]-->
</head>
<body bgcolor="#f0f0f0" style="margin:0;padding:0;background-color:#f0f0f0">
  outer table 100% (bgcolor #f0f0f0)
    wordmark row: "InThe" #1d1d1b + "Know" #e20e80, Lato 900, centred above the card
    <!--[if mso]> ghost table width=600 <![endif]-->
    card table: width 100%, max-width 600px, #ffffff, border 1px #e3e3e3, radius 2px
      bar row: 4px #e20e80
      content cell: padding 32px (20px on phones), body 16px/1.5 #3d3d3d
        <h1> subject, 28px (24px on phones), #1d1d1b
        … body by kind (below)
    footer (if the kind has one): 12px #5a5a5a, below the card
</body></html>
```

## Body by kind

| Kind | Body |
|---|---|
| Changes | per calendar: `<h2>` name, settings text link; change rows separated by 1px #e3e3e3; if `NotListed > 0`, "and N more changes" in a #f7f7f7 box. Footer: `changes.footer`. |
| Confirmation | intro paragraph; **button** `confirmation.confirm` → `ConfirmUrl`; text link `confirmation.decline` → `DeclineUrl`; `confirmation.expiry` in muted text. No footer. |
| Test | `test.body`; text link `test.settings` → `SettingsUrl`. No footer. |
| Account | body paragraph; if `Link`: **button** `account.{kind}.action`, then a muted line `account.linkFallback` + the full link. Footer: `account.footer`. |

## Change row

```
[LABEL]  Event name (bold) [low priority]
date/time (monospace)                    ← before → after for a time change
what: old → new | where it is/was        ← omitted for a time change
detected at …  (small, muted)
```

Label colours (text on background): added `#16673a/#e3f4ea`, removed `#a50d26/#fbe3e7`, field change
`#7a4a00/#fdf0d9`, low priority `#5a5a5a/#f0f0f0` (and the whole row in `#5a5a5a`). Calendar values in a row, and
calendar names, are at most 200 bytes once encoded, the "…" included when shortened.

## Guarantees (checked by tests)

- Text part, subject and headers byte-for-byte equal to the pre-feature snapshots (SC-004).
- Every inserted value encoded (SC-005), for every kind.
- Every colour in the HTML is in the palette; every text pair ≥ 4.5:1 except the wordmark (SC-003).
- HTML of a changes email with `Notifier.Listed` long Dutch changes, one calendar each, with values that grow when
  encoded, < 100 000 UTF-8 bytes (SC-007).
- `<html lang>`, the light-mode and format-detection metas and the 600px card are present in every kind.
