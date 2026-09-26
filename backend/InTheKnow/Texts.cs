using System.Globalization;

namespace InTheKnow;

// Every fixed text of emails and the /n/ pages, in English and Dutch (research R9). Both languages have the same keys (SC-009).
static class Texts
{
    static readonly Dictionary<string, Dictionary<string, string>> All = new()
    {
        ["en"] = new()
        {
            ["subject.confirmation"] = "Confirm your InTheKnow notifications",
            ["subject.change"] = "1 timetable change – {0}",
            ["subject.changes"] = "{0} timetable changes – {1}",
            ["subject.changesCalendars"] = "{0} timetable changes in {1} calendars",
            ["subject.test"] = "InTheKnow test notification",

            ["confirmation.intro"] = "A student added this email address to InTheKnow to receive notifications about their timetable changes.",
            ["confirmation.confirm"] = "Confirm notifications for this address",
            ["confirmation.decline"] = "This wasn't me",
            ["confirmation.expiry"] = "The confirmation link expires after 48 hours. If you did not expect this email, choose \"This wasn't me\" and this address gets no more email from InTheKnow for this student.",

            ["changes.settings"] = "Notification settings for this calendar",
            ["changes.lowPriority"] = "low priority",
            ["changes.detectedAt"] = "detected at {0}",
            ["changes.more"] = "and {0} more changes",
            ["changes.footer"] = "You receive this email because this address was confirmed for InTheKnow timetable notifications. Use your mail client's unsubscribe option to stop these notifications to this address.",

            ["test.body"] = "This is a test notification from InTheKnow. It contains no changes.",
            ["test.settings"] = "Notification settings",

            ["kind.added"] = "Added",
            ["kind.removed"] = "Removed",
            ["kind.name"] = "Name",
            ["kind.time"] = "Time",
            ["kind.status"] = "Status",
            ["kind.location"] = "Location",
            ["kind.description"] = "Description",
            ["kind.activityType"] = "Activity type",
            ["kind.rooms"] = "Rooms",
            ["kind.teachers"] = "Teachers",
            ["kind.groups"] = "Groups",

            ["page.confirm.title"] = "Confirm notifications",
            ["page.confirm.text"] = "Confirm timetable notifications from InTheKnow for this address.",
            ["page.confirm.button"] = "Confirm",
            ["page.decline.title"] = "This wasn't me",
            ["page.decline.text"] = "If you did not add this address, this address gets no more email from InTheKnow for this student.",
            ["page.decline.button"] = "This wasn't me",
            ["page.unsubscribe.title"] = "Unsubscribe",
            ["page.unsubscribe.text"] = "Stop these notifications to this address.",
            ["page.unsubscribe.button"] = "Unsubscribe",
            ["page.confirm.done"] = "Notifications are confirmed for this address.",
            ["page.decline.done"] = "Done. This address gets no more email from InTheKnow for this student.",
            ["page.unsubscribe.done"] = "Done. These notifications are no longer sent to this address.",
            ["page.alreadyDone"] = "This address is already confirmed. Nothing changed.",
            ["page.expired"] = "This link has expired. Request a new confirmation email in InTheKnow.",
            ["page.declined"] = "This is not possible: this address chose \"This wasn't me\". Nothing changed.",
            ["page.unavailable"] = "This is not available right now. Try again later. Nothing changed.",
            ["page.notFound.title"] = "Link not valid",
            ["page.notFound.text"] = "This link is not valid.",
        },
        ["nl"] = new()
        {
            ["subject.confirmation"] = "Bevestig je InTheKnow-meldingen",
            ["subject.change"] = "1 roosterwijziging – {0}",
            ["subject.changes"] = "{0} roosterwijzigingen – {1}",
            ["subject.changesCalendars"] = "{0} roosterwijzigingen in {1} roosters",
            ["subject.test"] = "InTheKnow-testmelding",

            ["confirmation.intro"] = "Een student heeft dit e-mailadres aan InTheKnow toegevoegd om meldingen over zijn of haar roosterwijzigingen te krijgen.",
            ["confirmation.confirm"] = "Meldingen voor dit adres bevestigen",
            ["confirmation.decline"] = "Dit was ik niet",
            ["confirmation.expiry"] = "De bevestigingslink verloopt na 48 uur. Verwachtte je deze e-mail niet, kies dan \"Dit was ik niet\" en dit adres krijgt geen e-mail meer van InTheKnow voor deze student.",

            ["changes.settings"] = "Meldingsinstellingen voor dit rooster",
            ["changes.lowPriority"] = "lage prioriteit",
            ["changes.detectedAt"] = "gevonden op {0}",
            ["changes.more"] = "en nog {0} wijzigingen",
            ["changes.footer"] = "Je krijgt deze e-mail omdat dit adres is bevestigd voor roostermeldingen van InTheKnow. Gebruik de afmeldoptie van je mailprogramma om deze meldingen naar dit adres te stoppen.",

            ["test.body"] = "Dit is een testmelding van InTheKnow. Er staan geen wijzigingen in.",
            ["test.settings"] = "Meldingsinstellingen",

            ["kind.added"] = "Toegevoegd",
            ["kind.removed"] = "Verwijderd",
            ["kind.name"] = "Naam",
            ["kind.time"] = "Tijd",
            ["kind.status"] = "Status",
            ["kind.location"] = "Locatie",
            ["kind.description"] = "Beschrijving",
            ["kind.activityType"] = "Soort activiteit",
            ["kind.rooms"] = "Lokalen",
            ["kind.teachers"] = "Docenten",
            ["kind.groups"] = "Groepen",

            ["page.confirm.title"] = "Meldingen bevestigen",
            ["page.confirm.text"] = "Bevestig roostermeldingen van InTheKnow voor dit adres.",
            ["page.confirm.button"] = "Bevestigen",
            ["page.decline.title"] = "Dit was ik niet",
            ["page.decline.text"] = "Heb je dit adres niet toegevoegd, dan krijgt dit adres geen e-mail meer van InTheKnow voor deze student.",
            ["page.decline.button"] = "Dit was ik niet",
            ["page.unsubscribe.title"] = "Afmelden",
            ["page.unsubscribe.text"] = "Stop deze meldingen naar dit adres.",
            ["page.unsubscribe.button"] = "Afmelden",
            ["page.confirm.done"] = "Meldingen zijn bevestigd voor dit adres.",
            ["page.decline.done"] = "Gelukt. Dit adres krijgt geen e-mail meer van InTheKnow voor deze student.",
            ["page.unsubscribe.done"] = "Gelukt. Deze meldingen worden niet meer naar dit adres gestuurd.",
            ["page.alreadyDone"] = "Dit adres is al bevestigd. Er is niets veranderd.",
            ["page.expired"] = "Deze link is verlopen. Vraag in InTheKnow een nieuwe bevestigingsmail aan.",
            ["page.declined"] = "Dit kan niet: voor dit adres is \"Dit was ik niet\" gekozen. Er is niets veranderd.",
            ["page.unavailable"] = "Dit kan nu even niet. Probeer het later opnieuw. Er is niets veranderd.",
            ["page.notFound.title"] = "Link niet geldig",
            ["page.notFound.text"] = "Deze link is niet geldig.",
        },
    };

    public static string Get(string language, string key) => All[language][key]; // KeyNotFoundException on an unknown key

    public static string Format(string language, string key, params object[] args) =>
        string.Format(Culture(language), Get(language, key), args);

    public static string Date(string language, TimeZoneInfo zone, DateTimeOffset t) =>
        TimeZoneInfo.ConvertTime(t, zone).ToString("ddd d MMM yyyy HH:mm", Culture(language));

    // IANA ids only: on Linux .NET also resolves Windows ids, which are rejected (research R9).
    public static TimeZoneInfo? FindTimeZone(string id) =>
        TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z) && z.HasIanaId ? z : null;

    public static IReadOnlyCollection<string> Keys(string language) => All[language].Keys;

    static CultureInfo Culture(string language) => CultureInfo.GetCultureInfo(language == "nl" ? "nl-NL" : "en-GB");
}
