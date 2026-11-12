"""Validate and document the final v1.7.0 public media set.

Writes evidence/v1.7.0/visual/asset-manifest.json (dimensions, bytes, SHA256, capture method,
GIF frame counts/durations) for docs/media/v1.7.0/ and checks the public constraints
(PNG sizes, GIF sizes, no PNG text/EXIF chunks, every asset has no alpha in public PNGs).
No network, account data or product code is touched.

Usage (from repository root):
  python tools/capture-v170/finalize-media.py docs/media/v1.7.0 evidence/v1.7.0/visual
"""
import hashlib
import json
import struct
import sys
from datetime import datetime, timezone
from pathlib import Path

from PIL import Image

MEDIA = Path(sys.argv[1])
EVIDENCE = Path(sys.argv[2])
MANIFEST = EVIDENCE / "asset-manifest.json"

PNG_ASSETS = [
    ("hero.png", "Presentation hero: actual four-provider Expanded UI on the shared dark stage."),
    ("expanded.png", "Expanded island with four providers, quota windows and the 26-week heatmap."),
    ("collapsed.png", "Collapsed capsule with each visible provider's limiting value."),
    ("minimal.png", "Minimal 8 px resting strip at the app's 150% raster scale."),
    ("alert-minimal.png", "Alert Minimal low-quota capsules from the production alert evaluator."),
    ("states.png", "Minimal / Collapsed / Expanded / Alert Minimal presentation."),
    ("opencode-go.png", "OpenCode Go only: 5-hour, weekly and monthly windows with resets."),
    ("heatmap.png", "26-week token heatmap close-up with legend and provider totals."),
    ("tooltip.png", "Real date tooltip with per-provider token totals, unclipped."),
    ("settings.png", "Four real Settings tabs: General, Providers, Alerts, Advanced."),
    ("layouts.png", "One, two, three and four visible-provider Expanded layouts."),
]
GIF_ASSETS = [
    ("heatmap.gif", "Real window capture: hover tooltips on the four-provider 26-week heatmap."),
    ("quota.gif", "Real window capture: four-provider quota, collapsed limiting values, Alert Minimal."),
]
PNG_METHOD = ("Offline RenderTargetBitmap of unchanged production WPF views via tools/QuotaMonitor.Showcase; "
              "no adapters, network, account, credential or production-store access; composed by tools/compose-showcase.py. "
              "Not a screen recording.")
GIF_METHOD = ("Real screen capture of the packaged QuotaMonitor 1.7.0 --preview-v170 window (synthetic preview fixture only) "
              "by tools/capture-v170/capture-preview.ps1; the preview window's flat synthetic background is keyed out and the "
              "captured island is composed on the shared stage by tools/capture-v170/encode-gifs.py.")


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def png_chunks(path):
    chunks = []
    with path.open("rb") as handle:
        handle.read(8)
        while True:
            header = handle.read(8)
            if len(header) < 8:
                break
            length, kind = struct.unpack(">I4s", header)
            chunks.append(kind.decode("latin-1"))
            handle.seek(length + 4, 1)
    return chunks


def main():
    generated = datetime.now(timezone.utc).replace(microsecond=0).isoformat()
    assets = []
    problems = []
    for name, description in PNG_ASSETS:
        path = MEDIA / name
        if not path.exists():
            problems.append(f"missing {name}")
            continue
        with Image.open(path) as image:
            size = image.size
            mode = image.mode
        chunks = png_chunks(path)
        forbidden = [chunk for chunk in chunks if chunk.lower() in ("exif", "tEXt", "zTXt", "iTXt")]
        if forbidden:
            problems.append(f"{name}: unexpected metadata chunks {forbidden}")
        if mode != "RGB":
            problems.append(f"{name}: expected opaque RGB, got {mode}")
        payload = path.read_bytes()
        assets.append({
            "file": f"docs/media/v1.7.0/{name}",
            "description": description,
            "method": PNG_METHOD,
            "dimensions": f"{size[0]}x{size[1]}",
            "bytes": len(payload),
            "sha256": sha256(path),
            "syntheticDemoData": True,
        })
    for name, description in GIF_ASSETS:
        path = MEDIA / name
        if not path.exists():
            problems.append(f"missing {name}")
            continue
        with Image.open(path) as image:
            size = image.size
            frames = getattr(image, "n_frames", 1)
            durations = []
            for index in range(frames):
                image.seek(index)
                durations.append(image.info.get("duration", 0))
        payload = path.read_bytes()
        assets.append({
            "file": f"docs/media/v1.7.0/{name}",
            "description": description,
            "method": GIF_METHOD,
            "dimensions": f"{size[0]}x{size[1]}",
            "bytes": len(payload),
            "sha256": sha256(path),
            "syntheticDemoData": True,
            "frames": frames,
            "durationSeconds": round(sum(durations) / 1000.0, 2),
            "loop": True,
        })
    too_big = [asset["file"] for asset in assets if asset["file"].endswith(".png") and asset["bytes"] > 800 * 1024]
    if too_big:
        problems.append(f"PNG size budget exceeded: {too_big}")
    gif_big = [asset["file"] for asset in assets if asset["file"].endswith(".gif") and asset["bytes"] > 8 * 1024 * 1024]
    if gif_big:
        problems.append(f"GIF size budget exceeded: {gif_big}")
    manifest = {
        "product": "QuotaMonitor",
        "version": "1.7.0",
        "generatedUtc": generated,
        "syntheticDemoData": True,
        "statement": ("Every public image contains invented synthetic values only. No real account balances, API keys, "
                      "account IDs, emails, private paths or personal usage are shown. Static PNGs are offline renders of the "
                      "unchanged production WPF views; the GIFs are real screen captures of the packaged 1.7.0 preview window "
                      "driven by the app's synthetic fixture. Both contain a permanent SYNTHETIC DEMO DATA marker."),
        "provenance": "docs/media/PROVENANCE.md",
        "tools": [
            "tools/QuotaMonitor.Showcase",
            "tools/compose-showcase.py",
            "tools/capture-v170/capture-preview.ps1",
            "tools/capture-v170/encode-gifs.py",
            "tools/capture-v170/finalize-media.py",
        ],
        "sources": {
            "showcaseRenders": "evidence/v1.7.0/visual/showcase",
            "previewCapture": "evidence/v1.7.0/visual/capture",
            "previewExecutable": "publish/v1.7.0-stage2/win-x64/QuotaMonitor.exe",
            "previewExecutableSha256": "aaf02b1fb514b7d0a2b675d12ea75823ef1135b17cb9f389e5a735058203fc0f",
        },
        "counts": {"total": len(assets), "png": len(PNG_ASSETS), "gif": len(GIF_ASSETS)},
        "assets": assets,
        "dropped": [
            {"file": "docs/media/v1.7.0/morph.gif",
             "reason": "Not shipped: transient collapse/expand frames in the real capture contain an empty black shell that "
                       "reads as a host-window artifact in a public GIF. states.png covers the four island states instead."}
        ],
        "problems": problems,
    }
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    MANIFEST.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {MANIFEST}")
    for asset in assets:
        extra = ""
        if "frames" in asset:
            extra = f"  {asset['frames']} frames / {asset['durationSeconds']}s"
        print(f"{Path(asset['file']).name:20s} {asset['dimensions']:10s} {asset['bytes']/1024:8.1f} KiB  {asset['sha256'][:16]}{extra}")
    if problems:
        print("PROBLEMS:")
        for problem in problems:
            print(" -", problem)
        raise SystemExit(1)
    print("all public-media checks passed")


if __name__ == "__main__":
    main()
