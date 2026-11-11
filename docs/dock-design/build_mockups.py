#!/usr/bin/env python3
"""Generate Odysseus-styled agent-notch dock mockups and render annotated PNGs."""
from pathlib import Path
import subprocess

ROOT = Path("/workspace/agent-notch-dock/design")
HTML = ROOT / "html"
ANN = ROOT / "annotated"
HTML.mkdir(exist_ok=True)
ANN.mkdir(exist_ok=True)

# Shared chrome CSS (paths relative to html/)
CSS = r"""
@font-face{font-family:"Fira Code";src:url("../fonts/FiraCode-Variable.ttf") format("truetype");font-weight:300 700;font-display:block}
:root{
  --ody-bg:#0d1717;--ody-text:#aadcef;--ody-heading:#8bd9da;--ody-teal:#8bd9da;
  --ody-teal-mid:#2bb3b1;--ody-teal-deep:#0f6b68;--ody-bullet:#86b1e7;--ody-muted:#46575e;
  --ody-muted-2:#7495a1;--ody-yellow:#D8CB03;--ody-yellow-soft:#f0e46a;--ody-user:#162022;
  --ody-rule:rgba(139,217,218,.55);--ody-glass:rgba(12,20,12,.58);--ody-glass-heavy:rgba(12,20,12,.84);
  --ody-glass-border:rgba(139,217,218,.14);--ody-warn:#e8a13a;--ody-deny:#c45b5b;--ody-mic:#8199f5;
  --font:"Fira Code",ui-monospace,monospace;
  --collapsed-w:10px;--collapsed-h:196px;--rail-w:42px;--rail-h:285px;--avatar:24px;--avatar-pitch:39px;
  --chat-w:428px;--chat-h:601px;--gap:19px;--header-h:36px;--composer-h:38px;--composer-inset:12px;
}
*{box-sizing:border-box;margin:0;padding:0}
html,body{background:#071010;font-family:var(--font);color:var(--ody-text);-webkit-font-smoothing:antialiased}
.page{width:1760px;height:960px;position:relative;overflow:hidden;background:#071010}
.kicker{position:absolute;left:28px;top:18px;z-index:40;pointer-events:none}
.kicker .ey{font-size:11px;letter-spacing:.14em;text-transform:uppercase;color:var(--ody-muted)}
.kicker h1{font-size:20px;color:var(--ody-heading);font-weight:600;margin-top:4px}
.kicker p{font-size:12px;color:var(--ody-muted-2);margin-top:4px;max-width:640px;line-height:18px}
.desktop{position:absolute;left:40px;top:92px;width:1440px;height:720px;border-radius:10px;overflow:hidden;
  background:linear-gradient(180deg,#e8eef3 0%,#d5dde6 100%);box-shadow:0 18px 50px rgba(0,0,0,.45)}
.host{position:absolute;inset:0;padding:28px 72px 28px 36px;color:#3a4550;font-family:var(--font)}
.host h2{font-size:22px;color:#1c2430;font-weight:600}
.host .filters{display:flex;gap:8px;margin:16px 0 20px}
.host .chip{height:28px;padding:0 12px;border-radius:8px;background:#fff;border:1px solid #c9d3dc;font-size:12px;display:flex;align-items:center;color:#52606c}
.host .chip.on{background:#d7e8ff;border-color:#8bb6e8;color:#1a4d8c}
.host .metrics{display:flex;gap:10px;margin-bottom:16px}
.host .m{flex:1;max-width:180px;height:78px;border-radius:10px;background:#fff;border:1px solid #d5dee6;padding:10px 12px}
.host .m.b{background:#3b82f6;border:0;color:#fff}
.host .m.p{background:#6d28d9;border:0;color:#fff}
.host .m .n{font-size:26px;font-weight:600;line-height:1.1}
.host .m .l{font-size:11px;opacity:.8;margin-top:4px}
.host .chart{height:280px;background:#fff;border:1px solid #d5dee6;border-radius:12px;padding:16px;position:relative}
.host .chart svg{width:100%;height:230px}
.host .hint{position:absolute;left:28px;bottom:18px;font-size:11px;color:#7a8792}

/* dock layer inside desktop */
.dock-layer{position:absolute;inset:0;pointer-events:none}
.collapsed{position:absolute;right:0;top:262px;width:10px;height:196px;border-radius:5px 0 0 5px;
  background:rgba(12,20,12,.96);border:1px solid rgba(139,217,218,.45);border-right:0;
  box-shadow:inset 0 1px 0 rgba(220,255,250,.14),0 0 10px rgba(139,217,218,.25),0 4px 16px rgba(0,0,0,.4)}
.collapsed.pulse{animation:none;box-shadow:inset 0 1px 0 rgba(220,255,250,.1),0 0 0 2px rgba(216,203,3,.55),0 0 18px rgba(216,203,3,.65)}

.rail{position:absolute;right:0;top:218px;width:42px;height:285px;border-radius:21px 0 0 21px;
  background:rgba(12,20,12,.9);border:1px solid rgba(139,217,218,.16);border-right:0;
  box-shadow:inset 0 1px 0 rgba(220,255,250,.1),0 10px 30px rgba(0,0,0,.45);
  display:flex;flex-direction:column;align-items:center;padding:12px 0 10px;gap:15px}
.av{width:24px;height:24px;border-radius:12px;position:relative;flex:none}
.av.active{box-shadow:0 0 0 2px #0d1717,0 0 0 4px #8bd9da}
.av .dot{position:absolute;right:-1px;bottom:-1px;width:8px;height:8px;border-radius:4px;border:1.5px solid #0d1717}
.av .dot.idle{display:none}
.av .dot.busy{background:#8bd9da;box-shadow:0 0 6px rgba(139,217,218,.8)}
.av .dot.needs{background:#D8CB03;box-shadow:0 0 6px rgba(216,203,3,.85)}
.av .dot.wait{background:#D8CB03;box-shadow:0 0 0 2px rgba(216,203,3,.35)}
.av.user{background:radial-gradient(circle at 40% 35%,#d9ecec,#5a6e70);box-shadow:inset 0 0 0 1px rgba(255,255,255,.25)}

.chat{position:absolute;right:61px;top:60px;width:428px;height:601px;border-radius:16px;
  background:rgba(12,20,12,.88);border:1px solid rgba(139,217,218,.14);
  box-shadow:inset 0 1px 0 rgba(220,255,250,.1),0 16px 40px rgba(0,0,0,.5);
  -webkit-backdrop-filter:blur(24px) saturate(160%);backdrop-filter:blur(24px) saturate(160%);
  display:flex;flex-direction:column;overflow:hidden;color:var(--ody-text)}
.hd{height:36px;flex:none;display:flex;align-items:center;gap:8px;padding:0 12px;border-bottom:1px solid rgba(139,217,218,.08)}
.hd .nm{font-size:13px;font-weight:600;color:var(--ody-heading);flex:1}
.hd .ico{width:28px;height:28px;display:flex;align-items:center;justify-content:center;border-radius:8px;color:var(--ody-muted-2)}
.hd .share{height:24px;padding:0 10px;border-radius:8px;border:1px solid rgba(139,217,218,.22);color:var(--ody-text);font-size:11px;display:flex;align-items:center}
.msgs{flex:1;overflow:hidden;padding:10px 16px 8px;display:flex;flex-direction:column;gap:10px}
.as{border-left:2px solid var(--ody-rule);padding:2px 0 2px 12px;font-size:13.5px;line-height:21px}
.as p+p{margin-top:8px}
.as .h{color:var(--ody-heading);font-weight:600}
.us{align-self:flex-end;max-width:78%;background:#162022;color:var(--ody-text);border-radius:18px 18px 6px 18px;padding:8px 12px;font-size:13px;line-height:19px}
.typing{display:flex;align-items:center;gap:8px;font-size:12px;color:var(--ody-muted-2);padding-left:2px}
.typing i{width:6px;height:6px;border-radius:3px;background:var(--ody-teal);opacity:.7;display:inline-block}
.composer{height:38px;margin:0 12px 12px;border-radius:20px;flex:none;display:flex;align-items:center;gap:4px;padding:0 4px 0 4px;
  background:rgba(12,20,12,.58);border:1px solid rgba(139,217,218,.14);
  box-shadow:inset 0 1px 0 rgba(220,255,250,.1)}
.composer.tall{height:auto;min-height:38px;padding:8px 4px;border-radius:16px;align-items:flex-end}
.pb{width:30px;height:30px;border-radius:15px;display:flex;align-items:center;justify-content:center;flex:none}
.plus{background:rgba(216,203,3,.10);box-shadow:inset 0 0 0 1px rgba(216,203,3,.35)}
.inp{flex:1;min-width:0;font-size:13px;color:var(--ody-muted);white-space:nowrap;overflow:hidden}
.inp.typed{color:var(--ody-text)}
.caret{display:inline-block;width:1.5px;height:14px;background:var(--ody-teal);margin-left:1px;vertical-align:-2px}
.send{background:#8bd9da}
.mic.live{background:rgba(139,217,218,.18);box-shadow:0 0 0 1px rgba(139,217,218,.5),0 0 14px rgba(139,217,218,.45)}
.thumb{width:100%;height:86px;border-radius:10px;margin:0 8px 8px;background:linear-gradient(180deg,#1a2a2c,#0e1818);border:1px solid rgba(139,217,218,.12);position:relative;overflow:hidden}
.thumb .mini{position:absolute;inset:10px 16px;border-radius:6px;background:linear-gradient(90deg,#3b82f6 30%,#6d28d9 30% 55%,#eef2f5 55%);opacity:.9}

/* rich card */
.card{background:rgba(0,0,0,.28);border:1px solid rgba(139,217,218,.12);border-radius:12px;padding:14px}
.card h3{font-size:16px;color:#fff;font-weight:600;margin-bottom:6px}
.card .lede{font-size:12px;line-height:18px;color:var(--ody-text);opacity:.9}
.tiles{display:grid;grid-template-columns:1fr 1fr;gap:10px;margin-top:12px}
.tile{background:rgba(255,255,255,.03);border:1px solid rgba(139,217,218,.10);border-radius:12px;padding:10px 12px;min-height:78px}
.tile .v{font-size:26px;font-weight:600;color:#fff;line-height:1.1}
.tile .k{font-size:12px;color:var(--ody-text);margin-top:4px}
.tile .s{font-size:11px;color:var(--ody-muted-2);margin-top:2px}
.tile .s.warn{color:var(--ody-warn)}
.sect{margin-top:14px}
.sect h4{font-size:13px;color:#fff;font-weight:600;margin-bottom:8px}
.tabs{display:flex;gap:14px;font-size:12px;margin-bottom:8px}
.tabs span{color:var(--ody-muted-2)}
.tabs .on{color:var(--ody-heading);border-bottom:2px solid var(--ody-heading);padding-bottom:3px}
.chartbox{height:132px;border-radius:12px;background:rgba(8,18,20,.7);border:1px solid rgba(139,217,218,.1);position:relative;overflow:hidden}
.chartbox svg{position:absolute;inset:8px 8px 18px}
.ann{position:absolute;right:16px;top:18px;font-size:11px;color:var(--ody-muted-2)}
.insight{display:flex;gap:8px;padding:8px 10px;border-radius:10px;background:rgba(255,255,255,.03);border:1px solid rgba(139,217,218,.08);margin-bottom:6px}
.insight .ic{width:18px;flex:none;color:var(--ody-teal);font-size:14px}
.insight.warn .ic{color:var(--ody-warn)}
.insight b{display:block;font-size:12px;color:#fff;font-weight:600}
.insight span{display:block;font-size:11px;color:var(--ody-muted-2);line-height:16px}
.flow{border:1px solid rgba(139,217,218,.1);border-radius:12px;padding:12px;display:flex;flex-direction:column;align-items:center;gap:6px}
.fn{border:1px solid rgba(139,217,218,.22);border-radius:10px;padding:6px 14px;font-size:12px;color:var(--ody-text);text-align:center;background:rgba(0,0,0,.2)}
.fn small{display:block;color:var(--ody-muted-2);font-size:10px}
.farr{font-size:10px;color:var(--ody-muted)}
table.nt{width:100%;border-collapse:collapse;font-size:12px}
table.nt th{text-align:left;color:var(--ody-muted-2);font-weight:500;padding:6px 8px;border-bottom:1px solid rgba(139,217,218,.12)}
table.nt td{padding:9px 8px;border-bottom:1px solid rgba(139,217,218,.08);vertical-align:top;line-height:16px}
.next{margin-top:12px;padding:12px;border-radius:12px;background:rgba(139,217,218,.10);border:1px solid rgba(139,217,218,.22)}
.next .t{font-size:12px;color:var(--ody-heading);font-weight:600;margin-bottom:4px}
.next p{font-size:12px;line-height:18px}

/* approval */
.appr{border-radius:14px;padding:14px;background:rgba(0,0,0,.32);border:1px solid rgba(216,203,3,.35);
  box-shadow:0 0 0 1px rgba(216,203,3,.08),0 10px 28px rgba(0,0,0,.35)}
.appr .tag{display:inline-flex;align-items:center;gap:6px;font-size:10px;letter-spacing:.12em;text-transform:uppercase;color:var(--ody-yellow)}
.appr h3{font-size:14px;color:#fff;margin:8px 0 6px}
.appr .cmd{font-size:12px;background:rgba(0,0,0,.35);border-radius:8px;padding:8px 10px;color:var(--ody-text);border:1px solid rgba(139,217,218,.1);margin:8px 0}
.appr .why{font-size:12px;color:var(--ody-muted-2);line-height:18px}
.btns{display:flex;gap:8px;margin-top:12px}
.btn{height:36px;min-width:44px;padding:0 14px;border-radius:10px;display:flex;align-items:center;justify-content:center;font-size:13px;font-weight:600}
.btn.yes{background:#8bd9da;color:#0d1717}
.btn.no{background:transparent;color:var(--ody-text);border:1px solid rgba(196,91,91,.55)}
.appr.ok{border-color:rgba(139,217,218,.35)}
.appr.ok .tag{color:var(--ody-teal)}
.appr.no{border-color:rgba(196,91,91,.4)}
.appr.no .tag{color:var(--ody-deny)}

/* voice orb (css-only still — no bars) */
.orbwrap{display:flex;flex-direction:column;align-items:center;margin:8px 0 4px}
.orb{width:92px;height:92px;border-radius:50%;filter:blur(1.5px) saturate(120%);
  background:radial-gradient(circle at 35% 30%,#aadcef 0%,#8bd9da 28%,#2bb3b1 52%,#0f6b68 78%,transparent 82%),
             radial-gradient(circle at 70% 65%,rgba(216,203,3,.55),transparent 46%)}
.orb.listen{box-shadow:0 0 28px rgba(139,217,218,.45)}
.orb.think{background:radial-gradient(circle at 40% 40%,#f0e46a 0%,#8bd9da 40%,#2bb3b1 70%,transparent 78%),
            radial-gradient(circle at 65% 60%,rgba(216,203,3,.7),transparent 50%)}
.orb.speak{background:radial-gradient(circle at 40% 35%,#f0e46a 0%,#D8CB03 40%,#8bd9da 70%,transparent 80%)}
.orbcap{font-size:12px;color:var(--ody-heading);margin-top:6px}
.orbcap small{color:var(--ody-muted);margin-left:6px}

/* callouts */
.note{position:absolute;z-index:30;font-size:11px;line-height:15px;color:#e7f7f7;background:rgba(8,16,16,.88);
  border:1px solid rgba(139,217,218,.28);border-radius:8px;padding:7px 9px;max-width:220px;
  box-shadow:0 6px 18px rgba(0,0,0,.35)}
.note b{display:block;color:var(--ody-yellow);font-size:10px;letter-spacing:.08em;text-transform:uppercase;margin-bottom:2px}
.note em{font-style:normal;color:var(--ody-muted-2);font-size:10px}
.note.ref b{color:#9ad4ff}
.note.ody b{color:var(--ody-teal)}
.note.est b{color:var(--ody-warn)}
.leader{position:absolute;z-index:29;pointer-events:none}

.src{position:absolute;left:28px;bottom:16px;font-size:11px;color:#5e7272;z-index:40}
"""

