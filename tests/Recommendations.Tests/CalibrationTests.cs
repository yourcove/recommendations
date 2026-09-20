using Recommendations.Tastes;
using Xunit;

namespace Recommendations.Tests;

/// <summary>
/// Stage 0 calibration and the statistics the self-evaluation report is built from.
///
/// Calibration is what makes a score mean "vs your typical" rather than anything absolute, so a bug here doesn't
/// look like a bug — it looks like the model having odd taste. The isotonic fit in particular has a hand-rolled
/// pool-adjacent-violators implementation, which is the kind of code that is either right or subtly wrong.
/// </summary>
public class CalibrationTests
{
    private const double Tolerance = 1e-9;

    // ── Isotonic (learned enjoyment-shape) curves ────────────────────────────

    [Fact]
    public void IsotonicFitIsNonDecreasing()
    {
        // The whole point of the fit: whatever the data does, the curve it produces must be monotone, because it
        // is used to map a signal percentile onto preference units.
        var noisy = new List<(double x, double y)>
        {
            (0.0, 0.2), (0.1, -0.5), (0.2, 0.4), (0.3, 0.1), (0.4, 0.9),
            (0.5, 0.3), (0.6, 0.7), (0.7, 0.6), (0.8, 1.0), (0.9, 0.8), (1.0, 0.95),
        };

        var grid = NicheRecommender.FitIsotonicGrid(noisy, 32);

        for (var i = 1; i < grid.Length; i++)
            Assert.True(grid[i] >= grid[i - 1] - Tolerance, $"isotonic grid decreased at index {i}");
    }

    [Fact]
    public void IsotonicFitPreservesAlreadyMonotoneData()
    {
        var clean = new List<(double x, double y)>
        {
            (0.0, -1.0), (0.25, -0.5), (0.5, 0.0), (0.75, 0.5), (1.0, 1.0),
        };

        var grid = NicheRecommender.FitIsotonicGrid(clean, 5);

        Assert.Equal(-1.0, grid[0], 1e-6);
        Assert.Equal(1.0, grid[^1], 1e-6);
        Assert.Equal(0.0, grid[2], 1e-6);
    }

    [Fact]
    public void IsotonicFitPoolsViolatorsIntoTheirMean()
    {
        // Two points in the wrong order collapse to their average rather than either value.
        var inverted = new List<(double x, double y)> { (0.0, 1.0), (1.0, 0.0) };

        var grid = NicheRecommender.FitIsotonicGrid(inverted, 3);

        Assert.All(grid, v => Assert.Equal(0.5, v, 1e-6));
    }

    [Fact]
    public void GridInterpolationIsLinearAndClamped()
    {
        double[] grid = [0.0, 1.0, 2.0];

        Assert.Equal(0.0, NicheRecommender.InterpGrid(grid, 0.0), Tolerance);
        Assert.Equal(1.0, NicheRecommender.InterpGrid(grid, 0.5), Tolerance);
        Assert.Equal(2.0, NicheRecommender.InterpGrid(grid, 1.0), Tolerance);
        Assert.Equal(0.5, NicheRecommender.InterpGrid(grid, 0.25), Tolerance);
        // Out-of-range percentiles clamp rather than extrapolating off the end of the learned curve.
        Assert.Equal(0.0, NicheRecommender.InterpGrid(grid, -3), Tolerance);
        Assert.Equal(2.0, NicheRecommender.InterpGrid(grid, 7), Tolerance);
    }

    [Fact]
    public void GridInterpolationHandlesDegenerateGrids()
    {
        Assert.Equal(0, NicheRecommender.InterpGrid([], 0.5));
        Assert.Equal(4.0, NicheRecommender.InterpGrid([4.0], 0.5), Tolerance);
    }

    [Fact]
    public void DeadbandCenterMakesTheLibraryMeanNeutral()
    {
        // The centre is chosen so the dead-banded curve averages to zero across the library — that is what keeps
        // "having data" from being worth points (R3). Verify the property directly rather than the number.
        double[] skewed = [0.1, 0.3, 0.45, 0.6, 0.7, 0.8, 0.85, 0.9, 0.95, 1.0];

        var centre = NicheRecommender.CenterForDeadband(skewed);
        var mean = skewed.Select(v => NicheRecommender.DeadBanded(v - centre)).Average();

        Assert.Equal(0, mean, 1e-3);
    }

