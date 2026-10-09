#!/usr/bin/env bash
# Build one linux-x64 libpdfium.so that contains both AGG and Skia.
#
# Pin: PDFium branch chromium/8066, the same revision as bblanchon.PDFium
# 156.0.8066. The C# P/Invoke in this repo (FPDF_LIBRARY_CONFIG version 4,
# 48 bytes on 64-bit) matches that revision. Do not move the pin without
# rechecking the struct layout.
#
# This does not build a GPU surface. Skia fills the same CPU bitmap that
# FPDF_RenderPageBitmap already returns (BGRA, or Gray8 when the caller asks).
#
# The checkout is tens of gigabytes. Linking Skia into PDFium wants more RAM
# than a 4-core machine with about 4GB free. Set NINJA_JOBS and run this on
# a larger host. The .so is not committed.

set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
ROOT=${PDFIUM_SKIA_ROOT:-"$HOME/src/pdfium-skia-8066"}
JOBS=${NINJA_JOBS:-2}
REVISION=chromium/8066
SOURCE="$ROOT/pdfium"
OUT="$SOURCE/out/SkiaCpu"

if [[ "${1:-}" == "--self-test" ]]; then
  python3 "$SCRIPT_DIR/rewrite_shared_library.py" --self-test
  exit 0
fi

echo "Work directory: $ROOT"
echo "Ninja jobs: $JOBS (override with NINJA_JOBS)"
echo "Expect a multi-gigabyte checkout and a link that needs several GB of RAM."

mkdir -p "$ROOT"
if [[ ! -d "$ROOT/depot_tools/.git" ]]; then
  git clone https://chromium.googlesource.com/chromium/tools/depot_tools.git "$ROOT/depot_tools"
fi
export PATH="$ROOT/depot_tools:$PATH"
export DEPOT_TOOLS_UPDATE=0

cat > "$ROOT/.gclient" <<'EOF'
solutions = [
  {
    "name": "pdfium",
    "url": "https://pdfium.googlesource.com/pdfium.git@chromium/8066",
    "deps_file": "DEPS",
    "managed": False,
    "custom_deps": {},
    "custom_vars": {
      "checkout_configuration": "default",
    },
  },
]
target_os = ["linux"]
EOF

cd "$ROOT"
# Flag names differ across depot_tools. --no-history is the shallow sync.
if ! gclient sync --no-history; then
  gclient sync
fi

if [[ ! -d "$SOURCE/third_party/skia" ]]; then
  echo "third_party/skia is missing. checkout_configuration=minimal skips Skia; this recipe needs the default checkout." >&2
  exit 1
fi

if [[ "${INSTALL_BUILD_DEPS:-0}" == "1" ]]; then
  "$SOURCE/build/install-build-deps.sh" --no-chromeos-fonts
else
  echo "System packages are not installed by default. If gn or the link fails on missing headers, re-run with INSTALL_BUILD_DEPS=1 (uses sudo)."
fi

python3 "$SCRIPT_DIR/rewrite_shared_library.py" "$SOURCE"

mkdir -p "$OUT"
cat > "$OUT/args.gn" <<'EOF'
is_debug = false
pdf_is_standalone = true
pdf_use_partition_alloc = false
target_cpu = "x64"
target_os = "linux"
pdf_enable_v8 = false
pdf_enable_xfa = false
treat_warnings_as_errors = false
is_component_build = false
clang_use_chrome_plugins = false
pdf_use_skia = true
pdf_use_agg = true
EOF

# pdf_is_complete_lib is the fat static archive. It must stay unset here.
if grep -q 'pdf_is_complete_lib' "$OUT/args.gn"; then
  echo "args.gn must not set pdf_is_complete_lib on a shared build" >&2
  exit 1
fi

cd "$SOURCE"
gn gen "$OUT"
ninja -C "$OUT" -j "$JOBS" pdfium

LIB="$OUT/libpdfium.so"
if [[ ! -f "$LIB" ]]; then
  echo "ninja finished without $LIB" >&2
  exit 1
fi

cp -f "$LIB" "$ROOT/libpdfium.so"
if command -v strip >/dev/null 2>&1; then
  strip --strip-unneeded "$ROOT/libpdfium.so"
fi

missing=0
for symbol in FPDF_RenderPageSkia FPDF_InitLibraryWithConfig FPDF_RenderPageBitmap; do
  if ! nm -D "$ROOT/libpdfium.so" | grep -q " ${symbol}$"; then
    echo "missing export: $symbol" >&2
    missing=1
  fi
done
if [[ "$missing" -ne 0 ]]; then
  exit 1
fi

echo "Drop-in: $ROOT/libpdfium.so"
if command -v readelf >/dev/null 2>&1; then
  readelf -d "$ROOT/libpdfium.so" | grep SONAME || true
fi
ls -lh "$ROOT/libpdfium.so"
echo "Replace libpdfium.so next to the Native AOT binary. Do not commit it."
echo "Then set PDFTOIMAGE_RENDERER=skia (miniocr: MINIOCR_PDF_RENDERER=skia)."
echo "Pinned revision: $REVISION"
