# Contract: Channel types (the provider seam, FR-003, US6)

A channel type is the only code that differs per provider (research R3). Adding web push or webhooks later means
adding one implementation of this interface and registering it. The notifier, the channel lifecycle and the API
stay unchanged.

```csharp
interface IChannelType
{
    string Name { get; }                  // "email"; matches channel.type (lowercase, ^[a-z][a-z0-9-]{0,30}$)
    bool Configured { get; }              // false → cannot add or confirm; pending deliveries expire (edge case)
    int MaxMessagesPerMinute { get; }     // FR-016; the notifier spaces sends by 1 min / this
    string? Normalize(string target);     // the stored target, or null when invalid (→ 400 invalid_target)
    Task<SendResult> SendAsync(string target, Message message, CancellationToken ct);
}

enum SendStatus { Sent, TargetFailed, TargetRejected, TypeUnavailable }
record SendResult(SendStatus Status, string? Reason = null);   // Reason: a code such as "smtp_550", never server text
```

| `SendStatus` | Meaning | What the notifier does (the same for every type) |
|---|---|---|
| `Sent` | Accepted for delivery | Marks the message's deliveries `sent`, or the `channel_message` sent. Resets channel and type backoff. Sets `last_message_at`. |
| `TargetFailed` | A temporary failure for this target only (SMTP 4xx on RCPT) | Channel backoff (research R4). Deliveries stay `pending` until they expire. |
| `TargetRejected` | This target will never work (SMTP 5xx on RCPT) | The channel becomes `failing` with `Reason`, and its pending deliveries become `expired` (FR-027) |
| `TypeUnavailable` | The provider itself failed (connection, timeout, authentication, any other error) | Type-wide backoff, and a channel backoff for the channel that hit it, so a single bad message cannot stall the type. No status changes (FR-027, US4-6). |

Every result other than `Sent` is logged with the channel id, the type and `Reason`, and nothing else (FR-030a,
FR-031). A type must never put a target, token or message text into `Reason` or into an exception that escapes.

## `Message`

The notifier builds one neutral message, and each type renders it in its own way. The texts come from the shared
`Texts` class (research R9), so both languages are the same for every type.

```csharp
record Message(
    MessageKind Kind,              // Confirmation | Changes | Test
    string Language,               // "en" | "nl", the subscriber's setting at send time (FR-017)
    TimeZoneInfo TimeZone,         // the subscriber's, else the operator default (FR-018a)
    string? ConfirmUrl,            // Confirmation only: {PublicBaseUrl}/n/confirm/{token}
    string? DeclineUrl,            // Confirmation only: {PublicBaseUrl}/n/decline/{token}
    string? UnsubscribeUrl,        // every kind except Confirmation: {PublicBaseUrl}/n/unsubscribe/{token} (FR-010)
    string? SettingsUrl,           // Test only: {FrontendBaseUrl}/notifications
    IReadOnlyList<CalendarGroup> Calendars,   // Changes only; ordered by each calendar's first change
    int NotListed);                // Changes beyond the 50 listed (FR-015)

record CalendarGroup(long CalendarId, string Name, string SettingsUrl, IReadOnlyList<ChangeItem> Changes);
//   Name: label → feed name → host (FR-019a); SettingsUrl: {FrontendBaseUrl}/calendars/{calendarId}/notifications (FR-009)

record ChangeItem(ChangeView Change, bool LowPriority);   // ChangeView from 002's FilterEngine; detection order
```

A message never holds a calendar link, a feed token or another subscriber's data (FR-020).

## Implementations

| Type | Where | Behaviour |
|---|---|---|
| `EmailChannel` | `backend/InTheKnow/EmailChannel.cs`, registered when the app starts | `Normalize`: trims the input and accepts it only if `MailAddress.TryCreate` succeeds, the parsed `Address` equals the input (no display name) and it is at most 254 characters. `Configured`: `Notifications:Email:Host` is set. Rendering and headers: [messages.md](./messages.md). Error mapping: research R2. |
| `RecordingChannel` | `backend/InTheKnow.Tests/RecordingChannel.cs`, **tests only** | `Name = "test"`. It records every `Message` it receives, and it can be scripted to return each non-`Sent` status. SC-008 runs the delivery suite over it and over email. |

The app registers types as `IEnumerable<IChannelType>` from DI. The tests add `RecordingChannel` through
`ConfigureTestServices`.
