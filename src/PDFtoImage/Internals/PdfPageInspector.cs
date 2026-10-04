using System;
using System.Buffers;
using System.Runtime.InteropServices;

namespace PDFtoImage.Internals
{
    /// <summary>
    /// Reads one already-loaded page. Callers hold <see cref="NativeMethods.WithLock"/> for the whole call.
    /// </summary>
    internal static class PdfPageInspector
    {
        internal const int TextRenderModeInvisible = 3;

        private const int PageObjectText = 1;
        private const int PageObjectImage = 3;
        private const int PageObjectForm = 5;
        private const int MaxFormDepth = 8;

        internal readonly struct Result
        {
            internal Result(string text, int characterCount, int unknownCharacterCount, int textObjectCount, int invisibleTextObjectCount, double imageAreaCoverage)
            {
                Text = text;
                CharacterCount = characterCount;
                UnknownCharacterCount = unknownCharacterCount;
                TextObjectCount = textObjectCount;
                InvisibleTextObjectCount = invisibleTextObjectCount;
                ImageAreaCoverage = imageAreaCoverage;
            }

            internal string Text { get; }

            internal int CharacterCount { get; }

            internal int UnknownCharacterCount { get; }

            internal int TextObjectCount { get; }

            internal int InvisibleTextObjectCount { get; }

            internal double ImageAreaCoverage { get; }
        }

        internal static Result Inspect(IntPtr page)
        {
            var (text, characterCount, unknownCount) = ReadText(page);
            var stats = new ContentWalker();
            var objectCount = NativeMethods.Page_CountObjects(page);

            if (objectCount > 0)
            {
                for (var i = 0; i < objectCount; i++)
                {
                    var pageObject = NativeMethods.Page_GetObject(page, i);

                    if (pageObject != IntPtr.Zero)
                        stats.Walk(pageObject, depth: 0, atPageLevel: true);
                }
            }

            var pageArea = NativeMethods.GetPageWidth(page) * NativeMethods.GetPageHeight(page);
            var coverage = pageArea > 0 && stats.ImageArea > 0
                ? stats.ImageArea / pageArea
                : 0d;

            return new Result(text, characterCount, unknownCount, stats.TextObjectCount, stats.InvisibleTextObjectCount, coverage);
        }

        private static (string Text, int CharacterCount, int UnknownCount) ReadText(IntPtr page)
        {
            var textPage = NativeMethods.Text_LoadPage(page);

            if (textPage == IntPtr.Zero)
                return (string.Empty, 0, 0);

            char[]? rented = null;

            try
            {
                var count = NativeMethods.Text_CountChars(textPage);

                if (count <= 0)
                    return (string.Empty, 0, 0);

                // A supplementary-plane character becomes two UTF-16 code units.
                rented = ArrayPool<char>.Shared.Rent(checked(count * 2));

                // One native call when every character is a single BMP scalar.
                // GetText omits unmapped and non-UCS-2 characters, so a length
                // mismatch falls through to the per-character read.
                if (TryReadBulk(textPage, rented, count, out var bulkText, out var bulkUnknown))
                    return (bulkText, count, bulkUnknown);

                var length = 0;
                var unknown = 0;

                for (var i = 0; i < count; i++)
                {
                    var codePoint = NativeMethods.Text_GetUnicode(textPage, i);

                    if (IsUnknown(codePoint))
                    {
                        unknown++;

                        // Keep U+FFFD in the string. Drop missing and illegal scalars so the
                        // result stays a well-formed .NET string in reading order.
                        if (codePoint == 0xFFFD)
                            rented[length++] = '\uFFFD';

                        continue;
                    }

                    if (codePoint <= 0xFFFF)
                    {
                        rented[length++] = (char)codePoint;
                    }
                    else
                    {
                        var value = codePoint - 0x10000;
                        rented[length++] = (char)((value >> 10) + 0xD800);
                        rented[length++] = (char)((value & 0x3FF) + 0xDC00);
                    }
                }

                return (new string(rented, 0, length), count, unknown);
            }
            finally
            {
                if (rented != null)
                    ArrayPool<char>.Shared.Return(rented);

                NativeMethods.Text_ClosePage(textPage);
            }
        }

        private static bool TryReadBulk(IntPtr textPage, char[] rented, int count, out string text, out int unknown)
        {
            text = string.Empty;
            unknown = 0;
            var handle = GCHandle.Alloc(rented, GCHandleType.Pinned);
            int written;

            try
            {
                written = NativeMethods.Text_GetText(textPage, 0, count, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }

            // The return value includes the trailing NUL. Supplementary-plane characters
            // and characters with no Unicode mapping are left out, so the only buffer
            // that accounts for every character is one BMP scalar per index.
            if (written != count + 1 || rented[count] != '\0')
                return false;

            var replacement = 0;

            for (var i = 0; i < count; i++)
            {
                var unit = rented[i];

                if (unit == '\0' || (unit >= '\uD800' && unit <= '\uDFFF'))
                    return false;

                if (unit == '\uFFFD')
                    replacement++;
            }

            text = new string(rented, 0, count);
            unknown = replacement;
            return true;
        }

        private static bool IsUnknown(uint codePoint) =>
            codePoint == 0
            || codePoint == 0xFFFD
            || codePoint > 0x10FFFF
            || (codePoint >= 0xD800 && codePoint <= 0xDFFF);

        private sealed class ContentWalker
        {
            internal int TextObjectCount { get; private set; }

            internal int InvisibleTextObjectCount { get; private set; }

            internal double ImageArea { get; private set; }

            internal bool Walk(IntPtr pageObject, int depth, bool atPageLevel)
            {
                var type = NativeMethods.PageObj_GetType(pageObject);

                if (type == PageObjectText)
                {
                    TextObjectCount++;

                    if (NativeMethods.TextObj_GetTextRenderMode(pageObject) == TextRenderModeInvisible)
                        InvisibleTextObjectCount++;

                    return false;
                }

                if (type == PageObjectImage)
                {
                    if (atPageLevel)
                        AddBounds(pageObject);

                    return true;
                }

                if (type != PageObjectForm || depth >= MaxFormDepth)
                    return false;

                var childCount = NativeMethods.FormObj_CountObjects(pageObject);
                var hasImage = false;

                if (childCount > 0)
                {
                    for (var i = 0; i < childCount; i++)
                    {
                        var child = NativeMethods.FormObj_GetObject(pageObject, i);

                        if (child != IntPtr.Zero && Walk(child, depth + 1, atPageLevel: false))
                            hasImage = true;
                    }
                }

                // Child bounds are in the form's local space. The form bounds are in page space.
                if (atPageLevel && hasImage)
                    AddBounds(pageObject);

                return hasImage;
            }

            private void AddBounds(IntPtr pageObject)
            {
                if (!NativeMethods.PageObj_GetBounds(pageObject, out var left, out var bottom, out var right, out var top))
                    return;

                var width = Math.Abs(right - left);
                var height = Math.Abs(top - bottom);

                if (width > 0 && height > 0 && !double.IsInfinity(width) && !double.IsInfinity(height))
                    ImageArea += width * height;
            }
        }
    }
}
