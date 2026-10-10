#!/usr/bin/env python3
"""Turn the PDFium GN component into one shared library with exported FPDF_* symbols.

bblanchon/pdfium-binaries does the same edit in patches/shared_library.patch.
This script targets the chromium/8066 copies of BUILD.gn and public/fpdfview.h
and is safe to run twice. The Windows resources.rc hunk of that patch is not
applied; this recipe is linux-x64 only.
"""

from __future__ import annotations

import pathlib
import sys

COMPONENT = 'component("pdfium")'
SHARED = 'shared_library("pdfium")'

# public/fpdfview.h at chromium/8066. COMPONENT_BUILD is off in this recipe
# (is_component_build=false), so the #else branch would define FPDF_EXPORT as
# empty and nm -D would not show FPDF_RenderPageSkia.
STOCK_EXPORT = """\
#if defined(COMPONENT_BUILD)
// FPDF_EXPORT should be consistent with |export| in the pdfium_fuzzer
// template in testing/fuzzers/BUILD.gn.
#if defined(WIN32)
#if defined(FPDF_IMPLEMENTATION)
#define FPDF_EXPORT __declspec(dllexport)
#else
#define FPDF_EXPORT __declspec(dllimport)
#endif  // defined(FPDF_IMPLEMENTATION)
#else
#if defined(FPDF_IMPLEMENTATION)
#define FPDF_EXPORT __attribute__((visibility("default")))
#else
#define FPDF_EXPORT
#endif  // defined(FPDF_IMPLEMENTATION)
#endif  // defined(WIN32)
#else
#define FPDF_EXPORT
#endif  // defined(COMPONENT_BUILD)
"""

SHARED_EXPORT = """\
// Shared libpdfium is built with is_component_build=false, so COMPONENT_BUILD
// is unset. Keep the default-visibility export anyway.
#if defined(WIN32)
#if defined(FPDF_IMPLEMENTATION)
#define FPDF_EXPORT __declspec(dllexport)
#else
#define FPDF_EXPORT __declspec(dllimport)
#endif  // defined(FPDF_IMPLEMENTATION)
#else
#if defined(FPDF_IMPLEMENTATION)
#define FPDF_EXPORT __attribute__((visibility("default")))
#else
#define FPDF_EXPORT
#endif  // defined(FPDF_IMPLEMENTATION)
#endif  // defined(WIN32)
"""


def rewrite_build_gn(text: str) -> str:
    count = text.count(COMPONENT)
    if count == 1:
        return text.replace(COMPONENT, SHARED, 1)
    if count == 0 and SHARED in text:
        return text
    raise SystemExit(
        f'expected exactly one {COMPONENT!r} in BUILD.gn, found {count}'
    )


def rewrite_fpdfview(text: str) -> str:
    if STOCK_EXPORT in text:
        return text.replace(STOCK_EXPORT, SHARED_EXPORT, 1)
    if SHARED_EXPORT in text:
        return text
    raise SystemExit(
        "public/fpdfview.h does not match the chromium/8066 FPDF_EXPORT block. "
        "Refusing to guess."
    )


def rewrite_tree(pdfium: pathlib.Path) -> None:
    build_gn = pdfium / "BUILD.gn"
    header = pdfium / "public" / "fpdfview.h"
    for path in (build_gn, header):
        if not path.is_file():
            raise SystemExit(f"missing {path}")
    build_gn.write_text(rewrite_build_gn(build_gn.read_text(encoding="utf-8")), encoding="utf-8")
    header.write_text(rewrite_fpdfview(header.read_text(encoding="utf-8")), encoding="utf-8")


def self_test() -> None:
    build = 'group("default") {}\n\ncomponent("pdfium") {\n  output_name = "pdfium"\n}\n'
    rewritten = rewrite_build_gn(build)
    if 'component("pdfium")' in rewritten or rewritten.count(SHARED) != 1:
        raise SystemExit("BUILD.gn rewrite failed")
    if rewrite_build_gn(rewritten) != rewritten:
        raise SystemExit("BUILD.gn rewrite is not idempotent")

    header = "typedef int FPDF_OBJECT_TYPE;\n\n" + STOCK_EXPORT + "\n#if defined(WIN32)\n"
    rewritten_header = rewrite_fpdfview(header)
    if "#if defined(COMPONENT_BUILD)" in rewritten_header or "#endif  // defined(COMPONENT_BUILD)" in rewritten_header:
        raise SystemExit("fpdfview.h rewrite failed")
    if 'visibility("default")' not in rewritten_header:
        raise SystemExit("fpdfview.h rewrite dropped the visibility attribute")
    if rewrite_fpdfview(rewritten_header) != rewritten_header:
        raise SystemExit("fpdfview.h rewrite is not idempotent")

    try:
        rewrite_build_gn('component("pdfium") {\n}\ncomponent("pdfium") {\n}\n')
    except SystemExit:
        pass
    else:
        raise SystemExit("duplicate component target was accepted")
    print("rewrite_shared_library self-test ok")


def main(argv: list[str]) -> None:
    if argv == ["--self-test"]:
        self_test()
        return
    if len(argv) != 1:
        raise SystemExit(f"usage: {sys.argv[0]} <pdfium-source-dir> | --self-test")
    rewrite_tree(pathlib.Path(argv[0]))


if __name__ == "__main__":
    main(sys.argv[1:])
