using System.Runtime.CompilerServices;

// Lets NwsAlertBot.Tests exercise internal parsing/formatting logic directly (e.g. SpcMcdService's
// LAT...LON parsing, MapService's IEM URL validation) without widening the public API surface.
[assembly: InternalsVisibleTo("NwsAlertBot.Tests")]