def icons():
    gear = '''<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><circle cx="12" cy="12" r="3"/><path d="M12 2v3M12 19v3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M2 12h3M19 12h3M4.9 19.1 7 17M17 7l2.1-2.1"/></svg>'''
    share = '''Share'''
    plus = '''<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#D8CB03" stroke-width="2.3" stroke-linecap="round"><path d="M12 5v14M5 12h14"/></svg>'''
    mic = '''<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#8199f5" stroke-width="2" stroke-linecap="round"><rect x="9" y="3" width="6" height="11" rx="3"/><path d="M5.5 11a6.5 6.5 0 0 0 13 0M12 17.5V21"/></svg>'''
    send = '''<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="#0d1717" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M5 12h12M13 6l6 6-6 6"/></svg>'''
    spark = '''<svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="#0d1717" stroke-width="2"><path d="M12 3c.6 4.2 2.8 6.4 7 7-4.2.6-6.4 2.8-7 7-.6-4.2-2.8-6.4-7-7 4.2-.6 6.4-2.8 7-7z"/></svg>'''
    return dict(gear=gear, plus=plus, mic=mic, send=send, spark=spark)

ICO = icons()

def avatars(status=None, active=0):
    """status: list of idle|busy|needs|wait or None"""
    colors = ["#2bb3b1", "#86b1e7", "#c9844a", "#7a63d4", "#4aa07a", "#c46b8a"]
    # first is Odysseus teal with spark
    items = []
    st = status or ["idle"] * 6
    for i, c in enumerate(colors):
        cls = "av active" if i == active else "av"
        if i == 0:
            inner = f'<div class="{cls}" style="background:linear-gradient(145deg,#8bd9da,#0f6b68);display:flex;align-items:center;justify-content:center">{ICO["spark"]}<i class="dot {st[i]}"></i></div>'
        else:
            inner = f'<div class="{cls}" style="background:{c}"><i class="dot {st[i]}"></i></div>'
        items.append(inner)
    items.append('<div class="av user"></div>')
    return "\n".join(items)

