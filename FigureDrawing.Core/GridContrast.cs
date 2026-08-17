namespace FigureDrawing.Core;

// The four ARGB values the palette offers a grid line, supplied by the screen so the Nocturne
// tokens stay in colors.xml (ARCHITECTURE.md §3) while the *choice* between them stays testable
// here. "Light" and "dark" name the line itself, not the image it sits on.
public readonly record struct GridPalette(int LightLine, int LightCasing, int DarkLine, int DarkCasing);

// How one guide line is painted: a core stroke with a casing of the opposite tone behind it.
public readonly record struct GridLineStyle(int LineArgb, int CasingArgb);

// The four rule-of-thirds guides in layout order — the vertical pair left to right, then the
// horizontal pair top to bottom.
public readonly record struct GridStyles(
    GridLineStyle VerticalLeft,
    GridLineStyle VerticalRight,
    GridLineStyle HorizontalTop,
    GridLineStyle HorizontalBottom);

// Pure, testable contrast math for the rule-of-thirds overlay. A hairline at one fixed colour
// disappears over a bright reference, so each guide picks its tone from the strip of image it
// actually crosses.
//
// It sits beside BitmapMath rather than in Session/ because it is the same kind of thing: a
// supporting rendering service (ARCHITECTURE.md §16), not a domain object. Grid *visibility* is
// ViewerTools.Grid; grid *colour* is a rendering decision and never becomes session state, which
// is what keeps INV-VIEW-3 ("the entity holds no bitmap, no matrix, and no view") intact.
//
// The screen samples the decoded pose down to a SampleGrid x SampleGrid block of pixels once per
// pose and hands the ints here. Everything after that is arithmetic on those ints, so re-resolving
// the colours when the drawer zooms or flips costs no bitmap work.
public static class GridContrast
{
    // 32x32 = 1024 ints (~4 KB). Coarse enough to be free, fine enough that a band either side of
    // a third still averages several distinct columns.
    public const int SampleGrid = 32;

    // Above this relative luminance the region is "light" and wants a dark line.
    public const double LightThreshold = 0.5;

    // How much of the image, either side of a guide, feeds that guide's average. 5% each way is
    // roughly the line's own visual weight plus the eye's immediate surround.
    public const double BandHalfWidth = 0.05;

    // The guides sit on the thirds.
    const double FirstThird = 1.0 / 3.0;
    const double SecondThird = 2.0 / 3.0;

