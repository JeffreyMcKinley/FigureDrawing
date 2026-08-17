namespace FigureDrawing.Core;

// The reference-viewing aids the player screen offers on the pose itself: value-study helpers
// (grayscale, blur), a mirror, a rule-of-thirds grid, and zoom. They change how the image is
// presented and nothing about the session — no count, no timer, no drawing time depends on them.
//
// It lives in Core because the toggling and the zoom clamp are rules ("zoom never goes below 1:1,
// never past 2.5x, and moves in fixed steps") rather than view wiring, and because the Activity is
// not allowed to own a state machine. The screen reads these properties and applies the matching
// Android effect: a saturation-0 ColorMatrix, a RenderEffect blur, a scale/scaleX transform, a
// grid overlay view.
public sealed class ViewerTools
{
    // 1.0 is fit-to-screen. Zooming below that would letterbox the pose for no gain.
    public const double MinZoom = 1.0;
    public const double MaxZoom = 2.5;
    public const double ZoomStep = 0.2;

    // grayscale : seeded from AppSettings.GrayscaleMode, so "start in grayscale" is honoured without
    //             the screen having to poke the flag after construction.
    public ViewerTools(bool grayscale = false) => Grayscale = grayscale;

    // Saturation dropped to zero — the classic value study.
    public bool Grayscale { get; private set; }

    // Mirror horizontally: a fresh read of a pose the eye has already learned.
    public bool Flip { get; private set; }

    // Rule-of-thirds overlay for placement and proportion.
    public bool Grid { get; private set; }

    // Heavy blur, for blocking in the big shapes before any detail.
    public bool Blur { get; private set; }

    // Current magnification, always a clean multiple of the step within the range.
    public double Zoom { get; private set; } = MinZoom;

    public bool CanZoomIn => Zoom < MaxZoom - Epsilon;
    public bool CanZoomOut => Zoom > MinZoom + Epsilon;

    public void ToggleGrayscale() => Grayscale = !Grayscale;
    public void ToggleFlip() => Flip = !Flip;
    public void ToggleGrid() => Grid = !Grid;
    public void ToggleBlur() => Blur = !Blur;

    public void ZoomIn() => Zoom = Clamp(Zoom + ZoomStep);
    public void ZoomOut() => Zoom = Clamp(Zoom - ZoomStep);

    // Back to fit-to-screen — what a new pose gets, so a zoom set for one image does not silently
    // crop the next.
    public void ResetZoom() => Zoom = MinZoom;

    const double Epsilon = 1e-9;

    // Clamped to the range and rounded, so repeated stepping cannot accumulate binary-fraction drift
    // (0.1 + 0.2 style) into a zoom that never quite reaches its bound.
    static double Clamp(double value) => Math.Round(Math.Clamp(value, MinZoom, MaxZoom), 2);
}