def host_app():
    return '''<div class="host">
      <h2>Performance</h2>
      <div class="filters"><div class="chip">24 hours</div><div class="chip">7 days</div><div class="chip">28 days</div><div class="chip on">3 months</div></div>
      <div class="metrics">
        <div class="m b"><div class="n">451</div><div class="l">Clicks</div></div>
        <div class="m p"><div class="n">18.6K</div><div class="l">Impressions</div></div>
        <div class="m"><div class="n">2.4%</div><div class="l">Click rate</div></div>
        <div class="m"><div class="n">12.3</div><div class="l">Avg. position</div></div>
      </div>
      <div class="chart">
        <svg viewBox="0 0 640 200" preserveAspectRatio="none">
          <polyline fill="none" stroke="#3b82f6" stroke-width="2" points="0,120 40,110 80,130 120,90 160,140 200,100 240,115 280,80 320,125 360,95 400,110 440,70 480,60 520,85 560,50 600,55 640,40"/>
          <polyline fill="none" stroke="#6d28d9" stroke-width="2" points="0,150 40,145 80,155 120,148 160,160 200,142 240,150 280,140 320,146 360,138 400,130 440,120 480,90 520,100 560,70 600,75 640,68"/>
        </svg>
      </div>
      <div class="hint">Host window behind the dock · generic analytics chrome, not a vendor UI</div>
    </div>'''

