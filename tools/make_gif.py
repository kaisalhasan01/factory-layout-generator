"""Stitch captured frames into an animated GIF and/or WebP for the README and the portfolio.

    python tools/make_gif.py logs/shots/demo_frames docs/images/demo_workflow.gif --width 960
    python tools/make_gif.py logs/shots/demo_frames docs/images/blueprint_to_3d.gif --start 185 --crop-panel

Frames come from `FactoryLayout.exe -frames <dir>` (the blueprint-to-3D transition) or
`-frames <dir> -demo` (the whole workflow); see tools/build_and_shoot.ps1.
An output ending in .webp is written as an animated WebP (much smaller than a GIF).
"""
import argparse
import glob
import os

from PIL import Image


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("frames")
    ap.add_argument("out", nargs="+", help="one or more outputs, .gif or .webp")
    ap.add_argument("--width", type=int, default=960)
    ap.add_argument("--fps", type=float, default=15)
    ap.add_argument("--start", type=int, default=0, help="first frame index to use")
    ap.add_argument("--end", type=int, default=None, help="last frame index (exclusive)")
    ap.add_argument("--crop-panel", action="store_true", help="cut the side panel and status bar off")
    ap.add_argument("--hold", type=float, default=1.2, help="seconds to hold the first and last frame")
    ap.add_argument("--colors", type=int, default=128, help="GIF palette size")
    ap.add_argument("--quality", type=int, default=80, help="WebP quality")
    ap.add_argument("--stills", type=str, default="", help="comma-separated frame indices to also save as PNG")
    a = ap.parse_args()

    files = sorted(glob.glob(os.path.join(a.frames, "frame_*.png")))[a.start:a.end]
    if not files:
        raise SystemExit(f"no frames in {a.frames}")
    frames = []
    for f in files:
        im = Image.open(f).convert("RGB")
        if a.crop_panel:
            k = im.width / 1600
            im = im.crop((int(395 * k), 0, im.width, int(866 * k)))
        h = round(im.height * a.width / im.width)
        frames.append(im.resize((a.width, h), Image.LANCZOS))

    for idx in filter(None, a.stills.split(",")):
        i = min(int(idx), len(frames) - 1)
        frames[i].save(os.path.splitext(a.out[0])[0] + f"_{i:03d}.png")

    ms = round(1000 / a.fps)
    durations = [ms] * len(frames)
    durations[0] = durations[-1] = int(a.hold * 1000)
    for out in a.out:
        if out.lower().endswith(".webp"):
            frames[0].save(out, save_all=True, append_images=frames[1:], duration=durations, loop=0,
                           quality=a.quality, method=4)
        else:
            pal = [f.quantize(colors=a.colors, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE) for f in frames]
            pal[0].save(out, save_all=True, append_images=pal[1:], duration=durations, loop=0, optimize=True)
        print(f"{len(frames)} frames -> {out} ({os.path.getsize(out) / 1e6:.1f} MB)")


if __name__ == "__main__":
    main()
