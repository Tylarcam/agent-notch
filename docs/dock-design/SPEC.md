# Agent notch dock — design spec

Reverse-engineered from `@alexmichael` (`agent-notch-dock/ref/alexmichael-dock.mp4` and `FEATURES.md`), then restyled to Odysseus. Built for the Cursor Composer implementation on Tylar’s Mac.

Where the clip and Odysseus disagree, **use Odysseus**. Values are tagged **reference**, **Odysseus**, or **estimated**.

## How this was measured

- Source clip: `1416 × 720`, 60 fps, 16.967 s, 1018 frames, H.264.
- Extra frames extracted with ffmpeg at 10 fps across the clip, 15 fps around send/stream, and 60 fps at `0.00–1.00 s` (rail) and `1.55–2.25 s` (chat appear).
- Dock pieces were split by walking columns from the right edge where mean luminance `< 55`: rail, then a light gap (desktop showing through), then the chat panel.
- After `t ≈ 2.2 s` the recording **zooms** (chat width 428 → 492 → 584 → 620, ratios 1.00 / 1.15 / 1.36 / 1.45). Later-frame sizes were divided by that ratio. Design tokens use the pre-zoom pair `frame-02s` / `t = 1.80–2.15`.
- Scale: macOS traffic lights in the chat chrome measure ~12 px with ~20 px center-to-center, which matches 1× logical pixels. **1 video-px ≈ 1 CSS px.**

## Placement and opening

| State | What happens |
|---|---|
| Collapsed | A tiny vertical pill sits flush on the middle of the right screen edge, over any window. |
| Rail open | A 42 × 285 avatar rail slides out from the right edge. |
| Chat open | A 428 × 601 chat panel sits to the left of the rail with a **19 px** gap (desktop shows through). They are two surfaces, not one window. |

The clip never holds a stable collapsed pill at `t = 0.000` (not visible in `frame-00s`). A thin 8–16 px strip grows from `t = 0.350–0.633`, dips at `t = 0.667` (likely a click), then the rail expands.

## Dimensions

All CSS px. `ref` = measured. `est` = estimated. `ody` = Odysseus token.

### Collapsed notch

| Token | px | Source | Notes |
|---|---:|---|---|
| width | 10 | est | Detected 8–16 px while appearing. FEATURES.md: “tiny black vertical pill.” |
| height | 196 | est | ~195 px at `t = 0.450` (noisy on an 8 px strip). |
| radius | 5 | est | Half of width (matches the rail’s pill rule). |
| vertical position | mid-right | ref | FEATURES.md + the appearing strip sits around the vertical middle. |

### Avatar rail

| Token | px | Source | Notes |
|---|---:|---|---|
| width | 42 | ref | Settles at 42 from `t = 0.817` (`x = 1374–1415`). |
| height | 285 | ref | `y = 172–456` on the 720 frame. |
| radius | 21 | ref | Full pill; flush to the right so only the left side is rounded (`21 0 0 21`). |
| top padding | 12 | ref | First avatar starts at `y ≈ 184`. |
| avatar size | 24 | ref | Bodies 20–26 px; 24 is typical. Active including ring ≈ 26. |
| avatar pitch | 39 | ref | Center-to-center: 39, 38, 41, 35, 39. |
| active ring | 2 | ref | White in the clip → Odysseus teal `#8bd9da`. |
| status dot | 8 | est | FEATURES.md addition (not in the clip). 1.5 px panel-colored ring. |
| items | 7 | ref | Six colored bots + a grayscale account/add at the bottom. |

### Chat panel

| Token | px | Source | Notes |
|---|---:|---|---|
| width | 428 | ref | `x = 927–1354` at `t = 1.800–2.150`. |
| height | 601 | ref | `y = 14–614`. |
| radius | 16 | est | Clip corners ~10–12 (macOS-like). Odysseus sheets use 28. Overlay compromise: 16. |
| gap to rail | 19 | ref | Chat right `x = 1354`, rail left `x = 1374`. |
| right offset | 61 | ref | `rail 42 + gap 19`. |
| header height | 36 | ref | Top of window to ~`y = 50`. |
| header pad x | 12 | est | |
| traffic-light diameter | 12 | ref | Present in the clip. **Omit on the Odysseus overlay** — this is not a titled window. |
| traffic-light pitch | 20 | ref | |
| message pad x | 16 | ody | Stealth chat uses 16–18. |
| composer idle height | 38 | ref | `y = 550–587` of the 601-px crop. |
| composer inset | 12 | ref / ody | Matches the Odysseus phone pill inset. |
| composer radius | 20 | est | Idle composer is a pill (~half of 38). Odysseus phone pill is 27 on a 54-px height. |
| composer while typing | grows | ref | Attachment thumbnail + text at `frame-04s`. Extra height ~70–90 px after undoing 1.15× zoom (**estimated**). |
| min tap target | 44 | ody | Odysseus rule. Header icon hit-areas should pad out to 44 even if the glyph is smaller. |