def note(x, y, kind, title, body, width=200):
    cls = {"ref": "note ref", "ody": "note ody", "est": "note est"}.get(kind, "note")
    return f'<div class="{cls}" style="left:{x}px;top:{y}px;width:{width}px"><b>{title}</b>{body}</div>'

def line(x1,y1,x2,y2,color="rgba(139,217,218,.45)"):
    # page-absolute SVG line
    xmin,ymin=min(x1,x2),min(y1,y2)
    return (f'<svg class="leader" style="left:{xmin}px;top:{ymin}px;width:{abs(x2-x1)+2}px;height:{abs(y2-y1)+2}px">'
            f'<line x1="{x1-xmin}" y1="{y1-ymin}" x2="{x2-xmin}" y2="{y2-ymin}" stroke="{color}" stroke-width="1.5"/>'
            f'<circle cx="{x2-xmin}" cy="{y2-ymin}" r="2.5" fill="#8bd9da"/></svg>')

def page(title, sub, body, extras=""):
    return f'''<!doctype html><html><head><meta charset="utf-8"><style>{CSS}</style></head>
<body><div class="page">
  <div class="kicker"><div class="ey">Agent notch · Odysseus</div><h1>{title}</h1><p>{sub}</p></div>
  {body}
  {extras}
  <div class="src">Layout px from alexmichael-dock.mp4 @ 1416×720 / 60fps (1 video-px ≈ 1 CSS px). Color, type, glass, user pills and voice from Odysseus. Callout colors: blue = reference · teal = Odysseus · gold = estimated.</div>
</div></body></html>'''

# Desktop origin on the page
DX, DY = 40, 92

