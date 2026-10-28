namespace DataScope.Scanner.Content.Ocr;

/// <summary>One way to turn a page before its text is read.</summary>
/// <param name="QuarterTurns">Clockwise quarter turns, 0 to 3.</param>
/// <param name="Skew">
/// Then a turn of this many degrees clockwise, to level lines that rise to the right by that much (negative for lines
/// that fall). Zero when the lines are already level enough.
/// </param>
internal readonly record struct PageTurn(int QuarterTurns, double Skew)
{
    public static readonly PageTurn None = new(0, 0);
}

/// <param name="Tries">Ways to turn the page so its lines run level and the right way up, most likely first.</param>
/// <param name="TextLike">
/// True when the picture looks like lines of text, so a poor reading is worth another try turned another way. False
/// for photos, drawings and pictures too dark or too empty to judge: those are read as they are, once.
/// </param>
internal sealed record PageLayout(IReadOnlyList<PageTurn> Tries, bool TextLike)
{
    public static readonly PageLayout AsIs = new([PageTurn.None], false);

    public PageTurn Best => Tries[0];
}

/// <summary>
/// Finds how a scanned page lies (D131): tilted a few degrees by the scanner feeder, turned a quarter because it was
/// fed sideways, or upside down. Tesseract reads level, upright lines; a 4° tilt is enough to misread a digit.
/// <para>
/// The page is shrunk and reduced to ink and paper. Lines of text are found by projecting the ink onto an axis at
/// each angle within <see cref="MaxSkew"/>: when the angle matches the lines, the projection is sharp peaks (lines)
/// and empty valleys (the space between them). Lines that run down the page instead of across show as sharper
/// columns than rows. Which way up the lines are is judged from the letters: ascenders and capitals reach above the
/// line's core far more often than descenders hang below it. That is a hint only; when the first reading is poor the
/// page is read again the other way up, and the better reading is kept.
/// </para>
/// Pure arithmetic over bounded arrays: the work is at most <see cref="MaxPoints"/> ink pixels per angle tried.
/// </summary>
internal static class PageGeometry
{
    /// <summary>The page is judged at this size along its longer side: enough to resolve lines of body text.</summary>
    internal const int SampleSide = 1000;

    /// <summary>At most this many ink pixels are projected per angle.</summary>
    internal const int MaxPoints = 100_000;

    /// <summary>Scanner tilt beyond this is not looked for: a page at a steeper angle is a photo, not a scan.</summary>
    internal const double MaxSkew = 15;

    /// <summary>Tilts smaller than this are left alone: Tesseract copes, and turning a picture softens it a little.</summary>
    internal const double MinSkew = 0.4;

    /// <summary>A page whose ink covers less than this is too empty to judge; more than <see cref="MaxInk"/> is a photo or dark.</summary>
    private const double MinInk = 0.002;
    private const double MaxInk = 0.4;

    /// <summary>Columns must be this much sharper than rows before a page is taken as sideways.</summary>
    private const double SidewaysMargin = 1.5;

    /// <summary>Below this ratio between the two axes it is unclear which way the lines run, so both are tried.</summary>
    private const double SureMargin = 3;

