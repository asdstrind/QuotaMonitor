"""Compose QuotaMonitor v1.7.0 public artwork from offline production-view renders.

Inputs are RenderTargetBitmap captures of the unchanged production WPF views produced by
tools/QuotaMonitor.Showcase (invented synthetic data, no adapters/network/desktop capture).
Outputs go to docs/media/v1.7.0/. Requires Pillow + numpy and the installed Segoe UI fonts.

Usage (from repository root):
  python tools/compose-showcase.py evidence/v1.7.0/visual/showcase docs/media/v1.7.0 \
      C:/Windows/Fonts src/QuotaMonitor/Assets/QuotaMonitor.png
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

SRC = Path(sys.argv[1])
OUT = Path(sys.argv[2])
FONT_DIR = Path(sys.argv[3] if len(sys.argv) > 3 else "C:/Windows/Fonts")
LOGO_PATH = Path(sys.argv[4]) if len(sys.argv) > 4 else None
OUT.mkdir(parents=True, exist_ok=True)

TEXT = (238, 246, 242)
TEXT_SOFT = (196, 210, 207)
MUTED = (138, 154, 155)
ACCENT = (127, 207, 180)
ACCENT_DIM = (95, 156, 139)
STAMP_BG = (15, 37, 33)
STAMP_LINE = (46, 82, 74)
STAMP_INK = (169, 215, 202)

WEIGHTS = {"regular": "segoeui.ttf", "light": "segoeuil.ttf", "semibold": "seguisb.ttf", "bold": "segoeuib.ttf"}

# Shared presentation header geometry: kicker/brand ~36 px, title ~92 px, subtitle ~150 px.
# Pasted production UI starts no earlier than 230 px so nothing can sit on the subtitle.
TITLE_Y = 92
SUBTITLE_Y = 150
CONTENT_TOP = 232


def font(size, weight="regular"):
    return ImageFont.truetype(str(FONT_DIR / WEIGHTS[weight]), size)


def text_width(draw, text, f):
    return draw.textlength(text, font=f)


def round_logo(size):
    if LOGO_PATH is None or not LOGO_PATH.exists():
        return None
    logo = Image.open(LOGO_PATH).convert("RGBA")
    logo.thumbnail((size, size), Image.Resampling.LANCZOS)
    mask = Image.new("L", (size, size), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, size - 1, size - 1), radius=max(8, size // 4), fill=255)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(logo, ((size - logo.width) // 2, (size - logo.height) // 2), logo)
    return out


def backdrop(width, height):
    yy, xx = np.mgrid[0:height, 0:width].astype(np.float32)
    x = xx / width
    y = yy / height
    img = np.empty((height, width, 3), np.float32)
    img[..., 0] = 10.5 + 4.0 * (1.0 - y)
    img[..., 1] = 13.5 + 5.0 * (1.0 - y)
    img[..., 2] = 17.5 + 7.0 * (1.0 - y)
    teal = np.exp(-((((x - 0.10) / 0.55) ** 2 + ((y - 0.0) / 0.52) ** 2)) * 1.7)
    img += teal[..., None] * np.array([9.0, 33.0, 28.0], np.float32)
    violet = np.exp(-((((x - 1.04) / 0.46) ** 2 + ((y - 1.06) / 0.52) ** 2)) * 1.8)
    img += violet[..., None] * np.array([23.0, 13.0, 40.0], np.float32)
    vignette = np.clip(1.0 - 0.42 * (((x - 0.5) ** 2) * 1.7 + ((y - 0.5) ** 2) * 1.15), 0.55, 1.0)
    img *= vignette[..., None]
    return Image.fromarray(np.clip(img, 0, 255).astype(np.uint8), "RGB")


def render(name, width=None):
    art = Image.open(SRC / name).convert("RGBA")
    bbox = art.getchannel("A").getbbox()
    if bbox is None:
        raise SystemExit(f"empty render: {name}")
    art = art.crop(bbox)
    if width:
        art = art.resize((width, max(1, round(art.height * width / art.width))), Image.Resampling.LANCZOS)
    return art


def paste_art(canvas, art, x, y, shadow=True):
    if shadow:
        pad = 40
        shadow_layer = Image.new("RGBA", (art.width + pad * 2, art.height + pad * 2), (0, 0, 0, 0))
        shadow_layer.paste((0, 0, 0, 150), (pad, pad + 6), art.getchannel("A"))
        shadow_layer = shadow_layer.filter(ImageFilter.GaussianBlur(20))
        canvas.paste(shadow_layer, (x - pad, y - pad), shadow_layer)
    canvas.paste(art, (x, y), art)


def draw_stamp(im):
    draw = ImageDraw.Draw(im)
    label = "SYNTHETIC DEMO DATA"
    f = font(15, "semibold")
    width = text_width(draw, label, f) + 16 * 2 + 20
    height = 34
    x = im.width - 40 - width
    y = 34
    draw.rounded_rectangle((x, y, x + width, y + height), radius=17, fill=STAMP_BG, outline=STAMP_LINE, width=1)
    draw.ellipse((x + 15, y + height / 2 - 3, x + 21, y + height / 2 + 3), fill=ACCENT)
    draw.text((x + 29, y + 9), label, font=f, fill=STAMP_INK)


def draw_brand(im, label=None):
    draw = ImageDraw.Draw(im)
    size = 30
    logo = round_logo(size)
    x = 46
    y = 36
    if logo is not None:
        im.paste(logo, (x, y), logo)
    tx = x + (size + 12 if logo is not None else 0)
    word_font = font(17, "semibold")
    draw.text((tx, y + 5), "QuotaMonitor", font=word_font, fill=TEXT)
    if label:
        offset = tx + text_width(draw, "QuotaMonitor", word_font) + 14
        draw.text((offset, y + 8), label, font=font(13, "semibold"), fill=ACCENT)


def draw_header(im, title, subtitle=None, title_size=40):
    draw = ImageDraw.Draw(im)
    draw.text((56, TITLE_Y), title, font=font(title_size, "bold"), fill=TEXT)
    if subtitle:
        draw.text((58, SUBTITLE_Y), subtitle, font=font(18), fill=MUTED)


def draw_footer(im, note):
    draw = ImageDraw.Draw(im)
    draw.text((46, im.height - 40), note, font=font(13), fill=(104, 118, 120))
    version = "v1.7.0"
    f = font(13, "semibold")
    width = text_width(draw, version, f) + 24
    x = im.width - 46 - width
    y = im.height - 44
    draw.rounded_rectangle((x, y, x + width, y + 26), radius=13, outline=(44, 58, 60), width=1)
    draw.text((x + 12, y + 5), version, font=f, fill=(150, 176, 170))


def draw_caption(im, x, y, text, size=19, color=TEXT_SOFT):
    ImageDraw.Draw(im).text((x, y), text, font=font(size), fill=color)


def save(im, name):
    rgb = im.convert("RGB")
    rgb.save(OUT / name, "PNG", optimize=True)
    return name, rgb.size, (OUT / name).stat().st_size


def compose_hero():
    width, height = 1440, 1040
    im = backdrop(width, height)
    draw_brand(im, "v1.7.0")
    draw_stamp(im)
    draw = ImageDraw.Draw(im)
    draw.text((58, 128), " ".join("WINDOWS 11 X64   ·   ALWAYS IN VIEW"), font=font(15, "semibold"), fill=ACCENT)
    draw.text((56, 156), "Your AI quota, at a glance.", font=font(50, "bold"), fill=TEXT)
    draw.text((58, 228), "Codex   /   DeepSeek Harness   /   Antigravity   /   OpenCode Go", font=font(20), fill=TEXT_SOFT)
    art = render("expanded-2x.png", width=1140)
    paste_art(im, art, (width - art.width) // 2, 272)
    draw.text((58, height - 84), "Four providers. One quiet island.", font=font(22, "semibold"), fill=TEXT)
    draw.text((60, height - 50), "Actual QuotaMonitor v1.7.0 WPF UI · Synthetic demo data · no real account", font=font(14), fill=MUTED)
    return save(im, "hero.png")


def compose_clean(name, render_name, label, caption, art_width, footer_note, extra_height=0, top=124):
    art = render(render_name, width=art_width)
    height = top + art.height + (86 if caption else 46) + 58 + extra_height
    im = backdrop(1440, height)
    draw_brand(im, label)
    draw_stamp(im)
    paste_art(im, art, (im.width - art.width) // 2, top)
    if caption:
        draw_caption(im, 60, top + art.height + 42, caption)
    draw_footer(im, footer_note)
    return save(im, name)


def compose_minimal():
    # The Minimal state is only an 8 px strip. Show it at the app's own 150% raster scale on a
    # faint "top of screen" hairline instead of magnifying it into a decorative frame.
    art = render("minimal.png", width=264)
    height = 400
    im = backdrop(1440, height)
    draw = ImageDraw.Draw(im)
    draw.line((60, 250, 1380, 250), fill=(30, 42, 44), width=2)
    paste_art(im, art, (1440 - art.width) // 2, 250 - art.height // 2, shadow=False)
    draw_brand(im, "MINIMAL")
    draw_stamp(im)
    draw.text((58, 300), "An 8 px resting strip at the screen edge. Hover reveals the capsule; click expands the island.", font=font(19), fill=TEXT_SOFT)
    draw.text((60, 336), "Shown at the app's 150% raster scale — actual strip size, no magnification.", font=font(14), fill=MUTED)
    draw_footer(im, "Actual production WPF view · offline render (no desktop capture)")
    return save(im, "minimal.png")


def compose_states():
    rows = [
        ("01", "MINIMAL", "An 8 px strip resting at the top edge. Hover shows the capsule; click expands.", render("minimal.png", width=264)),
        ("02", "COLLAPSED", "One capsule: each visible provider's limiting value, readable at a glance.", render("collapsed-2x.png", width=900)),
        ("03", "EXPANDED", "Quota windows with reset countdowns, plus the 26-week token heatmap.", render("expanded-2x.png", width=1010)),
        ("04", "ALERT MINIMAL", "Below-threshold values become live low-quota capsules instead of the grip.", render("alert-minimal-3x.png", width=920)),
    ]
    header_height = CONTENT_TOP + 8
    row_gap = 38
    layout = []
    y = header_height
    for number, title, description, art in rows:
        label_height = 118
        row_height = max(art.height, label_height) + row_gap
        layout.append((number, title, description, art, y, row_height))
        y += row_height
    height = y + 78
    im = backdrop(1440, height)
    draw = ImageDraw.Draw(im)
    rail_x = 300
    draw.line((rail_x, header_height + 20, rail_x, height - 92), fill=(34, 52, 50), width=2)
    minimal_line_y = layout[0][4] + layout[0][5] // 2
    draw.line((rail_x + 42, minimal_line_y, 1360, minimal_line_y), fill=(30, 42, 44), width=2)
    for number, title, description, art, y, row_height in layout:
        paste_art(im, art, rail_x + 42, y + (row_height - row_gap - art.height) // 2, shadow=art.height > 30)
    draw_brand(im, "STATES")
    draw_stamp(im)
    draw_header(im, "Four states, one island.", "Minimal   →   Collapsed   →   Expanded   →   Alert Minimal")
    label_x = 56
    for number, title, description, art, y, row_height in layout:
        draw.text((label_x, y + 14), number, font=font(40, "light"), fill=ACCENT_DIM)
        draw.text((label_x, y + 68), title, font=font(16, "semibold"), fill=ACCENT)
        words = description.split(" ")
        lines, current = [], ""
        for word in words:
            probe = (current + " " + word).strip()
            if text_width(draw, probe, font(14)) <= 208:
                current = probe
            else:
                lines.append(current)
                current = word
        if current:
            lines.append(current)
        for index, line in enumerate(lines):
            draw.text((label_x, y + 94 + index * 20), line, font=font(14), fill=MUTED)
    draw_footer(im, "Actual production WPF views · offline renders, invented synthetic data")
    return save(im, "states.png")


def compose_settings():
    pages = [
        ("01", "GENERAL", "Desktop behavior and visible Harnesses", "settings-general.png"),
        ("02", "PROVIDERS", "Per-platform connection and read controls", "settings-providers.png"),
        ("03", "ALERTS", "Low-quota thresholds for Alert Minimal", "settings-alerts.png"),
        ("04", "ADVANCED", "Refresh intervals, optional paths and estimates", "settings-advanced.png"),
    ]
    cell_w = 690
    gap = 32
    margin = (1440 - (cell_w * 2 + gap)) // 2
    top = CONTENT_TOP + 18
    window = Image.open(SRC / pages[0][3]).convert("RGBA")
    cell_h = round(window.height * cell_w / window.width)
    label_h = 34
    row_h = cell_h + label_h + 30
    height = top + row_h * 2 + 74
    im = backdrop(1440, height)
    for index, (number, title, description, name) in enumerate(pages):
        column = index % 2
        row = index // 2
        x = margin + column * (cell_w + gap)
        y = top + row * row_h + label_h
        page = Image.open(SRC / name).convert("RGBA")
        page = page.resize((cell_w, cell_h), Image.Resampling.LANCZOS)
        paste_art(im, page, x, y, shadow=True)
    draw_brand(im, "SETTINGS")
    draw_stamp(im)
    draw_header(im, "Settings, without the clutter.", "Four real tabs from the production Settings window (offline render).")
    draw = ImageDraw.Draw(im)
    for index, (number, title, description, name) in enumerate(pages):
        column = index % 2
        row = index // 2
        x = margin + column * (cell_w + gap)
        y = top + row * row_h
        draw.text((x, y), f"{number}  {title}", font=font(15, "semibold"), fill=ACCENT)
        draw.text((x + text_width(draw, f"{number}  {title}", font(15, "semibold")) + 14, y + 3), description, font=font(13), fill=MUTED)
    draw_footer(im, "Actual production Settings window frames · isolated empty store, no credentials or network")
    return save(im, "settings.png")


def compose_layouts():
    cells = [
        ("01", "1 PROVIDER", "Codex only", "layout-1.png"),
        ("02", "2 PROVIDERS", "Codex + OpenCode Go", "layout-2.png"),
        ("03", "3 PROVIDERS", "Codex + DSH + Antigravity", "layout-3.png"),
        ("04", "4 PROVIDERS", "All Harnesses visible", "layout-4.png"),
    ]
    widest = render("layout-4.png")
    scale = 600 / widest.width
    rendered = []
    for number, title, description, name in cells:
        art = render(name)
        art = art.resize((round(art.width * scale), round(art.height * scale)), Image.Resampling.LANCZOS)
        rendered.append((number, title, description, art))
    cell_w = 640
    gap = 40
    margin = (1440 - (cell_w * 2 + gap)) // 2
    top = CONTENT_TOP + 24
    label_h = 40
    row_heights = [max(rendered[row * 2][3].height, rendered[row * 2 + 1][3].height) + label_h + 34 for row in range(2)]
    height = top + sum(row_heights) + 74
    im = backdrop(1440, height)
    y = top + label_h
    positions = []
    for row in range(2):
        for column in range(2):
            index = row * 2 + column
            number, title, description, art = rendered[index]
            x = margin + column * (cell_w + gap)
            paste_art(im, art, x + (cell_w - art.width) // 2, y + 8)
            positions.append((index, x, y))
        y += row_heights[row]
    draw_brand(im, "LAYOUTS")
    draw_stamp(im)
    draw_header(im, "One to four providers, one island.", "The instrument strip follows the Harnesses you choose to show.")
    draw = ImageDraw.Draw(im)
    for index, x, y in positions:
        number, title, description, art = rendered[index]
        draw.text((x, y - 34), f"{number}  {title}", font=font(15, "semibold"), fill=ACCENT)
        label_width = text_width(draw, f"{number}  {title}", font(15, "semibold"))
        draw.text((x + label_width + 14, y - 31), description, font=font(13), fill=MUTED)
    draw_footer(im, "Actual production WPF views · offline renders, invented synthetic data")
    return save(im, "layouts.png")


def heatmap_crop():
    data = json.loads((SRC / "geometry-expanded-2x.json").read_text(encoding="utf-8"))
    scale = data["Scale"]
    heading = data["HeatmapHeading"]
    legend = data["Legend"]
    left = 30
    top = heading["Y"] - 12
    right = 860
    bottom = legend["Y"] + legend["H"] + 8
    expanded = Image.open(SRC / "expanded-2x.png").convert("RGBA")
    crop = expanded.crop((round(left * scale), round(top * scale), round(right * scale), round(bottom * scale)))
    bbox = crop.getchannel("A").getbbox()
    return crop.crop(bbox)


def compose_heatmap():
    art = heatmap_crop()
    art = art.resize((1320, round(art.height * 1320 / art.width)), Image.Resampling.LANCZOS)
    top = CONTENT_TOP + 12
    height = top + art.height + 34 + 40 + 78
    im = backdrop(1440, height)
    paste_art(im, art, (im.width - art.width) // 2, top, shadow=False)
    draw_brand(im, "HEATMAP")
    draw_stamp(im)
    draw_header(im, "26 weeks of tokens, one square per day.", "Combined scale across visible Harnesses · hover a day for the per-provider split.")
    draw = ImageDraw.Draw(im)
    chips = ["One square = one day", "Intensity = combined daily total", "Provider totals in the legend"]
    x = 60
    for chip in chips:
        f = font(14, "semibold")
        width = text_width(draw, chip, f) + 28
        y = top + art.height + 34
        draw.rounded_rectangle((x, y, x + width, y + 34), radius=17, fill=(18, 24, 26), outline=(40, 55, 53), width=1)
        draw.text((x + 14, y + 9), chip, font=f, fill=(160, 186, 178))
        x += width + 14
    draw_footer(im, "Actual production WPF heatmap · offline render, invented synthetic data")
    return save(im, "heatmap.png")


def compose_tooltip():
    art = render("tooltip-2x.png", width=1320)
    top = CONTENT_TOP + 12
    height = top + art.height + 34 + 26 + 78
    im = backdrop(1440, height)
    paste_art(im, art, (im.width - art.width) // 2, top)
    draw_brand(im, "TOOLTIP")
    draw_stamp(im)
    draw_header(im, "Every day has a story.", "Hover any heatmap day for that date's per-provider token totals.")
    draw_caption(im, 60, top + art.height + 32, "The production view repositions the card so it never clips the island edge.")
    draw_footer(im, "Actual production WPF tooltip · offline render, invented synthetic data")
    return save(im, "tooltip.png")


def compose_opencode_go():
    art = render("opencode-go-2x.png", width=1180)
    top = CONTENT_TOP + 12
    height = top + art.height + 34 + 26 + 78
    im = backdrop(1440, height)
    paste_art(im, art, (im.width - art.width) // 2, top)
    draw_brand(im, "OPENCODE GO")
    draw_stamp(im)
    draw_header(im, "All three windows, one provider.", "OpenCode Go only: 5-hour, weekly and monthly quota with reset countdowns.")
    draw_caption(im, 60, top + art.height + 32, "Other Harnesses stay monitored; only visibility changes. Tokens use the provider's own history scale.")
    draw_footer(im, "Actual production WPF view · offline render, invented synthetic data")
    return save(im, "opencode-go.png")


def main():
    results = []
    results.append(compose_hero())
    results.append(compose_clean("expanded.png", "expanded-2x.png", "EXPANDED", "Four providers: quota windows, reset countdowns and the 26-week token heatmap.", 1320,
                                 "Actual production WPF view · offline render (no desktop capture)"))
    results.append(compose_clean("collapsed.png", "collapsed-2x.png", "COLLAPSED", "One capsule with each visible provider's limiting value. Click to expand.", 1120,
                                 "Actual production WPF view · offline render (no desktop capture)"))
    results.append(compose_minimal())
    results.append(compose_clean("alert-minimal.png", "alert-minimal-3x.png", "ALERT MINIMAL", "Only below-threshold platforms become capsules; no window opens by itself.", 1180,
                                 "Actual production WPF view · offline render (no desktop capture)"))
    results.append(compose_states())
    results.append(compose_settings())
    results.append(compose_layouts())
    results.append(compose_heatmap())
    results.append(compose_tooltip())
    results.append(compose_opencode_go())
    for name, size, bytes_ in results:
        print(f"{name:24s} {size[0]}x{size[1]:<5d} {bytes_/1024:8.1f} KiB")
    total = sum(item[2] for item in results)
    print(f"total stills: {total/1024:.1f} KiB")


if __name__ == "__main__":
    main()
