#!/usr/bin/env python3
"""فحص اتساق وثائق التصميم.

يتحقق من:
  1. كل معرّف مُشار إليه (BO, BR, FR, NFR, ADR, R, ASM, Q, TS) معرّف في وثيقته.
  2. كل متطلب وظيفي مغطى بسيناريو اختبار (12) وبمرحلة تنفيذ (13).
  3. كل مفتاح إعداد أو صلاحية أو جدول مذكور بين علامتي ` معرّف في 07 أو 06 أو 05.
  4. كل إحالة بصيغة "NN §x.y" تشير إلى قسم موجود.
  5. كل انتقال مسمى في مخططات الحالة (08) له نقطة نهاية في عقد API (11)، أو موسوم بأنه آلي.

الاستخدام: python3 tools/check_docs.py   (رمز الخروج 1 عند وجود أخطاء)
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"
FILES = {p.name[:2]: p for p in sorted(DOCS.glob("*.md"))}
ALL_TEXT = {p: p.read_text(encoding="utf-8") for p in [ROOT / "README.md", *FILES.values()]}

errors: list[str] = []


def doc(prefix: str) -> str:
    return ALL_TEXT[FILES[prefix]]


def err(msg: str) -> None:
    errors.append(msg)


# ---------- 1. التعريفات ----------
def row_ids(text: str, pattern: str) -> set[str]:
    return set(re.findall(r"^\|\s*(" + pattern + r")\s*\|", text, re.M))


defined = {
    "BO": row_ids(doc("01"), r"BO-\d+"),
    "BR": row_ids(doc("01"), r"BR-\d+"),
    "FR": row_ids(doc("02"), r"FR-[A-Z]+-\d+"),
    "NFR": row_ids(doc("02"), r"NFR-[A-Z]+-\d+"),
    "ADR": set(re.findall(r"^## (ADR-\d{3})", doc("15"), re.M)),
    "R": row_ids(doc("14"), r"R-\d+"),
    "ASM": row_ids(doc("16"), r"ASM-\d+"),
    "Q": row_ids(doc("16"), r"Q-\d+"),
    "TS": row_ids(doc("12"), r"TS-\d+"),
}
FR_MODULES = {fr.split("-")[1] for fr in defined["FR"]}
NFR_CATS = {n.split("-")[1] for n in defined["NFR"]}

ID_RE = re.compile(r"\b(BO-\d+|BR-\d+|FR-[A-Z]+-(?:\d+(?:\.\.\d+)?|\*)|NFR-[A-Z]+-(?:\d+(?:\.\.\d+)?|\*)|ADR-\d{3}|R-\d{2}|ASM-\d+|Q-\d+|TS-\d+)")


def expand(ref: str) -> list[str]:
    m = re.fullmatch(r"(FR|NFR)-([A-Z]+)-(\d+)\.\.(\d+)", ref)
    if m:
        kind, mod, a, b = m.groups()
        return [f"{kind}-{mod}-{i:02d}" for i in range(int(a), int(b) + 1)]
    return [ref]


def kind_of(ref: str) -> str:
    return ref.split("-")[0]


for path, text in ALL_TEXT.items():
    for ref in ID_RE.findall(text):
        if ref.endswith("*"):
            kind, mod = ref.split("-")[:2]
            pool = FR_MODULES if kind == "FR" else NFR_CATS
            if mod not in pool:
                err(f"{path.name}: {ref} يشير إلى وحدة غير معرّفة")
            continue
        for one in expand(ref):
            if one not in defined[kind_of(one)]:
                err(f"{path.name}: {one} غير معرّف")

# ---------- 2. التغطية ----------
def covered_in(text: str) -> set[str]:
    out: set[str] = set()
    for ref in ID_RE.findall(text):
        if ref.startswith("FR-") and ref.endswith("*"):
            mod = ref.split("-")[1]
            out |= {f for f in defined["FR"] if f.split("-")[1] == mod}
        elif ref.startswith("FR-"):
            out |= set(expand(ref))
    return out


tested = covered_in(doc("12"))
planned = covered_in(doc("13"))
for fr in sorted(defined["FR"]):
    if fr not in tested:
        err(f"12-test-plan: {fr} بلا سيناريو اختبار")
    if fr not in planned:
        err(f"13-roadmap: {fr} بلا مرحلة تنفيذ")

# ---------- 3. المفاتيح والصلاحيات والجداول ----------
flags = set(re.findall(r"^\|\s*`([a-z_]+\.[a-z_]+)`\s*\|\s*(?:module|capability|connector)\s*\|", doc("07"), re.M))
settings: set[str] = set()
for line in re.findall(r"^\|\s*(`[^|]+)\|", doc("07"), re.M):
    settings |= set(re.findall(r"`([a-z_]+\.[a-z_]+)`", line))
settings -= flags
permissions: set[str] = set()
for line in re.findall(r"^\|\s*(`[a-z_]+\.[a-z_]+`[^|]*)\|", doc("06"), re.M):
    permissions |= set(re.findall(r"`([a-z_]+\.[a-z_]+)`", line))
tables: set[str] = set()
for heading in re.findall(r"^#### (.+)$", doc("05"), re.M):
    tables |= set(re.findall(r"\b([a-z]+\.[a-z_]+)\b", heading))
views = set(re.findall(r"`(reporting\.v_[a-z_]+)`", doc("05")))
SCHEMAS = {"iam", "org", "bodies", "meetings", "deliberation", "minutes", "resolutions", "tasks",
           "docs", "notify", "config", "search", "audit", "sig", "ai", "integ", "reporting"}
FLAG_PREFIXES = {"module", "esign", "ai", "integrations", "voting", "meetings", "tasks", "documents",
                 "search", "ui", "notify", "bodies", "session", "calendar", "numbering"}
PERM_PREFIXES = {"bodies", "meetings", "agenda", "attendance", "quorum", "conflict", "deliberation",
                 "motions", "votes", "circulations", "minutes", "resolutions", "tasks", "reports",
                 "search", "admin", "audit", "ai", "esign"}
# أسماء أحداث التدقيق ومعرّفات أخرى ليست مفاتيح ولا صلاحيات ولا جداول
ALLOWED_OTHER = {"config.flag_changed", "minutes.approved"}

known = flags | settings | permissions | tables | views | ALLOWED_OTHER
for path, text in ALL_TEXT.items():
    for tok in set(re.findall(r"`([a-z_]+\.[a-z_*]+)`", text)):
        prefix = tok.split(".")[0]
        if tok.endswith("*"):
            base = tok[:-1]
            if not any(k.startswith(base) for k in known):
                err(f"{path.name}: `{tok}` لا يطابق أي مفتاح/صلاحية")
            continue
        if prefix in SCHEMAS | FLAG_PREFIXES | PERM_PREFIXES and tok not in known:
            err(f"{path.name}: `{tok}` غير معرّف (مفتاح/إعداد/صلاحية/جدول)")

# ---------- 4. إحالات الأقسام ----------
def sections(text: str) -> set[str]:
    out = set()
    for h in re.findall(r"^#{2,4} (\d+(?:\.\d+)*)\.?\s", text, re.M):
        out.add(h)
    return out


SECTION_RE = re.compile(r"(?<!\d)(0\d|1\d) §(\d+(?:\.\d+)*)")
for path, text in ALL_TEXT.items():
    for num, sec in SECTION_RE.findall(text):
        if num not in FILES:
            err(f"{path.name}: إحالة إلى وثيقة غير موجودة {num}")
        elif sec not in sections(ALL_TEXT[FILES[num]]):
            err(f"{path.name}: إحالة {num} §{sec} إلى قسم غير موجود")

# ---------- 5. الانتقالات ↔ الواجهات ----------
api = doc("11")
for block in re.findall(r"```mermaid\nstateDiagram-v2\n(.*?)```", doc("08"), re.S):
    for label in re.findall(r"-->\s*\w+\s*:\s*(.+)", block):
        for verb in re.findall(r"\b([a-z]+(?:-[a-z]+)*)\b", label):
            if verb in {"request"}:
                continue
            if f"/{verb}" not in api and f"{verb}`" not in api:
                err(f"08: الانتقال «{verb}» بلا نقطة نهاية في 11")

# ---------- 6. المفاتيح المذكورة في 07 تُستخدم والعكس ----------
for f in sorted(flags):
    uses = sum(text.count(f"`{f}`") for p, text in ALL_TEXT.items() if p.name[:2] != "07")
    if uses == 0 and not f.startswith(("ui.", "tasks.", "documents.watermark", "search.")):
        err(f"07: المفتاح {f} غير مستخدم في أي وثيقة أخرى")

# ---------- النتيجة ----------
summary = ", ".join(f"{k}={len(v)}" for k, v in defined.items())
print(f"معرّفات: {summary}; مفاتيح={len(flags)}, إعدادات={len(settings)}, صلاحيات={len(permissions)}, جداول={len(tables)}")
if errors:
    print(f"\n{len(errors)} مشكلة:")
    for e in sorted(set(errors)):
        print(" -", e)
    sys.exit(1)
print("الوثائق متسقة.")
