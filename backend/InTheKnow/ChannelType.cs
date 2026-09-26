namespace InTheKnow;

// The provider seam (contracts/channel-type.md, research R3): only what differs per provider. Retries, backoff,
// expiry, the rate limit, language and statuses are shared, in Notifier and Channels.
interface IChannelType
{
    string Name { get; }                  // "email"; matches channel.type (^[a-z][a-z0-9-]{0,30}$)
    bool Configured { get; }              // false → cannot add or confirm; pending deliveries expire
    int MaxMessagesPerMinute { get; }     // FR-016; the notifier spaces sends by 1 min / this
    string? Normalize(string target);     // the stored target, or null when invalid
    Task<SendResult> SendAsync(string target, Message message, CancellationToken ct);
}

enum SendStatus { Sent, TargetFailed, TargetRejected, TypeUnavailable }

// Reason: a code such as "smtp_550", never server text (FR-031).
sealed record SendResult(SendStatus Status, string? Reason = null);

enum MessageKind { Confirmation, Changes, Test }

// One neutral message; each type renders it in its own way. Never holds a calendar link or another subscriber's data (FR-020).
sealed record Message(MessageKind Kind, string Language, TimeZoneInfo TimeZone, string? ConfirmUrl, string? DeclineUrl,
                      string? UnsubscribeUrl, string? SettingsUrl, IReadOnlyList<CalendarGroup> Calendars, int NotListed);

sealed record CalendarGroup(long CalendarId, string Name, string SettingsUrl, IReadOnlyList<ChangeItem> Changes);

sealed record ChangeItem(ChangeView Change, bool LowPriority);
