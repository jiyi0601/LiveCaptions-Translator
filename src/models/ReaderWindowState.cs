namespace LiveCaptionsTranslator.models;

public sealed class ReaderWindowState
{
    public bool Topmost { get; set; } = true;
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public double Width { get; set; } = 560;
    public double Height { get; set; } = 720;
    public double OriginalFontSize { get; set; } = 15;
    public double TranslatedFontSize { get; set; } = 18;
}
