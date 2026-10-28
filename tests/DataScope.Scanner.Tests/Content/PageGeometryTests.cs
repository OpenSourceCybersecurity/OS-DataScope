using DataScope.Scanner.Content.Ocr;

namespace DataScope.Scanner.Tests.Content;

/// <summary>
/// Finding how a scanned page lies (D131), on pages drawn here: rows of blocks the size of letters, some with
/// ascenders and fewer with descenders, as in English text. No text recognition is involved, so these run the same
/// on every platform.
/// </summary>
public sealed class PageGeometryTests
{
    /// <summary>A letter page of made-up "text": 30 lines of words, ascenders three times as common as descenders.</summary>
    private static GrayImage TextPage(int lines = 30, int width = 1200, int height = 1600)
    {
        var random = new Random(7);
        var pixels = new byte[width * height];
        Array.Fill(pixels, (byte)255);
        void Ink(int x0, int y0, int w, int h)
        {
            for (var y = y0; y < y0 + h; y++)
            {
                for (var x = x0; x < x0 + w; x++)
                {
                    pixels[(y * width) + x] = 20;
                }
            }
        }
        for (var line = 0; line < lines; line++)
        {
            var baseline = 140 + (line * 44);
            var x = 100;
            while (x < width - 140)
            {
                var letters = random.Next(2, 9);
                for (var l = 0; l < letters; l++)
                {
                    Ink(x, baseline - 14, 9, 14); // The height of x.
                    var odds = random.Next(10);
                    if (odds < 3)
                    {
                        Ink(x, baseline - 24, 3, 10); // An ascender.
                    }
                    else if (odds == 3)
                    {
                        Ink(x + 6, baseline, 3, 8); // A descender.
                    }
                    x += 12;
                }
                x += 14; // Between words.
            }
        }
        return new GrayImage(pixels, width, height);
    }

    [Fact]
    public void An_upright_level_page_is_read_as_it_is()
    {
        var layout = PageGeometry.Find(TextPage());
        Assert.True(layout.TextLike);
        Assert.Equal(PageTurn.None, layout.Best);
        // Should the first reading be poor, the next try is upside down.
        Assert.Equal(new PageTurn(2, 0), layout.Tries[1]);
    }

    [Theory]
    [InlineData(1, 3)] // Fed in turned a quarter clockwise: turned back three quarters.
    [InlineData(2, 2)] // Upside down.
    [InlineData(3, 1)]
    public void A_page_turned_a_quarter_or_upside_down_is_turned_back(int turnedBy, int expected)
    {
        var layout = PageGeometry.Find(PageGeometry.Turn(TextPage(), turnedBy));
        Assert.True(layout.TextLike);
        Assert.Equal(new PageTurn(expected, 0), layout.Best);
    }

    [Theory]
    [InlineData(-4.0)] // Lines rising 4° to the right.
    [InlineData(-7.5)]
    [InlineData(2.0)] // Lines falling.
    [InlineData(12.0)]
    public void A_tilted_page_is_levelled(double turnedClockwise)
    {
        var layout = PageGeometry.Find(PageGeometry.Rotate(TextPage(), turnedClockwise));
        Assert.Equal(0, layout.Best.QuarterTurns);
        // Levelling turns it back the other way, to within a few tenths of a degree.
        Assert.InRange(layout.Best.Skew, -turnedClockwise - 0.3, -turnedClockwise + 0.3);
    }

    [Fact]
    public void A_sideways_tilted_page_is_turned_and_levelled()
    {
        var layout = PageGeometry.Find(PageGeometry.Rotate(PageGeometry.Turn(TextPage(), 1), 3));
        Assert.Equal(3, layout.Best.QuarterTurns);
        Assert.InRange(layout.Best.Skew, -3.3, -2.7);
    }

    [Fact]
    public void A_slight_tilt_is_left_alone()
    {
        // Under MinSkew, turning would soften the letters for no gain.
        var layout = PageGeometry.Find(PageGeometry.Rotate(TextPage(), 0.2));
        Assert.Equal(PageTurn.None, layout.Best);
    }

    [Fact]
    public void A_single_line_is_read_across_not_down()
    {
        // One line has no space between lines, but words with space between them.
        var line = TextPage(lines: 1, width: 900, height: 200);
        Assert.Equal(PageTurn.None, PageGeometry.Find(line).Best);
        Assert.Equal(3, PageGeometry.Find(PageGeometry.Turn(line, 1)).Best.QuarterTurns);
    }

    [Fact]
    public void Pictures_that_are_not_text_are_read_once_as_they_are()
    {
        var blank = new GrayImage(Enumerable.Repeat((byte)255, 800 * 600).ToArray(), 800, 600);
        var random = new Random(3);
        var noise = new GrayImage(Enumerable.Range(0, 800 * 600).Select(_ => (byte)random.Next(256)).ToArray(), 800, 600);
        var gradient = new GrayImage(Enumerable.Range(0, 800 * 600).Select(i => (byte)(i % 800 * 255 / 800)).ToArray(), 800, 600);
        foreach (var picture in new[] { blank, noise, gradient })
        {
            var layout = PageGeometry.Find(picture);
            Assert.False(layout.TextLike);
            Assert.Equal([PageTurn.None], layout.Tries);
        }
    }

    [Fact]
    public void Quarter_turns_are_exact()
    {
        var page = PageGeometry.Turn(TextPage(lines: 3, width: 300, height: 260), 0);
        var once = PageGeometry.Turn(page, 1);
        Assert.Equal((260, 300), (once.Width, once.Height));
        Assert.Equal(page.Pixels, PageGeometry.Turn(once, 3).Pixels);
        Assert.Equal(page.Pixels, PageGeometry.Turn(PageGeometry.Turn(page, 2), 2).Pixels);
        // The top-left corner goes to the top right on a clockwise quarter turn.
        var marked = (byte[])page.Pixels.Clone();
        marked[0] = 1;
        Assert.Equal(1, PageGeometry.Turn(page with { Pixels = marked }, 1).Pixels[259]);
    }

    [Fact]
    public void Levelling_grows_the_canvas_and_fills_it_with_paper()
    {
        var page = TextPage(lines: 3, width: 400, height: 300);
        var turned = PageGeometry.Rotate(page, 10);
        Assert.True(turned.Width > 400 && turned.Height > 300);
        Assert.Equal(255, turned.Pixels[0]); // A new corner is white, so it reads as paper, not ink.
        Assert.Same(page, PageGeometry.Straighten(page, PageTurn.None));
    }

    [Fact]
    public void Large_pictures_are_judged_at_a_bounded_size()
    {
        var (_, width, height) = PageGeometry.Downsample(new GrayImage(new byte[4000 * 3000], 4000, 3000), PageGeometry.SampleSide);
        Assert.True(width <= PageGeometry.SampleSide && height <= PageGeometry.SampleSide);
    }
}