def desktop(inner):
    return f'<div class="desktop">{host_app()}<div class="dock-layer">{inner}</div></div>'

def composer(kind="idle", text=""):
    if kind == "idle":
        return f'''<div class="composer">
      <div class="pb plus">{ICO["plus"]}</div>
      <div class="inp">Message Odysseus</div>
      <div class="pb">{ICO["mic"]}</div>
    </div>'''
    if kind == "typing":
        return f'''<div class="composer tall">
      <div class="thumb"><div class="mini"></div></div>
      <div style="display:flex;align-items:center;width:100%">
        <div class="pb plus">{ICO["plus"]}</div>
        <div class="inp typed">{text}<i class="caret"></i></div>
        <div class="pb send">{ICO["send"]}</div>
      </div>
    </div>'''
    if kind == "ready":
        return f'''<div class="composer">
      <div class="pb plus">{ICO["plus"]}</div>
      <div class="inp">Message Odysseus</div>
      <div class="pb">{ICO["mic"]}</div>
    </div>'''
    if kind == "listen":
        return f'''<div class="composer">
      <div class="pb plus">{ICO["plus"]}</div>
      <div class="inp typed">Voice mode</div>
      <div class="pb mic live">{ICO["mic"]}</div>
    </div>'''
    return composer("idle")

def chat_shell(msgs_html, composer_html, name="Odysseus"):
    return f'''<aside class="chat">
      <div class="hd">
        <div class="av active" style="width:22px;height:22px;background:linear-gradient(145deg,#8bd9da,#0f6b68);display:flex;align-items:center;justify-content:center">{ICO["spark"]}</div>
        <div class="nm">{name}</div>
        <div class="ico">{ICO["gear"]}</div>
        <div class="share">Share</div>
      </div>
      <div class="msgs">{msgs_html}</div>
      {composer_html}
    </aside>'''

# ---------- states ----------
def state_collapsed():
    inner = '<div class="collapsed"></div>'
    extras = "".join([
        note(80, 300, "ref", "Collapsed pill", "Flush to the right edge, vertically mid-screen.<br>10×196px · radius 5<br><em>Estimated — absent at t=0.000; 8–16×~195px strip detected t=0.35–0.63s</em>", 230),
        note(80, 430, "ody", "Odysseus glass", "Not the reference solid #101010.<br>rgba(12,20,12,.92) + teal 1px edge.", 220),
        note(80, 530, "ref", "Open motion", "Rail expand 100ms (t=0.717→0.817, 34→42px) after a click dip.<br>Easing estimated snappy ease-out.", 250),
        line(310, 340, DX+1440-10, DY+262+98),
    ])
    return page("1 · Collapsed",
                "Tiny vertical notch on the right edge. FEATURES.md describes this resting state; the source clip never holds it steady.",
                desktop(inner), extras)

def state_rail():
    inner = f'<div class="rail">{avatars(status=["busy","idle","needs","idle","idle","idle"])}</div>'
    extras = "".join([
        note(70, 200, "ref", "Rail 42 × 285", "Measured x=1374–1415, y=172–456.<br>Pill radius 21 (width/2).<br>Right edge flush, left side rounded.", 230),
        note(70, 320, "ref", "Avatars 24 · pitch 39", "Six bot marks + a grayscale account/add at the bottom.<br>Active ring 2px (white in the clip → teal here).", 240),
        note(70, 430, "ody", "Live status dots · added", "FEATURES.md addition, not in the clip.<br>Teal = busy · Yellow = needs you · Hidden = idle.<br>Dot 8px, 1.5px panel ring.", 250),
        note(70, 560, "ref", "Gap after chat opens", "19px of desktop shows between the chat panel and the rail. They are separate surfaces.", 240),
        line(300, 250, DX+1440-42, DY+218+20),
        line(300, 360, DX+1440-21, DY+218+24),
        line(320, 470, DX+1440-8, DY+218+36),
    ])
    return page("2 · Rail open",
                "Narrow avatar rail slides out from the right. Chat is still closed.",
                desktop(inner), extras)

def state_chat():
    msgs = '''
      <div class="as">
        <p>I can read the window behind this dock and turn the numbers into a dashboard you can keep.</p>
        <p class="h">Ask me about the last 3 months.</p>
        <p>I’ll pull clicks, impressions, click rate and average position, then flag what moved after October 20.</p>
      </div>
    '''
    inner = f'<div class="rail">{avatars(status=["busy","idle","idle","idle","idle","idle"])}</div>' + chat_shell(msgs, composer("idle"))
    extras = "".join([
        note(48, 170, "ref", "Chat 428 × 601", "Measured x=927–1354, y=14–614 before the camera zoom.<br>Sits 19px left of the rail (right: 42+19=61).", 250),
        note(48, 280, "est", "Radius 16", "Clip window corners ~10–12px (macOS). Odysseus sheets use 28. Overlay compromise: 16.", 230),
        note(48, 370, "ref", "Header 36", "Name + gear + Share. Traffic lights (12px, 20px pitch) appear in the clip; the Odysseus overlay omits them — this is not a titled window.", 260),
        note(48, 500, "ody", "Messages", "No boxed cards. Assistant = teal 2px left rule.<br>Reference used full-width lighter blocks and a pink user border — those are not used.", 260),
        note(48, 620, "ref", "Composer idle 38 × pill", "Measured y=550–587 of the 601 crop.<br>12px inset. + left, mic right, placeholder “Message &lt;bot&gt;”.", 250),
        note(48, 740, "ody", "Glass composer", "Odysseus pill tokens: blur 24 / saturate 160 / border rgba(139,217,218,.14). Yellow + and periwinkle mic.", 250),
        line(300, 200, DX+1440-61-428, DY+60),
        line(300, 400, DX+1440-61-200, DY+60+18),
        line(300, 640, DX+1440-61-214, DY+60+601-12-19),
    ])
    return page("3 · Chat open",
                "Panel attached to the left of the rail with a 19px gap. Jump-cut in the source at t=1.800 — implement as a 280ms slide (estimated).",
                desktop(inner), extras)

