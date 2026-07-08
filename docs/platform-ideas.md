# Additional Delivery Platform Ideas

> **Status: research notes on platforms considered and not pursued.** This is different from
> `docs/additional-features.md` (which surveys new *alert feeds*, i.e. new data sources) — this
> doc covers new *delivery platforms* (i.e. new places to post alerts), the same category as the
> existing Facebook/Instagram/X/Bluesky/Mastodon/Pushover/Twilio/Discord/DiscordDm/Telegram/VoipMs
> services already in `Services/`.

## Signal — rejected, not worth it (2026-07)

**Verdict: technically easy once running, but requires a fundamentally different deployment
model than every other platform in this bot. User decided not worth it.**

- Signal has **no official bot/business API** (still true as of 2026, confirmed no announcement of
  one). The standard community workaround is `signal-cli`, most commonly run via
  [`signal-cli-rest-api`](https://github.com/bbernhard/signal-cli-rest-api) — a Docker container
  wrapping it in a plain HTTP API.
- **Sending itself is trivial** and would fit this codebase's existing pattern well:
  `POST /v2/send` with a plain JSON body (`message`, `number`, `recipients`), no auth token needed
  by default. Would likely be one of the simplest `Service.cs` files in the bot.
- **What makes it hard is entirely infrastructure, not code**:
  - Requires a **separate, persistently-running process** (Docker container or Java process)
    alongside the .NET app — every other platform here is "call a public HTTPS endpoint"; this is
    "operate and maintain a second service." A real departure from this project's single-process
    deployment model (systemd service / Task Scheduler running one console app).
  - Account setup either **links to your personal Signal account** (bot then posts as you, tied to
    your personal number) or **registers a dedicated number** (requires SMS/voice verification,
    with unofficial clients having a history of tripping Signal's anti-abuse/CAPTCHA checks).
  - **No ToS blessing for bot behavior** — signal-cli is unofficial/reverse-engineered. Not a
    documented hard rate limit, but real risk of the account being flagged/challenged at
    alert-bot posting volumes.
  - Session/link state has to survive restarts and updates — one more thing to keep alive/backed up.
- **Conclusion**: skip. If this comes up again, the answer hasn't changed unless Signal ships an
  official bot API (no sign of that as of this research pass).

## Facebook Messenger — rejected, policy mismatch (2026-07)

**Verdict: technically easy (same Graph API / Page Access Token this bot already uses), but Meta's
own messaging policy blocks the actual use case — broadcasting alerts to a subscriber list who
haven't necessarily messaged the Page recently. Got *more* restrictive in 2026, not less.**

- The Send API integration itself would be simple — same Graph API family and Page Access Token
  already working in `FacebookService.cs` for Page posts. Not the blocker.
- **The blocker is the 24-hour messaging window policy**: a Page can only message a user within
  24 hours of that user last messaging the Page. Every reply resets the clock. There is no
  general-purpose "just broadcast it" mechanism the way Telegram/Discord/Twilio/VoIP.ms work in
  this bot today.
- **The one mechanism built for exactly this use case is now dead**: "Recurring Notifications"
  (renamed "Marketing Messages" — opt-in once, receive ongoing updates on a topic) **is no longer
  available in most countries as of 2026.**
- **The narrow exceptions got cut too**: effective 2026-04-27, three message tags that allowed
  messaging outside the 24-hour window (`CONFIRMED_EVENT_UPDATE`, `ACCOUNT_UPDATE`,
  `POST_PURCHASE_UPDATE`) now return an error (code 100) instead of sending.
- **The one surviving non-promotional exception**, `NON_PROMOTIONAL_SUBSCRIPTION`, is restricted to
  Pages formally registered in Meta's **News Page Index** — a separate editorial-vetting/business
  process, not something a personal weather alert Page would typically qualify for.
- **No weather/safety/emergency-alert carve-out exists** in Meta's policy docs (checked directly) —
  confirmed no special allowance for public-safety broadcast content outside the frameworks above.
- **Conclusion**: skip unless the Facebook Page pursues and obtains News Page Index registration.
  Without that, the only legitimate way to reach someone via Messenger is if they messaged the Page
  within the last 24 hours — impossible to guarantee ahead of a storm, so it can't do the job
  every other platform here does.

---

## Candidates worth pursuing (2026-07)

Prompted by wanting alternatives for people who specifically dislike Telegram. Ranked by fit.

### Matrix (Element) — top pick for "not a Telegram fan"

**Verdict: build this one first if any.** Matrix is *the* open-source, decentralized,
no-central-company messenger — exactly what people who dislike Telegram tend to already prefer or
be receptive to.

- Official, well-documented **Client-Server API**. Create a bot account on any homeserver (public
  matrix.org or self-hosted), get a non-expiring access token, then:
  `PUT /_matrix/client/v3/rooms/{roomId}/send/m.room.message/{txnId}` with a Bearer token and a
  JSON body (`msgtype`, `body`). No OAuth dance, no App Review, no broadcast/24-hour-window
  restriction — a room behaves like a Discord channel, everyone in it just gets the message.
- **Complexity**: Low — closest in shape to `DiscordService`'s webhook approach (single
  authenticated POST per message), just with a room ID + bearer token instead of a webhook URL.
  Free, no per-message cost.

### WhatsApp Business API — most-used messenger globally, and it actually has a path for this

**Verdict: viable and officially sanctioned, but real onboarding overhead — comparable to adding a
second Twilio-style SMS provider, not a quick webhook integration.**

- Confirmed directly in Meta's own template-categorization docs: there is a **"Public Safety"
  Utility template category that explicitly names severe weather notifications** as a qualifying
  use case. Unlike Messenger (whose equivalent mechanism was cut back in 2026), WhatsApp's Utility
  templates **can be sent proactively** — once a template is approved and a user has opted in once,
  it does not need the recipient to have messaged recently, unlike a plain freeform message.
- **Cost/setup**: requires Meta Business verification, the specific message template goes through
  Meta's content review (approval timeline not documented), and each utility template send costs a
  small per-message fee (~$0.004–$0.007, per pricing pages checked). Not free like every other
  platform in this bot.
- **Complexity**: Medium — the send call itself is a simple Graph-API-family POST (same family as
  `FacebookService.cs`), but getting a template approved and handling opt-in/opt-out (STOP handling
  is a hard requirement) is real, one-time setup work.

### Slack (incoming webhook) — trivial to build, good for group/team delivery

**Verdict: easy, same shape as the existing Discord webhook.**

- A single incoming webhook is one POST to a URL, no OAuth needed. Rate limit is ~1 msg/sec per
  channel — far above this bot's volume.
- **2026 wrinkle to verify at build time, not assume**: Slack tightened rate limits (down to
  1 req/min) specifically for apps *distributed to other workspaces* as of March 2026. A private,
  single-workspace incoming webhook (the intended use here) likely isn't the target of that
  change, but confirm against current Slack docs when actually implementing rather than relying on
  this note.

### Not worth pursuing
- **iMessage** — no real API. Would require actual Mac hardware and fragile AppleScript/Shortcuts
  automation against Messages.app. Skip.
- **Session** — Signal-like privacy messenger; no bot/API story found. Skip.
- **Threema** — has an official paid Gateway API (broadcast-capable, no window restriction,
  comparable in spirit to WhatsApp's), but a much smaller user base outside Europe. Not worth
  building unless a specific user base requests it.

---

## Simple push-notification services (Pushover-style)

Different category from full messenger platforms above — these are single-purpose "send an alert
to a phone" services, the same niche `PushoverService.cs` already fills. Listed for completeness /
future reference, not because Pushover needs replacing.

### ntfy — recommended if adding a second service in this category
- **Confirmed**: genuinely the simplest possible integration in this entire doc — `curl -d
  "message" ntfy.sh/yourtopic` is a complete example. No account, no API key, no signup at all for
  the public server; title/priority/tags are just HTTP headers. Open source, self-hostable
  (`docker run` a ~10MB binary), and supports UnifiedPush (open Android push standard) — the same
  "not tied to one company" appeal as Matrix, worth pairing with it for that audience.
- **Complexity**: Very low. Public `ntfy.sh` topics are unauthenticated by default (anyone who
  knows/guesses the topic name can read or publish to it) — fine for a low-stakes personal channel,
  but note this explicitly if ever recommending it, and prefer a self-hosted instance with auth
  enabled for anything more sensitive.

### Gotify — self-hosted alternative, more "dashboard" than "pipe"
- Self-hosted only (no free public instance like ntfy), uses per-application auth tokens, has a
  richer web UI/mobile app aimed at being a shared team notification hub rather than a simple
  publish pipe. No iOS app and no UnifiedPush support (Android-oriented). Reasonable alternative if
  a self-hosted dashboard matters more than the absolute simplicity of ntfy, otherwise ntfy is the
  easier build.

### Pushbullet — active, but has a real volume ceiling
- Confirmed still active and maintained (current API docs, active Home Assistant integration).
  **Free tier is capped at 500 pushes/month** — worth flagging explicitly, since an alert bot
  during an active severe weather stretch could plausibly approach or exceed that on its own,
  before multiplying by however many recipients. Would need to check current paid-tier pricing
  before treating this as viable for anything beyond light personal use.

### Bark — strong pick, iOS-only, matches Pushover's emergency-priority use case
- Confirmed active and well-documented. API is a plain GET/POST:
  `https://api.day.app/{device_key}/{title}/{body}` against the free hosted `day.app`, or
  self-host `bark-server` (small Docker image). No account beyond the device key from the app.
- Notably supports iOS **critical / time-sensitive interruption levels** — same purpose as
  `PushoverSettings.ExtremePriority` (bypassing Do Not Disturb/silent mode) but native to iOS,
  which could make it a good Extreme-severity companion specifically for iPhone users, alongside
  or instead of Pushover.
- **Complexity**: Very low — simplest full-featured option in this doc alongside ntfy. iOS-only is
  the one real limitation (no Android/desktop story).

### Pushsafer — direct Pushover alternative
- Confirmed active (2016–2026 site copyright), cross-platform (iOS/Android/Windows), API shape
  closely mirrors Pushover's (message, title, priority, sound, device key). Would mainly make sense
  as a second option for users who specifically don't want to use Pushover, not as a replacement.

### Prowl, Boxcar, SimplePush — checked, not worth pursuing further
- All three show up as recognized service names in the Apprise notification library's service
  list, but none could be confirmed as a genuinely current, actively-developed standalone service:
  Boxcar's own API repo is unmaintained, and Prowl/SimplePush have no clear 2026 activity signal
  beyond being listed as legacy Apprise targets. Not worth spending more research time on unless a
  specific user asks for one by name.

### Apprise — a different strategy: one gateway instead of N services
- Not a single service — **`apprise-api`** is a self-hosted REST gateway (one Docker container)
  that speaks to 80-130+ notification services through a single unified API, including ntfy,
  Gotify, Pushover, Bark, Pushsafer, Slack, Discord, Telegram, and email. One `AppriseService.cs`
  hitting your own Apprise container could cover most of this entire "Pushover-style" category (and
  several full-messenger candidates above) in one integration, instead of writing a new
  `Service.cs` per app.
- **Tradeoff**: same "one more container to run and keep alive" cost as Signal, but far
  lower-risk — Apprise is a mature, popular, actively-maintained project that calls each target
  service's own official API under the hood, not a reverse-engineered protocol. Worth considering
  as an alternative to adding individual services one at a time if the goal becomes "support as many
  notification channels as possible" rather than a specific named app.
