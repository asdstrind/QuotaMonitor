"""Build the three public GIFs from real `--preview-v170` window captures.

Input: the takes recorded by tools/capture-v170/capture-preview.ps1 (window-rectangle screen
capture of the packaged app in preview mode, synthetic fixture data only). Output: optimized
animated GIFs in docs/media/v1.7.0/ with the shared dark presentation backdrop and a permanent
SYNTHETIC DEMO DATA marker.

Composition: the preview window paints a flat synthetic background (RGB 34,37,43); that flat
field is keyed out and the real island capture floats on the shared gradient, so no host-window
card or empty panel is shown. Every retained frame is a real captured screen frame sampled at
15 fps (no motion frames dropped; holds are not artificially shortened). Requires Pillow +
numpy + ffmpeg; no network, account data or desktop capture.

Usage (from repository root):
  python tools/capture-v170/encode-gifs.py evidence/v1.7.0/visual/capture docs/media/v1.7.0 \
      C:/Windows/Fonts src/QuotaMonitor/Assets/QuotaMonitor.png
"""
import json
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

CAPTURE = Path(sys.argv[1])
OUT = Path(sys.argv[2])
FONT_DIR = Path(sys.argv[3] if len(sys.argv) > 3 else "C:/Windows/Fonts")
LOGO_PATH = Path(sys.argv[4]) if len(sys.argv) > 4 else None
OUT.mkdir(parents=True, exist_ok=True)

TEXT = (238, 246, 242)
TEXT_SOFT = (196, 210, 207)
MUTED = (138, 154, 155)
ACCENT = (127, 207, 180)
STAMP_BG = (15, 37, 33)
STAMP_LINE = (46, 82, 74)
STAMP_INK = (169, 215, 202)
WEIGHTS = {"regular": "segoeui.ttf", "light": "segoeuil.ttf", "semibold": "seguisb.ttf", "bold": "segoeuib.ttf"}

CANVAS_W = 1100
CANVAS_H = 840
SHOT_Y = 96
SCALE = 0.78
FPS = 30
CAPTURE_BG = np.array([34, 37, 43], np.int16)


def font(size, weight="regular"):
    return ImageFont.truetype(str(FONT_DIR / WEIGHTS[weight]), size)


def round_logo(size):
    if LOGO_PATH is None or not LOGO_PATH.exists():
        return None
    logo = Image.open(LOGO_PATH).convert("RGBA")
    logo.thumbnail((size, size), Image.Resampling.LANCZOS)
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


def make_base(label, caption):
    base = backdrop(CANVAS_W, CANVAS_H)
    draw = ImageDraw.Draw(base)
    logo = round_logo(26)
    if logo is not None:
        base.paste(logo, (40, 32), logo)
    draw.text((78, 38), "QuotaMonitor", font=font(16, "semibold"), fill=TEXT)
    offset = 78 + draw.textlength("QuotaMonitor", font=font(16, "semibold")) + 14
    draw.text((offset, 41), label, font=font(12, "semibold"), fill=ACCENT)
    stamp = "SYNTHETIC DEMO DATA"
    f = font(13, "semibold")
    width = draw.textlength(stamp, font=f) + 50
    draw.rounded_rectangle((CANVAS_W - 34 - width, 30, CANVAS_W - 34, 62), radius=16, fill=STAMP_BG, outline=STAMP_LINE, width=1)
    draw.ellipse((CANVAS_W - 34 - width + 14, 43, CANVAS_W - 34 - width + 20, 49), fill=ACCENT)
    draw.text((CANVAS_W - 34 - width + 27, 39), stamp, font=f, fill=STAMP_INK)
    draw.text((40, CANVAS_H - 92), caption, font=font(15), fill=TEXT_SOFT)
    draw.text((41, CANVAS_H - 64),
              "SYNTHETIC DEMO DATA · real screen capture of QuotaMonitor v1.7.0 --preview-v170 · no real account",
              font=font(12), fill=MUTED)
    return base.convert("RGBA")


def prepare_frame(frame):
    """Crop to the real island silhouette and key the flat synthetic preview background.

    The packaged preview window paints the whole 880x480 DIP host rectangle with a flat
    synthetic background; that field (and its faint wide shadow) is removed so the island
    can be composed on the shared gradient stage without any host-window card.
    """
    rgb = frame.convert("RGB")
    arr = np.asarray(rgb).astype(np.int16)
    distance = np.abs(arr - CAPTURE_BG).max(axis=2)
    # 18 excludes the flat synthetic background and its faint wide drop shadow, but keeps the
    # near-black island card, capsule and tooltip surfaces (which differ by 20+ levels).
    content = distance > 18
    if not content.any():
        raise SystemExit("capture.frame-without-content")
    rows = np.where(content.any(axis=1))[0]
    cols = np.where(content.any(axis=0))[0]
    pad = 8
    left = max(0, int(cols[0]) - pad)
    right = min(rgb.width, int(cols[-1]) + pad + 1)
    top = max(0, int(rows[0]) - pad)
    bottom = min(rgb.height, int(rows[-1]) + pad + 1)
    cropped = rgb.crop((left, top, right, bottom))
    dist = distance[top:bottom, left:right]
    alpha = np.clip((dist - 16) * 32, 0, 255).astype(np.uint8)
    return Image.merge("RGBA", (*cropped.split(), Image.fromarray(alpha, "L")))