def state_typing():
    msgs = '''
      <div class="as"><p>The last report covered crawls only. Send a screenshot and I’ll read the graph.</p></div>
      <div class="us">how is my seo performing in the last 3 months?</div>
      <div class="typing"><i></i><i></i><i></i> Analyzing the SEO metrics</div>
    '''
    inner = f'<div class="rail">{avatars(status=["busy","idle","idle","idle","idle","idle"])}</div>' + chat_shell(msgs, composer("idle"))
    extras = "".join([
        note(48, 200, "ody", "User bubble", "Right-aligned, max 78%, #162022, radius 18 18 6 18.<br>Replaces the reference pink-border bubble (#ff4b97).", 250),
        note(48, 330, "ref", "Send + typing", "User message pops in around t=5.67 (pink-pixel jump at 15fps).<br>FEATURES: “Typing...” then stream line by line.<br>Clip caption: “Analyzing the SEO metrics”.", 270),
        note(48, 480, "est", "Stream in", "Per-line fade ~80ms — not isolated in the 720p encode. Composer grows with a screenshot thumb (visible at frame-04, +~80px estimated after undoing 1.15× zoom).", 270),
        note(48, 630, "ody", "Mic → send", "Odysseus: empty field shows the mic; typed text cross-fades to a teal send arrow in 160ms.", 240),
        line(300, 250, DX+1440-61-80, DY+60+280),
        line(300, 360, DX+1440-61-300, DY+60+340),
    ])
    return page("4 · Typing and streaming",
                "Message pops into the thread, a short status line shows, then the reply streams in.",
                desktop(inner), extras)

def state_rich():
    card = '''
      <div class="us">how is my seo performing in the last 3 months?</div>
      <div class="card" style="padding:12px">
        <h3 style="font-size:15px">SEO, last 3 months</h3>
        <div class="lede">Visibility roughly doubled after Oct 20. Moving page-2 queries to page 1 is the next win.</div>
        <div class="tiles">
          <div class="tile" style="min-height:70px;padding:8px 10px"><div class="v" style="font-size:24px">451</div><div class="k">Clicks</div><div class="s">Visits from Google</div></div>
          <div class="tile" style="min-height:70px;padding:8px 10px"><div class="v" style="font-size:24px">18.6K</div><div class="k">Impressions</div><div class="s">Times shown in results</div></div>
          <div class="tile" style="min-height:70px;padding:8px 10px"><div class="v" style="font-size:24px">2.4%</div><div class="k">Click rate</div><div class="s">Fair for page 2</div></div>
          <div class="tile" style="min-height:70px;padding:8px 10px"><div class="v" style="font-size:24px">12.3</div><div class="k">Avg. position</div><div class="s warn">Top of page 2</div></div>
        </div>
        <div class="sect" style="margin-top:10px">
          <h4>Trend over time</h4>
          <div class="tabs"><span class="on">Impressions</span><span>Clicks</span></div>
          <div class="chartbox" style="height:96px">
            <div class="ann">Jump after Oct 20</div>
            <svg viewBox="0 0 360 110" preserveAspectRatio="none">
              <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#8bd9da" stop-opacity=".45"/><stop offset="1" stop-color="#8bd9da" stop-opacity="0"/></linearGradient></defs>
              <path d="M0,70 C40,68 80,72 120,66 C160,62 200,58 240,40 C280,22 320,20 360,24 L360,110 L0,110 Z" fill="url(#g)"/>
              <path d="M0,70 C40,68 80,72 120,66 C160,62 200,58 240,40 C280,22 320,20 360,24" fill="none" stroke="#8bd9da" stroke-width="2"/>
            </svg>
          </div>
        </div>
      </div>
      <div class="insight"><div class="ic">↑</div><div><b>What’s working</b><span>Impressions climbed after Oct 20 and held.</span></div></div>
      <div class="insight warn"><div class="ic">!</div><div><b>Stuck on page 2 · Fix</b><span>Position 12.3 — most people never scroll far enough.</span></div></div>
    '''
    # The chat is 601 tall so we show the top of the rich card; callouts explain the rest of the parts
    msgs = card
    inner = f'<div class="rail">{avatars(status=["busy","idle","idle","idle","idle","idle"])}</div>' + chat_shell(msgs, composer("idle"))
    extras = "".join([
        note(36, 160, "ref", "Metric tiles 2×2", "Clicks 451 · Impressions 18.6K · Click rate 2.4% · Avg. position 12.3.<br>Tile gap 12 · radius 12 · value 28px · estimated fit.", 250),
        note(36, 300, "ref", "Trend + tabs", "Impressions / Clicks tabs and a note on the line (“Jump after Oct 20”). Chart ~140px tall, estimated from the zoomed frame-12 crop ÷ 1.45.", 260),
        note(36, 440, "ref", "Insight rows", "What’s working (up) and What to watch (alert). Reference used green/orange icons → teal / #e8a13a.", 250),
        note(36, 520, "ody", "Card chrome", "Dark glass nested cards, not the reference’s flat #181818 slabs. Next box uses rgba(139,217,218,.10) instead of the clip’s blue panel.", 260),
        '''<div style="position:absolute;left:36px;top:620px;width:860px;display:flex;gap:10px;z-index:30">
          <div class="card" style="flex:1;padding:10px;background:rgba(8,16,16,.92)">
            <h4 style="font-size:12px;color:#fff;margin-bottom:8px">Where the growth comes from</h4>
            <div class="flow" style="padding:8px;gap:4px">
              <div class="fn">Page 2 today</div><div class="farr">content and links ↓</div>
              <div class="fn">Reach page 1<small>Top queries into the top 10</small></div>
              <div class="farr">more people see you ↓</div>
              <div class="fn">Better snippets</div>
              <div class="farr">click rate above 2.4% ↓</div>
              <div class="fn">More visits and installs</div>
            </div>
          </div>
          <div class="card" style="flex:1.3;padding:10px;background:rgba(8,16,16,.92)">
            <h4 style="font-size:12px;color:#fff;margin-bottom:6px">Next steps</h4>
            <table class="nt"><tr><th>Step</th><th>Why</th><th>Effort</th></tr>
            <tr><td>Pull the Queries tab</td><td>Find searches at positions 8 to 20</td><td>5 min</td></tr>
            <tr><td>Rewrite top page titles</td><td>Lift the 2.4% click rate</td><td>1 hr</td></tr>
            <tr><td>Build pages for near-misses</td><td>Push page-2 terms onto page 1</td><td>Ongoing</td></tr></table>
            <div class="next" style="margin-top:8px;padding:8px 10px"><div class="t">Next</div>
              <p>If you send screenshots of the Queries and Pages tabs, I’ll add them here and pick the exact searches to go after.</p></div>
          </div>
        </div>''',
        line(290, 200, DX+1440-61-300, DY+60+250),
        line(290, 340, DX+1440-61-220, DY+60+430),
    ])
    return page("5 · Rich reply card",
                "The bot reads the window behind the dock and answers with an inline dashboard. Structure from the clip; surface from Odysseus.",
                desktop(inner), extras)