## Color

### Odysseus (use these)

| Token | Value | Used for |
|---|---|---|
| `--ody-bg` | `#0d1717` | Page / panel base |
| `--ody-text` | `#aadcef` | Body |
| `--ody-heading` / `--ody-teal` | `#8bd9da` | Titles, active ring, send fill, “what’s working” |
| `--ody-teal-mid` | `#2bb3b1` | Voice orb |
| `--ody-teal-deep` | `#0f6b68` | Voice orb |
| `--ody-bullet` | `#86b1e7` | Bullets / secondary marks |
| `--ody-muted` | `#46575e` | Placeholder, captions |
| `--ody-muted-2` | `#7495a1` | Meta, unused icons |
| `--ody-yellow` | `#D8CB03` | Plus, needs-you, pending pulse |
| `--ody-yellow-soft` | `#f0e46a` | Voice speaking highlight |
| `--ody-user` | `#162022` | User bubble fill |
| User radius | `18px 18px 6px 18px` | User bubble |
| Assistant rule | `2px solid rgba(139,217,218,.55)` | Left edge, no boxed cards |
| Glass fill | `rgba(12,20,12,.58)` / heavy `.82` | Panel + composer |
| Glass fallback | `rgba(11,15,11,.94)` | No `backdrop-filter` |
| Glass border | `rgba(139,217,218,.14)` | |
| Glass inset + shadow | `inset 0 1px 0 rgba(220,255,250,.12)`, `0 10px 30px rgba(0,0,0,.55)` | |
| Glass blur / saturate | `24px` / `160%` | |
| Plus chip | fill `rgba(216,203,3,.10)`, ring `rgba(216,203,3,.35)` | |
| Mic icon | `#8199f5` | |
| Warn | `#e8a13a` | **Estimated** stand-in for the clip’s orange “Top of page 2” / alert |
| Deny | `#c45b5b` | **Estimated** — no deny color in the clip |

### Reference-only (do not ship)

| Sample | Value | Why it was dropped |
|---|---|---|
| Panel fill | `#101010` / `#131313` | Replaced by Odysseus glass on `#0d1717` |
| Input fill | `#191919` (`rgb(25,25,25)`) | Replaced by the glass composer |
| User / “5 new messages” / voice button | `#ff4b97` (sampled ~`rgb(255,72,148)`) | Odysseus user pills and teal/yellow voice replace all pink |

## Radii, shadows, blur

| Surface | Radius | Shadow / blur | Source |
|---|---|---|---|
| Collapsed pill | 5 | Soft teal edge + `0 4px 16px rgba(0,0,0,.4)` | est / ody |
| Rail | 21 (left only) | Glass inset + `0 10px 30px rgba(0,0,0,.45)` | ref / ody |
| Chat panel | 16 | Glass inset + `0 16px 40px rgba(0,0,0,.5)`, blur 24 | est / ody |
| Composer | 20 | Glass | est / ody |
| User bubble | 18 18 6 18 | none | ody |
| Metric tile / chart / next | 12 | 1 px teal-tinted border | est |
| Insight row | 10 | | est |
| Approval card | 14 | Yellow edge while pending | est |
| Buttons | 10 | | est |

## Type

Family: **Fira Code** (variable), file `design/fonts/FiraCode-Variable.ttf`. Odysseus sampled this stack from the phone chat; it is not the clip’s UI font (the clip looks like a system sans).

| Role | Size / line-height / weight | Source |
|---|---|---|
| Body | 13.5 / 21 / 400 | ody |
| Chat title | 14.5 / — / 600 | ody |
| Header name | 13 / — / 600 | est (fits a 36-px header) |
| Meta / caption | 11 / — / 400, `#46575e` | ody |
| Metric value | 28 / 1.1 / 600 | est |
| Metric label | 12 | est |
| Placeholder | 13, `#46575e` | est |
| Table | 12 / 18 | est |
| Section heading | 16 / — / 600, `#8bd9da` or white on cards | ody / est |