    /// <summary>Works out how <paramref name="image"/> lies and the turns that should level it, most likely first.</summary>
    public static PageLayout Find(GrayImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var (small, width, height) = Downsample(image, SampleSide);
        var threshold = Otsu(small);
        var ink = 0;
        foreach (var p in small)
        {
            ink += p < threshold ? 1 : 0;
        }
        var fraction = (double)ink / small.Length;
        if (ink == 0 || fraction < MinInk || fraction > MaxInk)
        {
            return PageLayout.AsIs;
        }

        // Ink pixels, centred on the page; every so many in raster order when there are more than MaxPoints.
        var stride = (ink + MaxPoints - 1) / MaxPoints;
        var count = (ink + stride - 1) / stride;
        var xs = new float[count];
        var ys = new float[count];
        float cx = width / 2f, cy = height / 2f;
        int seen = 0, n = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (small[(y * width) + x] < threshold && seen++ % stride == 0 && n < count)
                {
                    xs[n] = x - cx;
                    ys[n] = y - cy;
                    n++;
                }
            }
        }
        var radius = (int)Math.Ceiling(Math.Sqrt((cx * cx) + (cy * cy))) + 2;

        // Rows sharper than columns: lines run across. Columns sharper: the page is sideways.
        var across = Level(xs, ys, n, 0, radius);
        var down = Level(xs, ys, n, 1, radius);
        var sideways = down.Score > across.Score * SidewaysMargin;
        var (first, second) = sideways ? (down, across) : (across, down);
        var sure = first.Score >= second.Score * SureMargin;
        if (!first.TextLike && !second.TextLike)
        {
            return PageLayout.AsIs;
        }

        var tries = new List<PageTurn>(4);
        AddBothWaysUp(tries, first);
        if (!sure)
        {
            AddBothWaysUp(tries, second);
        }
        return new PageLayout(tries, true);
    }

    private static void AddBothWaysUp(List<PageTurn> tries, Orientation o)
    {
        var skew = Math.Abs(o.Skew) >= MinSkew ? o.Skew : 0;
        var upright = new PageTurn(o.QuarterTurns, skew);
        var flipped = new PageTurn((o.QuarterTurns + 2) % 4, skew);
        tries.Add(o.UpsideDown ? flipped : upright);
        tries.Add(o.UpsideDown ? upright : flipped);
    }

    /// <summary>
    /// How lines lie once the ink is turned <paramref name="quarterTurns"/> clockwise: their tilt, how sharp the lines
    /// are at that tilt, whether they look like text, and whether the letters look upside down.
    /// </summary>
    private readonly record struct Orientation(int QuarterTurns, double Skew, double Score, bool TextLike, bool UpsideDown);

    private static Orientation Level(float[] xs, float[] ys, int n, int quarterTurns, int radius)
    {
        // A clockwise quarter turn moves a point right of the centre to below it: (x, y) becomes (-y, x).
        var (px, py) = quarterTurns == 1 ? (Negate(ys, n), xs) : (xs, ys);
        var bins = new int[(2 * radius) + 2];
        var (bestAngle, bestScore) = (0.0, double.MinValue);
        for (var degrees = -MaxSkew; degrees <= MaxSkew + 1e-9; degrees += 1)
        {
            var score = Sharpness(px, py, n, degrees, radius, bins);
            if (score > bestScore)
            {
                (bestAngle, bestScore) = (degrees, score);
            }
        }
        var coarse = bestAngle;
        for (var step = -10; step <= 10; step++)
        {
            var degrees = coarse + (step / 10.0);
            if (step == 0 || Math.Abs(degrees) > MaxSkew)
            {
                continue;
            }
            var score = Sharpness(px, py, n, degrees, radius, bins);
            if (score > bestScore)
            {
                (bestAngle, bestScore) = (degrees, score);
            }
        }
        bestAngle = Math.Round(bestAngle, 1);

        Project(px, py, n, bestAngle, radius, bins);
        var lines = new int[bins.Length];
        Array.Copy(bins, lines, bins.Length);
        Project(px, py, n, bestAngle + 90, radius, bins);
        // Several lines with space between them, or one line whose words have space between them.
        var textLike = Gaps(lines) >= 2 || Gaps(bins) >= 4;
        return new Orientation(quarterTurns, bestAngle, bestScore, textLike, UpsideDown(lines));
    }

    private static float[] Negate(float[] values, int n)
    {
        var negated = new float[n];
        for (var i = 0; i < n; i++)
        {
            negated[i] = -values[i];
        }
        return negated;
    }

    /// <summary>
    /// How sharply the ink falls into lines tilted <paramref name="degrees"/>: the sum of squared differences between
    /// neighbouring rows of the projection. Peaks with empty space between score high; a smeared projection low.
    /// </summary>
    internal static double Sharpness(float[] xs, float[] ys, int n, double degrees, int radius, int[] bins)
    {
        Project(xs, ys, n, degrees, radius, bins);
        double score = 0;
        for (var i = 1; i < bins.Length; i++)
        {
            double d = bins[i] - bins[i - 1];
            score += d * d;
        }
        return score;
    }

    /// <summary>
    /// Counts ink along lines rising <paramref name="degrees"/> to the right: a point (x, y) falls on the line
    /// t = y cos a + x sin a, measured down the page.
    /// </summary>
    private static void Project(float[] xs, float[] ys, int n, double degrees, int radius, int[] bins)
    {
        Array.Clear(bins);
        var a = degrees * Math.PI / 180;
        float c = (float)Math.Cos(a), s = (float)Math.Sin(a);
        for (var i = 0; i < n; i++)
        {
            // Floor, not round: rounding sends half-way values to even numbers, which at 0° would empty every other
            // row of a page with an odd height and make that angle look sharpest whatever the page holds.
            var t = (int)MathF.Floor((ys[i] * c) + (xs[i] * s)) + radius;
            if ((uint)t < (uint)bins.Length)
            {
                bins[t]++;
            }
        }
    }

    /// <summary>Runs of (nearly) empty rows between the first and last ink in a projection.</summary>
    private static int Gaps(int[] profile)
    {
        var max = profile.Max();
        var empty = max / 50;
        int first = Array.FindIndex(profile, v => v > empty), last = Array.FindLastIndex(profile, v => v > empty);
        var gaps = 0;
        var inGap = false;
        for (var i = first; first >= 0 && i <= last; i++)
        {
            var isEmpty = profile[i] <= empty;
            gaps += isEmpty && !inGap ? 1 : 0;
            inGap = isEmpty;
        }
        return gaps;
    }

    /// <summary>
    /// Whether the lines in a projection look upside down. Each line has a dense core (the height of x); ink above
    /// the core is ascenders and capitals (b d f h k l t and every capital), ink below is descenders (g j p q y), and
    /// in English and most Latin text the first is the more common. When there is clearly more below, the page is
    /// probably upside down.
    /// </summary>
    internal static bool UpsideDown(int[] profile)
    {
        var max = profile.Max();
        var floor = Math.Max(1, max / 100);
        long above = 0, below = 0;
        var i = 0;
        while (i < profile.Length)
        {
            if (profile[i] < floor)
            {
                i++;
                continue;
            }
            var start = i;
            while (i < profile.Length && profile[i] >= floor)
            {
                i++;
            }
            var end = i - 1;
            if (end - start < 4)
            {
                continue; // A rule or speck, not a line of letters.
            }
            var lineMax = 0;
            for (var j = start; j <= end; j++)
            {
                lineMax = Math.Max(lineMax, profile[j]);
            }
            int coreTop = start, coreBottom = end;
            while (profile[coreTop] * 2 < lineMax)
            {
                coreTop++;
            }
            while (profile[coreBottom] * 2 < lineMax)
            {
                coreBottom--;
            }
            for (var j = start; j < coreTop; j++)
            {
                above += profile[j];
            }
            for (var j = coreBottom + 1; j <= end; j++)
            {
                below += profile[j];
            }
        }
        return below > above * 1.2;
    }

    /// <summary>A copy of <paramref name="image"/> turned as <paramref name="turn"/> says, on white; the image itself when no turn is needed.</summary>
    public static GrayImage Straighten(GrayImage image, PageTurn turn)
    {
        ArgumentNullException.ThrowIfNull(image);
        var turned = Turn(image, turn.QuarterTurns);
        return turn.Skew == 0 ? turned : Rotate(turned, turn.Skew);
    }

    /// <summary>Turns a picture clockwise by whole quarter turns: exact, nothing resampled.</summary>
    internal static GrayImage Turn(GrayImage image, int quarterTurns)
    {
        var (w, h, src) = (image.Width, image.Height, image.Pixels);
        switch (((quarterTurns % 4) + 4) % 4)
        {
            case 1:
                {
                    // The bottom-left corner comes to the top left.
                    var dst = new byte[src.Length];
                    for (var v = 0; v < w; v++)
                    {
                        for (var u = 0; u < h; u++)
                        {
                            dst[(v * h) + u] = src[((h - 1 - u) * w) + v];
                        }
                    }
                    return new GrayImage(dst, h, w);
                }
            case 2:
                {
                    var dst = new byte[src.Length];
                    for (var i = 0; i < src.Length; i++)
                    {
                        dst[i] = src[src.Length - 1 - i];
                    }
                    return new GrayImage(dst, w, h);
                }
            case 3:
                {
                    // The top-right corner comes to the top left.
                    var dst = new byte[src.Length];
                    for (var v = 0; v < w; v++)
                    {
                        for (var u = 0; u < h; u++)
                        {
                            dst[(v * h) + u] = src[(u * w) + (w - 1 - v)];
                        }
                    }
                    return new GrayImage(dst, h, w);
                }
            default:
                return image;
        }
    }

    /// <summary>
    /// Turns a picture <paramref name="degrees"/> clockwise, so lines that rose by that much run level. The canvas
    /// grows to keep the corners, and the new space is white paper.
    /// </summary>
    internal static GrayImage Rotate(GrayImage image, double degrees)
    {
        var a = degrees * Math.PI / 180;
        double c = Math.Cos(a), s = Math.Sin(a);
        var (w, h, src) = (image.Width, image.Height, image.Pixels);
        var outW = (int)Math.Ceiling((w * Math.Abs(c)) + (h * Math.Abs(s)));
        var outH = (int)Math.Ceiling((w * Math.Abs(s)) + (h * Math.Abs(c)));
        var dst = new byte[(long)outW * outH];
        double cx = (w - 1) / 2.0, cy = (h - 1) / 2.0, ox = (outW - 1) / 2.0, oy = (outH - 1) / 2.0;
        for (var y = 0; y < outH; y++)
        {
            var v = y - oy;
            for (var x = 0; x < outW; x++)
            {
                // The output's level line v holds the source points on the tilted line t = v (see Project).
                var u = x - ox;
                var sx = cx + (u * c) + (v * s);
                var sy = cy - (u * s) + (v * c);
                dst[((long)y * outW) + x] = Sample(src, w, h, sx, sy);
            }
        }
        return new GrayImage(dst, outW, outH);
    }

    /// <summary>Bilinear sample; white outside the picture.</summary>
    private static byte Sample(byte[] src, int w, int h, double x, double y)
    {
        if (x <= -1 || y <= -1 || x >= w || y >= h)
        {
            return 255;
        }
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double At(int px, int py) => (uint)px < (uint)w && (uint)py < (uint)h ? src[(py * w) + px] : 255;
        var top = (At(x0, y0) * (1 - fx)) + (At(x0 + 1, y0) * fx);
        var bottom = (At(x0, y0 + 1) * (1 - fx)) + (At(x0 + 1, y0 + 1) * fx);
        return (byte)Math.Clamp(Math.Round((top * (1 - fy)) + (bottom * fy)), 0, 255);
    }

    /// <summary>Shrinks by a whole factor, averaging each block, until the longer side is at most <paramref name="side"/>.</summary>
    internal static (byte[] Pixels, int Width, int Height) Downsample(GrayImage image, int side)
    {
        var factor = Math.Max(1, (Math.Max(image.Width, image.Height) + side - 1) / side);
        if (factor == 1)
        {
            return (image.Pixels, image.Width, image.Height);
        }
        int w = image.Width / factor, h = image.Height / factor;
        var dst = new byte[w * h];
        var area = factor * factor;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var row = (((y * factor) + dy) * image.Width) + (x * factor);
                    for (var dx = 0; dx < factor; dx++)
                    {
                        sum += image.Pixels[row + dx];
                    }
                }
                dst[(y * w) + x] = (byte)(sum / area);
            }
        }
        return (dst, w, h);
    }

    /// <summary>Otsu's threshold: the gray level that best splits the picture into ink and paper.</summary>
    internal static int Otsu(byte[] pixels)
    {
        var histogram = new long[256];
        foreach (var p in pixels)
        {
            histogram[p]++;
        }
        double total = pixels.Length, sum = 0;
        for (var i = 0; i < 256; i++)
        {
            sum += i * (double)histogram[i];
        }
        double sumBelow = 0, below = 0, best = -1;
        var threshold = 128;
        for (var t = 0; t < 256; t++)
        {
            below += histogram[t];
            if (below == 0)
            {
                continue;
            }
            var above = total - below;
            if (above == 0)
            {
                break;
            }
            sumBelow += t * (double)histogram[t];
            var meanBelow = sumBelow / below;
            var meanAbove = (sum - sumBelow) / above;
            var between = below * above * (meanBelow - meanAbove) * (meanBelow - meanAbove);
            if (between > best)
            {
                (best, threshold) = (between, t + 1);
            }
        }
        return threshold;
    }
}
