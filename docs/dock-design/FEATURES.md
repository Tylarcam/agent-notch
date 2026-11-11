# Agent notch dock: reverse-engineered from the @alexmichael post (x.com/alexmichael/status/2120333678874075528)

## Placement and opening
- Collapsed: a tiny black vertical pill flush against the middle of the right screen edge, layered over any window.
- Open (about 0:01): a narrow dark avatar rail slides out from the right edge, and a wider chat panel slides out to its left, attached to the rail.

## Avatar rail
- A vertical stack of round, colored bot avatars. The active bot has a white ring. No unread badges appear in the clip.

## Chat panel
- Header: the bot's avatar, its name ("Storybox"), a settings gear and a Share button.
- Messages: bot replies are full-width blocks, slightly lighter than the dark background. The user's messages sit on the right with a pink border.
- Input: placeholder "Message <bot>", a + for attachments on the left and a mic on the right. The box grows taller while typing.
- Sending: the message pops into the chat, "Typing..." shows briefly, then the reply streams in line by line.

## Rich reply (the key feature)
- The bot read the window behind the dock (Google Search Console) and answered with an inline dashboard card. The card held:
  - metric tiles: Clicks 451, Impressions 18.6K, Click rate 2.4%, Avg. position 12.3
  - a line chart with Impressions and Clicks tabs and a note on the line ("Jump after Oct 20")
  - insight rows with green up arrows ("What's working") and orange alert icons ("What to watch")
  - a small flow diagram ("Where the growth comes from")
  - a table with Step, Why and Effort columns
  - a "Next" box asking for screenshots of more tabs

## What we add
- Approvals: when Claude Code or Cursor stops for permission, an Approve/Deny card appears and the notch pulses.
- Live agent status on the rail, from agent-notch's existing session detection.
- Voice input through Aether, which converts speech to text locally. Chat goes through the Odysseus relay.