    // ── Objective video quality ──────────────────────────────────────────────

    [Fact]
    public void ObjectiveQualityRisesWithResolutionAndBitrate()
    {
        var sd = NicheRecommender.ObjectiveQuality(480, 1_000_000);
        var hd = NicheRecommender.ObjectiveQuality(1080, 1_000_000);
        var hdFatter = NicheRecommender.ObjectiveQuality(1080, 8_000_000);

        Assert.True(hd.raw > sd.raw);
        Assert.True(hdFatter.raw > hd.raw);
    }

    [Fact]
    public void ObjectiveQualityReportsLowerConfidenceFromOneMeasurement()
    {
        var both = NicheRecommender.ObjectiveQuality(1080, 5_000_000);
        var heightOnly = NicheRecommender.ObjectiveQuality(1080, 0);
        var bitrateOnly = NicheRecommender.ObjectiveQuality(0, 5_000_000);
        var neither = NicheRecommender.ObjectiveQuality(0, 0);

        Assert.True(both.conf > heightOnly.conf);
        Assert.Equal(heightOnly.conf, bitrateOnly.conf);
        Assert.Equal(0, neither.conf);
        Assert.Equal(0, neither.raw);   // and unknown reads as neutral, never as bad
    }

    [Fact]
    public void ObjectiveQualityStaysInUnitRangeAcrossPlausibleMedia()
    {
        foreach (var (h, b) in new[] { (240, 200_000L), (720, 2_000_000L), (2160, 40_000_000L), (4320, 120_000_000L) })
        {
            var (raw, _) = NicheRecommender.ObjectiveQuality(h, b);
            Assert.InRange(raw, 0.0, 1.0);
        }
    }

    // ── Rank statistics behind the self-evaluation report ────────────────────

    [Fact]
    public void SpearmanDetectsPerfectAgreementAndInversion()
    {
        double[] a = [1, 2, 3, 4, 5];
        double[] ascending = [10, 20, 30, 40, 50];
        double[] descending = [50, 40, 30, 20, 10];

        Assert.Equal(1.0, NicheRecommender.Spearman(a, ascending), 1e-9);
        Assert.Equal(-1.0, NicheRecommender.Spearman(a, descending), 1e-9);
    }

    [Fact]
    public void SpearmanIsNotANumberWhenItCannotBeComputed()
    {
        // The eval report renders these as "—" rather than as 0, so the distinction has to survive.
        Assert.True(double.IsNaN(NicheRecommender.Spearman([1, 2], [1, 2])));              // too few points
        Assert.True(double.IsNaN(NicheRecommender.Spearman([1, 2, 3], [1, 2])));           // mismatched lengths
        Assert.True(double.IsNaN(NicheRecommender.Spearman([1, 2, 3], [7, 7, 7])));        // no variance to correlate
    }

    [Fact]
    public void RanksAreAveragedAcrossTies()
    {
        double[] withTies = [5, 1, 5, 3];

        var ranks = NicheRecommender.RankArray(withTies);

        Assert.Equal(0, ranks[1], Tolerance);            // 1 → lowest
        Assert.Equal(1, ranks[3], Tolerance);            // 3 → next
        Assert.Equal(2.5, ranks[0], Tolerance);          // the two 5s share ranks 2 and 3
        Assert.Equal(2.5, ranks[2], Tolerance);
    }

    [Fact]
    public void StandardDeviationIsPopulationAndSafeOnShortInputs()
    {
        Assert.Equal(0, NicheRecommender.Std([]));
        Assert.Equal(0, NicheRecommender.Std([42]));
        Assert.Equal(0, NicheRecommender.Std([3, 3, 3, 3]), Tolerance);
        Assert.Equal(2.0, NicheRecommender.Std([2, 4, 4, 4, 5, 5, 7, 9]), Tolerance);
    }
}