    // Relative luminance of a packed ARGB pixel, 0..1, using the sRGB coefficients. Alpha is
    // ignored: a decoded pose is opaque, and the letterbox around it is handled by the caller
    // falling back rather than by averaging transparent pixels.
    public static double Luminance(int argb)
    {
        var r = ((argb >> 16) & 0xFF) / 255.0;
        var g = ((argb >> 8) & 0xFF) / 255.0;
        var b = (argb & 0xFF) / 255.0;
        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    // A light region gets a dark core, and the casing is always the opposite tone of the core — a
    // black casing behind a black line would do nothing, and it is the casing that carries the line
    // across a local patch fighting the region's average.
    public static GridLineStyle ForLuminance(double luminance, GridPalette palette) =>
        luminance > LightThreshold
            ? new GridLineStyle(palette.DarkLine, palette.LightCasing)
            : new GridLineStyle(palette.LightLine, palette.DarkCasing);

    // Resolve all four guides against the pose as it is currently presented.
    //
    // The overlay spans the whole stage but the image is fitCenter'd inside it and then scaled by
    // zoom about the centre, so a guide can land on the letterbox bar rather than on the pose. Any
    // guide that does falls back to the light style, which is what reads over @color/stage.
    //
    // Totality is the contract: every degenerate input — no samples, a short span, a zero-sized
    // stage or image, a non-positive zoom — returns four light styles rather than throwing. This
    // runs on the render path and a bad frame must never sink the screen.
    public static GridStyles LineStyles(
        ReadOnlySpan<int> samples,
        int grid,
        int stageWidth,
        int stageHeight,
        int imageWidth,
        int imageHeight,
        double zoom,
        bool flip,
        GridPalette palette)
    {
        var fallback = ForLuminance(0.0, palette);
        var allFallback = new GridStyles(fallback, fallback, fallback, fallback);

        if (grid <= 0 || samples.Length < grid * grid)
            return allFallback;

        if (stageWidth <= 0 || stageHeight <= 0 || imageWidth <= 0 || imageHeight <= 0)
            return allFallback;

        if (zoom <= 0 || double.IsNaN(zoom) || double.IsInfinity(zoom))
            return allFallback;

        // fitCenter, then ScaleX/ScaleY about the view centre: one centred rect either way.
        var fit = Math.Min((double)stageWidth / imageWidth, (double)stageHeight / imageHeight);
        var drawnWidth = imageWidth * fit * zoom;
        var drawnHeight = imageHeight * fit * zoom;
        var left = (stageWidth - drawnWidth) / 2.0;
        var top = (stageHeight - drawnHeight) / 2.0;

        // The part of the image actually on screen. A zoomed-in pose is cropped by the stage, and
        // averaging the off-screen remainder would colour a guide from pixels nobody can see.
        var visibleColumns = VisibleCells(left, drawnWidth, stageWidth, grid);
        var visibleRows = VisibleCells(top, drawnHeight, stageHeight, grid);

        // Flip is a negative horizontal scale, so a vertical guide reads the mirrored column. The
        // horizontal pair is unaffected — the app never mirrors vertically.
        var firstColumn = BandCells(Normalize(FirstThird * stageWidth, left, drawnWidth, flip), grid);
        var secondColumn = BandCells(Normalize(SecondThird * stageWidth, left, drawnWidth, flip), grid);
        var firstRow = BandCells(Normalize(FirstThird * stageHeight, top, drawnHeight, mirror: false), grid);
        var secondRow = BandCells(Normalize(SecondThird * stageHeight, top, drawnHeight, mirror: false), grid);

        return new GridStyles(
            Resolve(samples, grid, firstColumn, visibleRows, palette, fallback),
            Resolve(samples, grid, secondColumn, visibleRows, palette, fallback),
            Resolve(samples, grid, visibleColumns, firstRow, palette, fallback),
            Resolve(samples, grid, visibleColumns, secondRow, palette, fallback));
    }

    // Stage coordinate to a normalized position within the drawn image.
    static double Normalize(double stagePosition, double origin, double size, bool mirror)
    {
        var position = (stagePosition - origin) / size;
        return mirror ? 1.0 - position : position;
    }

    static GridLineStyle Resolve(
        ReadOnlySpan<int> samples,
        int grid,
        CellRange columns,
        CellRange rows,
        GridPalette palette,
        GridLineStyle fallback) =>
        columns.IsValid && rows.IsValid
            ? ForLuminance(MeanLuminance(samples, grid, columns, rows), palette)
            : fallback;

    // Inclusive cell range covering BandHalfWidth either side of a normalized position. Invalid
    // when the position itself is off the image — that is the letterbox case.
    static CellRange BandCells(double position, int grid)
    {
        if (position < 0.0 || position > 1.0 || double.IsNaN(position))
            return CellRange.Invalid;

        var lo = ToCell(position - BandHalfWidth, grid);
        var hi = ToCell(position + BandHalfWidth, grid);

        // Clamping at an edge narrows the band rather than reading out of bounds, and the cell the
        // guide itself sits in is always included.
        var centre = ToCell(position, grid);
        return new CellRange(Math.Min(lo, centre), Math.Max(hi, centre));
    }

    // Inclusive cell range for the part of the image between `origin` and `origin + size` that
    // falls inside [0, stageSize]. Invalid when none of it does.
    static CellRange VisibleCells(double origin, double size, int stageSize, int grid)
    {
        var lo = (0.0 - origin) / size;
        var hi = (stageSize - origin) / size;

        if (hi <= 0.0 || lo >= 1.0)
            return CellRange.Invalid;

        return new CellRange(ToCell(lo, grid), ToCell(hi, grid));
    }

    // Normalized position to a cell index, clamped into the grid.
    static int ToCell(double position, int grid) =>
        Math.Clamp((int)(position * grid), 0, grid - 1);

    static double MeanLuminance(ReadOnlySpan<int> samples, int grid, CellRange columns, CellRange rows)
    {
        var total = 0.0;
        var count = 0;

        for (var row = rows.Low; row <= rows.High; row++)
        {
            var offset = row * grid;
            for (var column = columns.Low; column <= columns.High; column++)
            {
                total += Luminance(samples[offset + column]);
                count++;
            }
        }

        return count == 0 ? 0.0 : total / count;
    }

    // An inclusive span of sample cells on one axis, or the "nothing here" marker.
    readonly record struct CellRange(int Low, int High)
    {
        public static CellRange Invalid => new(1, 0);

        public bool IsValid => Low <= High;
    }
}