## Motion

Method: 60 fps frame extract + column-width walk, unless noted.

| Transition | Duration | Easing | Source | Notes |
|---|---:|---|---|---|
| Collapsed appear | 280 ms | ease-out | ref | `t = 0.350 → 0.633`, width 2 → 16. May be a hover-reveal rather than the resting state. |
| Rail open | 100 ms | `cubic-bezier(0.22, 1, 0.36, 1)` **est** | ref | `t = 0.717` (34 px) → `0.817` (42 px). Counted from the click dip at `0.667`: ~150 ms. Too few samples to fit a curve; use a snappy ease-out. |
| Chat open | 280 ms | `cubic-bezier(0.22, 1, 0.36, 1)` | **est** | **Not measurable.** Absent at `t = 1.783`, fully formed 428 × 601 at `t = 1.800` (a jump cut). FEATURES.md describes a slide; implement a 280 ms slide from the right. |
| Send pop | 180 ms | `cubic-bezier(0.34, 1.22, 0.64, 1)` | est | User bubble appears ~`t = 5.67` (pink-pixel count 3709 → 4844 at 15 fps). Spring borrowed from the Odysseus orb pop. |
| Typing indicator | ~400 ms dwell | — | ref / est | FEATURES.md: “Typing...” then stream. `frame-06s` shows “Analyzing the SEO metrics.” |
| Stream per line | ~80 ms | fade | est | Described as line-by-line; not isolated in the 720p encode. |
| Mic → send | 160 ms | ease | ody | Cross-fade + scale. |
| Voice state change | 400 ms | ease | ody | |
| Voice pop in | 460 ms | `cubic-bezier(.34,1.56,.64,1)` | ody | |
| Voice pop out | 240 ms | ease-in | ody | |
| Notch pulse (approval) | 1200 ms period | ease-in-out | est | FEATURES.md: the notch pulses while pending. |
| Approval resolve | 220 ms | ease | est | Pending → approved / denied. |

Camera zoom after `t = 2.2 s` is editorial, not a UI animation.

## Chat structure (from the clip, styled Odysseus)

**Header.** Active bot avatar (22–24), name, settings gear, Share. The clip also shows macOS traffic lights and a small live-preview blob of the desktop in the top-right of the header. Gear is listed in FEATURES.md; Share is clearly visible. Traffic lights and the preview blob are clip chrome — drop them on the overlay.

**Messages.**
- Assistant: full-width, no card, 2 px teal left rule (Odysseus). The clip used slightly lighter full-width blocks on `#101010`.
- User: right-aligned dark pill, max-width 78% (Odysseus). The clip used a pink-bordered bubble.
- The clip’s “5 new messages” pink chip is a Storybox artifact — do not ship.

**Composer.** `+` (attach) on the left, placeholder `Message <bot>`, mic on the right. The clip adds a pink waveform button next to the mic; Odysseus does **not** — no bars, lines, rings or waveforms. Empty field = mic (tap to dictate, long-press ~450 ms for voice mode). Typed text = teal send arrow.

## Rich reply card

The bot read the window behind the dock and answered with an inline dashboard. Structure is from the clip (`frame-08s` through `frame-16s`). Sizes below are **estimated** from those zoomed frames after dividing by 1.45, then fitted to a 428 − 32 = 396 px content width.

| Part | Structure | Size / notes |
|---|---|---|
| Title + lede | “SEO, last 3 months” + one-sentence summary | Title 16 / 600; lede 12 / 18 |
| Metric tiles | 2 × 2 grid: Clicks 451, Impressions 18.6K, Click rate 2.4%, Avg. position 12.3 | Gap 12, radius 12, min-height ~88 (70 in the compact mock), value 28, label 12, caption 11. “Top of page 2” uses warn `#e8a13a`. |
| Trend chart | Impressions / Clicks tabs; area+line; annotation “Jump after Oct 20” | Chart height ~140 (96 in the compact in-panel crop), radius 12 |
| Insight rows | “What’s working” (up) and “What to watch” / “Stuck on page 2 · Fix” (alert) | Row pad 8 10, radius 10. Clip used green/orange icons → teal / warn |
| Flow diagram | “Where the growth comes from”: Page 2 today → Reach page 1 → Better snippets → More visits and installs, with captions between | Node radius 10, stacked capsules |
| Table | Columns Step / Why / Effort. Rows: Pull the Queries tab (5 min); Rewrite top page titles (1 hr); Build pages for near-miss searches (Ongoing) | Row height ~44 |
| Next box | “If you send screenshots of the Queries and Pages tabs…” | Pad 12, radius 12, fill `rgba(139,217,218,.10)` (clip used a blue panel) |

