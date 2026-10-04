namespace PDFtoImage
{
    /// <summary>
    /// Unicode text extracted from one PDF page in reading order.
    /// </summary>
    public readonly struct PdfPageText
    {
        /// <summary>
        /// Creates text extracted from one page.
        /// </summary>
        /// <param name="text">Reading-order text. Characters PDFium could not map are omitted. U+FFFD is kept.</param>
        /// <param name="characterCount">PDFium character count, including characters with no Unicode mapping.</param>
        /// <param name="unknownCharacterCount">Characters whose scalar is missing, U+FFFD, or not a valid Unicode scalar.</param>
        public PdfPageText(string text, int characterCount, int unknownCharacterCount)
        {
            Text = text ?? string.Empty;
            CharacterCount = characterCount;
            UnknownCharacterCount = unknownCharacterCount;
        }

        /// <summary>Gets the page text in reading order.</summary>
        public string Text { get; }

        /// <summary>Gets the number of characters PDFium reported for the page.</summary>
        public int CharacterCount { get; }

        /// <summary>Gets how many of those characters are unknown, replaced, or invalid.</summary>
        public int UnknownCharacterCount { get; }
    }

    /// <summary>
    /// Object counts used to tell a text page from a scan.
    /// </summary>
    public readonly struct PdfPageContentStats
    {
        /// <summary>
        /// Creates content statistics for one page.
        /// </summary>
        /// <param name="textObjectCount">Text objects on the page, including objects nested in forms.</param>
        /// <param name="invisibleTextObjectCount">Text objects whose PDF text rendering mode is 3 (neither fill nor stroke).</param>
        /// <param name="imageAreaCoverage">Sum of image bounding-box areas divided by the page area. Image-bearing forms contribute their page-space bounds. Overlapping boxes can push this above 1.</param>
        public PdfPageContentStats(int textObjectCount, int invisibleTextObjectCount, double imageAreaCoverage)
        {
            TextObjectCount = textObjectCount;
            InvisibleTextObjectCount = invisibleTextObjectCount;
            ImageAreaCoverage = imageAreaCoverage;
        }

        /// <summary>Gets the number of text objects.</summary>
        public int TextObjectCount { get; }

        /// <summary>Gets how many text objects use rendering mode 3.</summary>
        public int InvisibleTextObjectCount { get; }

        /// <summary>
        /// Gets whether the page has text and every text object is invisible.
        /// That is the usual shape of a hidden OCR layer painted over a scan.
        /// </summary>
        public bool TextObjectsAreInvisible => TextObjectCount > 0 && InvisibleTextObjectCount == TextObjectCount;

        /// <summary>Gets image bounding-box area divided by page area.</summary>
        public double ImageAreaCoverage { get; }
    }

    /// <summary>
    /// Text and content statistics collected from one load of a page.
    /// </summary>
    public readonly struct PdfPageAnalysis
    {
        /// <summary>
        /// Creates a combined page analysis.
        /// </summary>
        public PdfPageAnalysis(PdfPageText text, PdfPageContentStats content)
        {
            Text = text;
            Content = content;
        }

        /// <summary>Gets the extracted text.</summary>
        public PdfPageText Text { get; }

        /// <summary>Gets the content statistics.</summary>
        public PdfPageContentStats Content { get; }
    }
}
