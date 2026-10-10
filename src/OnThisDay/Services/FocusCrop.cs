namespace OnThisDay.Services;

/// <summary>A box normalised to 0-1 of the image size.</summary>
public readonly record struct FocusBox(float X, float Y, float W, float H);

public static class FocusCrop
{
    private const double Margin = 0.2;     // added on each side of the focus box, relative to its size
    private const double MaxUpscale = 1.5;

    /// <summary>
    /// Picks the crop window (in image pixels) that covers a w×h output: centred on the focus box plus margin,
    /// zoomed in as far as the box allows but never past the image or beyond 1.5x upscale.
    /// Without a focus box it is a plain centre cover crop.
    /// </summary>
    public static (int X, int Y, int W, int H) Compute(int imgW, int imgH, FocusBox? focus, int w, int h)
    {
        var aspect = (double)w / h;

        // Largest crop with the target aspect that fits in the image
        double maxW = imgW, maxH = imgW / aspect;
        if (maxH > imgH)
        {
            maxH = imgH;
            maxW = imgH * aspect;
        }

        double cropW = maxW, cropH = maxH;
        double centreX = imgW / 2.0, centreY = imgH / 2.0;

        if (focus is { } f && f.W > 0 && f.H > 0)
        {
            var boxW = f.W * imgW * (1 + 2 * Margin);
            var boxH = f.H * imgH * (1 + 2 * Margin);
            centreX = (f.X + f.W / 2) * imgW;
            centreY = (f.Y + f.H / 2) * imgH;

            // Smallest crop that holds the box, but not so small that it needs more than 1.5x upscaling
            cropW = Math.Max(Math.Max(boxW, boxH * aspect), w / MaxUpscale);

            // A box wider or taller than the image allows keeps its centre and loses its edges
            cropW = Math.Min(cropW, maxW);
            cropH = cropW / aspect;
        }

        var cw = Math.Clamp((int)Math.Round(cropW), 1, imgW);
        var ch = Math.Clamp((int)Math.Round(cropH), 1, imgH);

        // Shift the window to stay inside the image
        var x = Math.Clamp((int)Math.Round(centreX - cw / 2.0), 0, imgW - cw);
        var y = Math.Clamp((int)Math.Round(centreY - ch / 2.0), 0, imgH - ch);

        return (x, y, cw, ch);
    }
}