Section gap ~18. Card pad 16 (12 in the compact mock). Nested on glass, not the clip’s flat `#181818` slabs.

## FEATURES.md additions

These are **not** in the video. Designed in the same Odysseus language.

### Approval card

States: `pending` · `approved` · `denied`.

- **Pending.** Card in the thread, yellow (`#D8CB03`) edge. Eyebrow “Pending · needs you”. Title names the tool and the action (`Allow Cursor to edit src/notch/Dock.tsx?`). A `cmd` row shows the payload. Copy explains that the session is waiting. Buttons: **Deny** (outline, `#c45b5b` border) and **Approve** (teal fill, dark label). Height 36, min width 44.
- While pending, the **collapsed notch pulses** yellow (~1200 ms) and the active rail avatar shows a yellow status dot + soft ring.
- **Approved.** Same card, teal edge, eyebrow “Approved”, kept as a receipt. 220 ms ease.
- **Denied.** Same card, deny tint, eyebrow “Denied”. The session is told to stop.

### Live agent status (rail)

From agent-notch’s existing session detection.

| State | Dot | Also |
|---|---|---|
| `idle` | Hidden | Default |
| `busy` | Teal `#8bd9da`, soft glow | Agent is running |
| `needs_you` | Yellow `#D8CB03` | Waiting on a non-approval question |
| `waiting_approval` | Yellow + 2 px soft ring | Pairs with the pulsing notch |

Dot is 8 px, bottom-right of the 24 px avatar, with a 1.5 px panel-colored halo so it reads on any avatar color.

### Voice (Aether → Odysseus relay)

Long-press the mic (~450 ms). Chat dims (`rgba(5,10,10,.8)`). A soft morphing blob rises above the composer — **no bars, lines, rings, spokes or waveforms**.

| State | Look |
|---|---|
| idle | Small, dim teal, slow breath |
| listening | Teal; size follows mic level. Caption “Listening…” |
| thinking | Teal/yellow swirl, steady size. “Thinking…” |
| speaking | Yellow-led, pulses with TTS. “Speaking…” |

State ease 400 ms. Pop in 460 ms spring; pop out 240 ms ease-in. Reduced motion: still per state, opacity-only pop. Colors: `#8bd9da` / `#2bb3b1` / `#0f6b68` and `#D8CB03` / `#f0e46a` on `#0d1717`.

## What was deliberately not copied

- Storybox name, pink “5 new messages” chip, pink user bubbles, pink voice button.
- macOS traffic lights (overlay, not a titled window).
- Solid `#101010` panels.
- Vendor UI of the host window (the mockups use generic analytics chrome so the dock can be seen as an overlay).

## Files

- `tokens.json` / `tokens.css` — same values, machine-readable.
- `html/01-collapsed.html` … `html/06-approval.html` — annotated mockup sources.
- `html/00-overview.html` — all six states.
- `annotated/*.png` — rendered with headless Chrome at 1×.
- `fonts/FiraCode-Variable.ttf` — real type, no hand lettering.
- `build_mockups.py` — regenerates the HTML.

## Open questions

1. The clip never shows a resting collapsed pill — only a brief 8–16 px strip. Is 10 × 196 the size Tylar wants, or should the notch stay closer to the existing agent-notch hit target (larger than 10 px so it’s tappable)?
2. Chat open is a jump cut in the source. 280 ms slide is a recommendation, not a measurement — confirm the easing with Tylar.
3. The clip’s header includes a live desktop preview blob that FEATURES.md does not list. Include it, or keep the overlay header to avatar + name + gear + Share?
4. Multi-bot rail: the clip has six colorful bots + a grayscale account. For Odysseus, is the rail “every connected agent (Claude Code, Cursor, …)” plus Odysseus on top?
5. Approval copy and the exact payload row (file path vs. shell command vs. MCP tool) should follow whatever Cursor / Claude Code actually pause on.
6. Voice: confirm Aether always STT-locally and only the transcript hits the Odysseus relay (as FEATURES.md states) before wiring the orb to a real level meter.