def state_approval():
    msgs = '''
      <div class="as"><p>Cursor stopped for permission before editing the file.</p></div>
      <div class="appr">
        <div class="tag">● Pending · needs you</div>
        <h3>Allow Cursor to edit src/notch/Dock.tsx?</h3>
        <div class="cmd">write src/notch/Dock.tsx · 184 lines</div>
        <div class="why">The session is waiting. Approve to continue, or deny and I’ll ask it to propose a smaller change.</div>
        <div class="btns"><div class="btn no">Deny</div><div class="btn yes">Approve</div></div>
      </div>
    '''
    inner = f'<div class="rail">{avatars(status=["wait","idle","idle","idle","idle","idle"])}</div>' + chat_shell(msgs, composer("idle"))
    # plus collapsed pulse demo as a mini and resolved states as insets
    extras = "".join([
        note(36, 160, "ody", "Approval · pending", "FEATURES.md addition. Yellow edge, Approve (teal fill) / Deny (muted red outline). Buttons 36px tall, 44px min target.", 250),
        note(36, 290, "ody", "Notch pulse", "While pending, the collapsed notch (and the active rail avatar) pulses yellow. Period ~1200ms, estimated.", 240),
        note(36, 400, "ody", "Rail status", "waiting_approval = yellow dot + soft ring on the active avatar. busy = teal. needs_you = yellow. idle = hidden.", 250),
        note(36, 530, "ody", "Voice (Aether)", "Long-press mic. No bars, lines, rings or waveforms — a soft teal/yellow blob. States: idle · listening · thinking · speaking. 400ms ease; pop-in 460ms spring.", 270),
        # resolved mini cards on the left bottom
        '''<div style="position:absolute;left:36px;top:680px;display:flex;gap:12px;z-index:30">
          <div class="appr ok" style="width:240px">
            <div class="tag">✓ Approved</div>
            <h3 style="font-size:13px">Edit allowed</h3>
            <div class="why">Card stays as a receipt. 220ms ease into this state (estimated).</div>
          </div>
          <div class="appr no" style="width:240px">
            <div class="tag">✕ Denied</div>
            <h3 style="font-size:13px">Edit blocked</h3>
            <div class="why">Same card, deny tint. The session is told to stop.</div>
          </div>
          <div style="width:200px;display:flex;flex-direction:column;align-items:center;background:rgba(8,16,16,.88);border:1px solid rgba(139,217,218,.28);border-radius:10px;padding:10px">
            <div class="orb listen" style="width:64px;height:64px"></div>
            <div class="orbcap">Listening… <small>Aether</small></div>
          </div>
        </div>''',
        line(290, 200, DX+1440-61-300, DY+60+160),
        line(290, 320, DX+1440-8, DY+218+20),
    ])
    return page("6 · Approval card",
                "When Claude Code or Cursor stops for permission, an Approve / Deny card appears and the notch pulses. Voice states from Odysseus / Aether.",
                desktop(inner), extras)

# Voice stills strip is included on approval page.

