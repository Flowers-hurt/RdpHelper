#!/usr/bin/env python3
"""生成 GitHub Pages 官网：把 site/ 拷到 _site/，并把 docs/screenshots 下的 2x PNG 截图
压缩成网页用的 JPEG（宽 1600），README 和官网用同一批 CI 真机截图，不用手动维护两份。

  python tools/build_site.py          # 输出到 _site/
  python -m http.server -d _site      # 本地预览
"""
import shutil
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "site"
SHOTS = ROOT / "docs" / "screenshots"
OUT = ROOT / "_site"
PAGES = ["remote", "ssh", "access", "tunnel", "accounts", "settings", "about"]


def main() -> None:
    if OUT.exists():
        shutil.rmtree(OUT)
    shutil.copytree(SRC, OUT)
    n = 0
    for lang, folder in (("zh", SHOTS), ("en", SHOTS / "en")):
        dst = OUT / "shots" / lang
        dst.mkdir(parents=True, exist_ok=True)
        for page in PAGES:
            for theme in ("light", "dark"):
                f = folder / f"{page}-{theme}.png"
                if not f.exists():
                    continue
                im = Image.open(f).convert("RGB")
                if im.width > 1600:
                    im = im.resize((1600, round(im.height * 1600 / im.width)), Image.LANCZOS)
                im.save(dst / f"{page}-{theme}.jpg", quality=84, optimize=True, progressive=True)
                n += 1
    (OUT / ".nojekyll").write_text("")
    print(f"site -> {OUT} ({n} screenshots)")


if __name__ == "__main__":
    main()
