#!/usr/bin/env python3
"""检查界面文案的英文翻译是否齐全（CI 与本地都可运行）。

  python tools/check_i18n.py            # 有缺失 / 未包裹的中文 / 多余条目时退出码为 1
  python tools/check_i18n.py --missing  # 以 JSON 输出缺失的 key，方便补译

规则：
  * XAML 中 {l:T '原文'}、C# 中 L.T("原文") / L.F("原文", ...) / L.N("原文") 都是 key；
  * Localization/en.json 必须覆盖全部 key，且不能有已经不用的条目；
  * 界面代码（Testing/ 自检代码与注释除外）里不能残留没包 L.T 的中文字面量；
  * L.F 的译文必须和原文使用同样的 {n} 占位符。
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "src" / "RdpHelper"
EN = ROOT / "Localization" / "en.json"
CJK = re.compile(r"[一-鿿]")

XAML_KEY = re.compile(r"\{l:T '((?:[^'\\]|\\.)*)'\}")
CS_KEY = re.compile(r'\bL\.([TFN])\("((?:[^"\\\n]|\\.)*)"')
CS_STR = re.compile(r'(?<![\w])(\$@|@\$|\$|@)?"((?:[^"\\\n]|\\.)*)"')
XAML_ATTR = re.compile(r'(\w+)="([^"]*)"')
PLACEHOLDER = re.compile(r"\{(\d+)(?:[:,][^}]*)?\}")


def cs_unescape(s: str) -> str:
    return json.loads('"' + s.replace("\\'", "'") + '"')


def xaml_unescape(s: str) -> str:
    return re.sub(r"\\(.)", r"\1", s)


def strip_cs_comments(line: str) -> str:
    st = line.lstrip()
    if st.startswith("//"):
        return ""
    # 行尾 // 注释（不在字符串内的粗略判断：注释前引号成对）
    idx = line.find(" // ")
    if idx >= 0 and line[:idx].count('"') % 2 == 0:
        return line[:idx]
    return line


def main() -> int:
    keys: dict[str, str] = {}  # key -> 首次出现位置
    fmt_keys: set[str] = set()
    problems: list[str] = []

    for f in sorted(ROOT.rglob("*.xaml")):
        text = re.sub(r"<!--.*?-->", "", f.read_text(encoding="utf-8"), flags=re.S)
        for m in XAML_KEY.finditer(text):
            keys.setdefault(xaml_unescape(m.group(1)), f.name)
        for m in XAML_ATTR.finditer(text):
            if CJK.search(m.group(2)) and not m.group(2).startswith("{l:T ") and m.group(1) not in ("Tag",) and m.group(2) != "简体中文":
                problems.append(f"{f.name}: 未本地化的 XAML 文案 {m.group(0)}")

    for f in sorted(ROOT.rglob("*.cs")):
        rel = f.relative_to(ROOT).as_posix()
        if rel.startswith(("Testing/", "obj/", "bin/")):
            continue
        in_verbatim = False
        for no, raw in enumerate(f.read_text(encoding="utf-8").splitlines(), 1):
            line = strip_cs_comments(raw)
            for m in CS_KEY.finditer(line):
                k = cs_unescape(m.group(2))
                keys.setdefault(k, f"{rel}:{no}")
                if m.group(1) == "F":
                    fmt_keys.add(k)
            # PowerShell 脚本（逐字字符串）里的中文由 Shell.Warnings 按 L.N 前缀翻译，跳过
            if in_verbatim or '@"' in line or "'WARN:" in line:
                if line.count('"') % 2 == 1 and '@"' in line:
                    in_verbatim = not in_verbatim
                elif in_verbatim and line.count('"') % 2 == 1:
                    in_verbatim = False
                continue
            wrapped = {m.span(2) for m in CS_KEY.finditer(line)}
            for m in CS_STR.finditer(line):
                if CJK.search(m.group(2)):
                    inner = (m.start(2), m.end(2))
                    if inner not in wrapped:
                        problems.append(f"{rel}:{no}: 未包 L.T 的中文 {m.group(0)}")

    en = json.loads(EN.read_text(encoding="utf-8"))
    missing = {k: "" for k in keys if k not in en}
    unused = [k for k in en if k not in keys]
    for k in keys:
        if k in en:
            if not en[k].strip():
                problems.append(f"空译文：{k!r}")
            a = sorted(PLACEHOLDER.findall(k))
            b = sorted(PLACEHOLDER.findall(en[k]))
            if k in fmt_keys and a != b:
                problems.append(f"占位符不一致：{k!r} -> {en[k]!r}")
            if CJK.search(en[k]):
                problems.append(f"译文里还有中文：{k!r} -> {en[k]!r}")

    if "--missing" in sys.argv:
        print(json.dumps(missing, ensure_ascii=False, indent=2))
        return 0

    for k in missing:
        problems.append(f"缺少英文：{k!r}（{keys[k]}）")
    for k in unused:
        problems.append(f"多余的英文条目：{k!r}")

    print(f"keys={len(keys)} translated={len(keys) - len(missing)} problems={len(problems)}")
    for p in problems:
        print("  " + p)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