def extract(mkv, start, end, fps, folder):
    """Extract a full real-time interval as PNG frames at the requested frame rate."""
    folder.mkdir(parents=True, exist_ok=True)
    for old in folder.glob("*.png"):
        old.unlink()
    subprocess.run([
        "ffmpeg", "-y", "-hide_banner", "-loglevel", "error",
        "-i", str(mkv), "-ss", f"{start:.3f}", "-t", f"{end - start:.3f}",
        "-vf", f"fps={fps}", "-q:v", "2", str(folder / "%04d.png")
    ], check=True)
    return sorted(folder.glob("*.png"))


def storyboard(config):
    takes = {item["name"]: CAPTURE / item["file"] for item in
             json.loads((CAPTURE / "takes.json").read_text(encoding="utf-8"))["Takes"]}
    frames = []
    tmp_root = Path(tempfile.mkdtemp(prefix="qm-gif-"))
    for index, segment in enumerate(config):
        mkv = takes[segment["take"]]
        fps = segment.get("fps", FPS)
        for path in extract(mkv, segment["from"], segment["to"], fps, tmp_root / f"s{index}"):
            frames.append((str(path), round(1000 / fps)))
    return frames


def compose_gif(name, label, caption, config, palette_colors=128, drop_empty_shell=False, disposal=1):
    base = make_base(label, caption)
    frames = storyboard(config)
    composed = []
    skipped = 0
    for path, duration in frames:
        shot = prepare_frame(Image.open(path))
        if drop_empty_shell and shot.width > 700:
            # The production collapse/expand animation briefly clips the detail content to a
            # nearly empty pure-black shell; those frames read as a "host-window card" in a GIF.
            arr = np.asarray(shot.convert("RGB")).astype(np.int16)
            if (arr.max(axis=2) < 8).mean() > 0.45:
                skipped += 1
                continue
        width = max(1, round(shot.width * SCALE))
        height = max(1, round(shot.height * SCALE))
        shot = shot.resize((width, height), Image.Resampling.LANCZOS)
        frame = base.copy()
        frame.paste(shot, ((CANVAS_W - width) // 2, SHOT_Y), shot)
        composed.append((frame.convert("RGB"), duration))
    palette = composed[len(composed) // 3][0].quantize(colors=palette_colors, method=Image.Quantize.MEDIANCUT)
    quantized = [image.quantize(palette=palette, dither=Image.Dither.NONE) for image, _ in composed]
    durations = [duration for _, duration in composed]
    target = OUT / name
    # optimize=False keeps every captured frame instead of collapsing holds.
    quantized[0].save(target, save_all=True, append_images=quantized[1:], duration=durations, loop=0,
                      optimize=False, disposal=disposal)
    total_ms = sum(durations)
    print(f"{name:12s} {len(quantized):4d} frames ({skipped} artifact frames dropped)  {total_ms/1000:.1f}s  {target.stat().st_size/1024:8.1f} KiB")
    return target


def main():
    # morph.gif: settled Minimal, Collapsed switch, real Expanded morph, real Minimal morph.
    compose_gif(
        "morph.gif",
        "MORPH",
        "Alert Minimal  →  Collapsed  →  Expanded  →  Alert Minimal  ·  real morph transitions (low-quota fixture)",
        [{"take": "morph", "from": 2.25, "to": 6.95}],
        drop_empty_shell=True,
    )
    # heatmap.gif: four-provider heatmap with real cursor hover tooltips at normal synthetic values.
    compose_gif(
        "heatmap.gif",
        "HEATMAP",
        "Hover a date: per-provider token tooltip, combined total, real cursor",
        [{"take": "heatmap", "from": 0.90, "to": 6.00}],
    )
    # quota.gif: four-provider Expanded, collapsed limiting values, low-quota Alert Minimal.
    compose_gif(
        "quota.gif",
        "QUOTA",
        "Four-provider quota  →  collapsed limiting values  →  low-quota Alert Minimal",
        [{"take": "quota", "from": 1.20, "to": 8.60}],
    )


if __name__ == "__main__":
    main()