pages = {
    "01-collapsed": state_collapsed(),
    "02-rail-open": state_rail(),
    "03-chat-open": state_chat(),
    "04-typing-streaming": state_typing(),
    "05-rich-reply": state_rich(),
    "06-approval": state_approval(),
}

# Clean (no callouts) versions for the overview by stripping .note and .leader and .src? 
# Overview is a dedicated page that inlines scaled clean stages.

def clean_desktop(inner, label):
    return f'''<div class="cell">
      <div class="clab">{label}</div>
      <div class="stage">
        <div class="desktop" style="position:relative;left:0;top:0;width:1440px;height:720px;transform:scale(0.36);transform-origin:top left">
          {host_app()}<div class="dock-layer">{inner}</div>
        </div>
      </div>
    </div>'''

overview_cells = [
    clean_desktop('<div class="collapsed pulse"></div>', "Collapsed"),
    clean_desktop(f'<div class="rail">{avatars(status=["busy","idle","needs","idle","idle","idle"])}</div>', "Rail open"),
    clean_desktop(
        f'<div class="rail">{avatars(status=["busy","idle","idle","idle","idle","idle"])}</div>' +
        chat_shell('''<div class="as"><p>I can read the window behind this dock and turn the numbers into a dashboard.</p></div>''', composer("idle")),
        "Chat open"),
    clean_desktop(
        f'<div class="rail">{avatars(status=["busy","idle","idle","idle","idle","idle"])}</div>' +
        chat_shell('''<div class="us">how is my seo performing in the last 3 months?</div><div class="typing"><i></i><i></i><i></i> Analyzing the SEO metrics</div>''', composer("idle")),
        "Typing & streaming"),
    clean_desktop(
        f'<div class="rail">{avatars(status=["busy","idle","idle","idle","idle","idle"])}</div>' +
        chat_shell('''<div class="us">how is my seo performing…</div>
          <div class="card"><h3>SEO, last 3 months</h3>
          <div class="tiles">
            <div class="tile"><div class="v">451</div><div class="k">Clicks</div></div>
            <div class="tile"><div class="v">18.6K</div><div class="k">Impressions</div></div>
            <div class="tile"><div class="v">2.4%</div><div class="k">Click rate</div></div>
            <div class="tile"><div class="v">12.3</div><div class="k">Avg. position</div></div>
          </div></div>''', composer("idle")),
        "Rich reply"),
    clean_desktop(
        f'<div class="rail">{avatars(status=["wait","idle","idle","idle","idle","idle"])}</div>' +
        chat_shell('''<div class="appr"><div class="tag">● Pending · needs you</div>
          <h3>Allow Cursor to edit Dock.tsx?</h3>
          <div class="btns"><div class="btn no">Deny</div><div class="btn yes">Approve</div></div></div>''', composer("idle")),
        "Approval"),
]

ov_css = CSS + """
.overview{width:1720px;height:780px;background:#071010;padding:24px 28px;position:relative}
.overview h1{font-size:22px;color:var(--ody-heading);font-weight:600}
.overview .sub{font-size:13px;color:var(--ody-muted-2);margin:6px 0 22px;max-width:1100px;line-height:20px}
.grid{display:grid;grid-template-columns:repeat(3,518px);gap:18px 22px}
.cell{position:relative;width:518px}
.stage{width:518px;height:259px;overflow:hidden;border-radius:8px;box-shadow:0 10px 28px rgba(0,0,0,.35)}
.clab{font-size:13px;color:var(--ody-heading);margin-bottom:8px;letter-spacing:.04em}
"""

overview = f'''<!doctype html><html><head><meta charset="utf-8"><style>{ov_css}</style></head>
<body><div class="overview">
  <div class="ey" style="font-size:11px;letter-spacing:.14em;text-transform:uppercase;color:#46575e">Agent notch · Odysseus</div>
  <h1>Screen map · every state</h1>
  <div class="sub">Collapsed, rail, chat, typing/streaming, rich reply, approval. Layout measured from the Alex Michael clip; glass, Fira Code, teal/yellow voice and user pills from Odysseus. Approval, live status and voice are FEATURES.md additions.</div>
  <div class="grid">{''.join(overview_cells)}</div>
</div></body></html>'''

for name, html in pages.items():
    (HTML / f"{name}.html").write_text(html)
    print("wrote", name, "bytes", len(html))
(HTML / "00-overview.html").write_text(overview)
print("wrote overview", len(overview))

# Render
from playwright.sync_api import sync_playwright

shots = [
    ("01-collapsed.html", "collapsed.png", 1760, 960),
    ("02-rail-open.html", "rail-open.png", 1760, 960),
    ("03-chat-open.html", "chat-open.png", 1760, 960),
    ("04-typing-streaming.html", "typing-streaming.png", 1760, 960),
    ("05-rich-reply.html", "rich-reply.png", 1760, 960),
    ("06-approval.html", "approval.png", 1760, 960),
    ("00-overview.html", "overview.png", 2680, 1180),
]

with sync_playwright() as p:
    browser = p.chromium.launch()
    for html_name, png_name, w, h in shots:
        page = browser.new_page(viewport={"width": w, "height": h}, device_scale_factor=1)
        page.goto((HTML / html_name).as_uri(), wait_until="networkidle")
        page.evaluate("document.fonts.ready")
        out = ANN / png_name
        page.screenshot(path=str(out), type="png")
        print("shot", out, out.stat().st_size)
        page.close()
    browser.close()
print("done")
