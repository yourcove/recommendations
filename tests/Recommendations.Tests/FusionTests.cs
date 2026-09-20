using Recommendations.Tastes;
using Xunit;

namespace Recommendations.Tests;

/// <summary>
/// The signal-fusion contract from <c>docs/FUSION_DESIGN.md</c>, pinned.
///
/// This maths decides every ranking the full taste model produces, and it is the part of the system where a
/// regression is completely silent: the page still loads, the feed still fills, the ordering is just quietly
/// worse. The design doc states its requirements as properties (R1–R10, S1–S4) rather than as expected outputs,
/// so the tests do too — they assert the property, not a golden number, and stay meaningful when the constants
/// are retuned.
/// </summary>
public class FusionTests
{
    private const double Tolerance = 1e-9;

    /// <summary>A signal as Stage 1 takes it: (lever k, confidence c, calibrated value d).</summary>
    private static (double k, double c, double d) Sig(double k, double c, double d) => (k, c, d);

    private static NicheRecommender.AspectBelief Fuse(params (double k, double c, double d)[] sigs)
        => NicheRecommender.FuseAspect(sigs);

    // ── Stage 1: within an aspect ────────────────────────────────────────────

    [Fact]
    public void R6_MissingSignalLeavesTheValueBitIdentical()
    {
        // "Missing (c=0) ⇒ bit-identical score, lower confidence only."
        var withoutIt = Fuse(Sig(1.0, 0.8, 0.6), Sig(1.0, 0.7, 0.4));
        var withIt = Fuse(Sig(1.0, 0.8, 0.6), Sig(1.0, 0.7, 0.4), Sig(1.0, 0.0, -0.9));

        Assert.Equal(withoutIt.Value, withIt.Value);          // bit-identical, not merely close
        Assert.True(withIt.Conf < withoutIt.Conf, "a signal present but unmeasured must lower confidence");
    }

    [Fact]
    public void R4_JunkConfidenceMovesAConfidentConsensusByAboutOnePercent()
    {
        // "c² makes junk (c≈0.1) shift a confident consensus by ~1%."
        var clean = Fuse(Sig(1.0, 0.9, 0.8));
        var polluted = Fuse(Sig(1.0, 0.9, 0.8), Sig(1.0, 0.1, -1.0));

        var shift = Math.Abs(polluted.Value - clean.Value);
        Assert.True(shift < 0.05, $"junk shifted a confident consensus by {shift:P1}; c² weighting is not doing its job");
    }

    [Fact]
    public void R9_LeverZeroRemovesASignalEntirely()
    {
        // "Lever 0 = removed" — not down-weighted, and not contributing to coverage either.
        var absent = Fuse(Sig(1.0, 0.9, 0.5));
        var zeroLevered = Fuse(Sig(1.0, 0.9, 0.5), Sig(0.0, 1.0, -1.0));

        Assert.Equal(absent.Value, zeroLevered.Value);
        Assert.Equal(absent.Coverage, zeroLevered.Coverage, Tolerance);
        Assert.Equal(absent.Conf, zeroLevered.Conf, Tolerance);
    }

    [Fact]
    public void S4_ALoneSignalCarriesFully()
    {
        // The novel-performer case the doc calls out by name: "face 0.9 alone ⇒ Performers 0.9, not 0.77/0.40".
        // A sub-signal with no data must not drag its aspect toward neutral — that is what coverage is for.
        var belief = Fuse(Sig(1.0, 0.9, 0.9), Sig(1.0, 0.0, 0.0));

        Assert.Equal(0.9, belief.Value, Tolerance);
    }

    [Fact]
    public void S4_SubSignalsWithinAnAspectPoolTowardConsensus()
    {
        // "a confident meh affinity DOES pull a strong face-match to an intermediate value" — within an aspect,
        // estimators of the same latent average rather than each speaking independently.
        var belief = Fuse(Sig(1.0, 0.9, 0.9), Sig(1.0, 0.9, 0.0));

        Assert.InRange(belief.Value, 0.05, 0.85);
    }

    [Fact]
    public void R8_WithinAspectDisagreementLowersConfidence()
    {
        var agreeing = Fuse(Sig(1.0, 0.8, 0.5), Sig(1.0, 0.8, 0.5));
        var disagreeing = Fuse(Sig(1.0, 0.8, 1.0), Sig(1.0, 0.8, -1.0));

        Assert.Equal(agreeing.Coverage, disagreeing.Coverage, Tolerance);   // same evidence…
        Assert.True(disagreeing.Conf < agreeing.Conf, "…but conflicting estimates must read as less certain");
    }

    [Fact]
    public void R8_DisagreementIsJunkImmune()
    {
        // "a c=0.05 outlier moves δ by ~0.01, not 0.28" — otherwise one junk estimator could tank the confidence
        // of an otherwise unanimous aspect.
        var unanimous = Fuse(Sig(1.0, 0.9, 0.7), Sig(1.0, 0.9, 0.7));
        var withOutlier = Fuse(Sig(1.0, 0.9, 0.7), Sig(1.0, 0.9, 0.7), Sig(1.0, 0.05, -1.0));

        // Coverage legitimately drops (a third signal read almost nothing); the DISAGREEMENT term must not.
        var disagreementDrop = (unanimous.Conf / unanimous.Coverage) - (withOutlier.Conf / withOutlier.Coverage);
        Assert.True(disagreementDrop < 0.05, $"a junk outlier moved the disagreement term by {disagreementDrop:F3}");
    }

    [Fact]
    public void NoSignalsMeansNeutralAndUnconfident()
    {
        var belief = Fuse();

        Assert.Equal(0, belief.Value);
        Assert.Equal(0, belief.Conf);
    }

    [Fact]
    public void ValueStaysWithinTheRangeOfItsInputs()
    {
        // A weighted mean of present signals can never leave their span — the property that makes "value" safe to
        // display as a calibrated d ∈ [-1, 1].
        var belief = Fuse(Sig(1.0, 0.9, -0.4), Sig(2.0, 0.3, 0.2), Sig(0.5, 0.7, 0.9));

        Assert.InRange(belief.Value, -0.4, 0.9);
    }

    [Fact]
    public void CoverageIsTheLeverWeightedMeanConfidence()
    {
        // coverage_a = Σ kᵢ·cᵢ / Σ kᵢ, over configured levers — including the ones that read nothing.
        var belief = Fuse(Sig(1.0, 1.0, 0.5), Sig(3.0, 0.0, 0.0));

        Assert.Equal(0.25, belief.Coverage, Tolerance);
    }

    // ── Stage 2: the dead band ───────────────────────────────────────────────

    [Fact]
    public void S1_NearTypicalAspectsAreSilent()
    {
        // "an aspect measured *near-typical* is SILENT, same as unmeasured."
        Assert.Equal(0, NicheRecommender.DeadBanded(0));
        Assert.Equal(0, NicheRecommender.DeadBanded(0.2));
        Assert.Equal(0, NicheRecommender.DeadBanded(-0.2));
        Assert.True(NicheRecommender.DeadBanded(0.5) > 0);
        Assert.True(NicheRecommender.DeadBanded(-0.5) < 0);
    }

    [Fact]
    public void DeadBandIsOddSymmetric()
    {
        // The odd symmetry is what makes presence EXACTLY mean-neutral (R3) rather than approximately so.
        foreach (var v in new[] { 0.05, 0.25, 0.3, 0.6, 0.99, 1.0 })
            Assert.Equal(-NicheRecommender.DeadBanded(v), NicheRecommender.DeadBanded(-v), Tolerance);
    }

    [Fact]
    public void DeadBandIsMonotoneAndReachesFullScale()
    {
        var previous = double.NegativeInfinity;
        for (var v = -1.0; v <= 1.0001; v += 0.01)
        {
            var e = NicheRecommender.DeadBanded(v);
            Assert.True(e >= previous - Tolerance, $"dead band is not monotone at v={v:F2}");
            previous = e;
        }
        Assert.Equal(1.0, NicheRecommender.DeadBanded(1.0), Tolerance);
    }

    [Fact]
    public void SufficiencyGateClampsAndSaturates()
    {
        // "confidence yes, score no": the gate saturates rather than scaling influence with coverage forever.
        Assert.Equal(0, NicheRecommender.Smoothstep(-5));
        Assert.Equal(1, NicheRecommender.Smoothstep(5));
        Assert.Equal(0.5, NicheRecommender.Smoothstep(0.5), Tolerance);
        Assert.True(NicheRecommender.Smoothstep(0.2) < 0.2, "smoothstep must ease in — a junk-only aspect stays near-silent");
    }

    // ── Stage 2/3: across aspects ────────────────────────────────────────────

    private static NicheRecommender.AspectBelief Belief(double value, double coverage, double conf)
        => new(value, coverage, conf);

    [Fact]
    public void R3_SilentAspectsChangeTheKeyBitForBit()
    {
        // "identity at zero (missing/silent aspects change nothing bit-for-bit)" — this is the property that keeps
        // data-rich videos from being systematically advantaged just for having coverage.
        var one = NicheRecommender.FuseOverall([(1.0, Belief(0.8, 0.9, 0.9))]);
        var withSilent = NicheRecommender.FuseOverall(
        [
            (1.0, Belief(0.8, 0.9, 0.9)),
            (1.0, Belief(0.0, 0.9, 0.9)),     // measured, and exactly typical
            (1.0, Belief(0.1, 0.0, 0.0)),     // not measured at all
        ]);

        Assert.Equal(one.key, withSilent.key);
    }

    [Fact]
    public void KeyIsZeroWhenEveryAspectIsSilent()
    {
        var (key, _) = NicheRecommender.FuseOverall(
        [
            (1.0, Belief(0.0, 1.0, 1.0)),
            (1.0, Belief(0.1, 1.0, 1.0)),
            (1.0, Belief(-0.2, 1.0, 1.0)),
        ]);

        Assert.Equal(0, key);
    }

    [Fact]
    public void R2_AWeakPositiveNeverDragsAStrongPositiveDown()
    {
        // The vetoed-averaging requirement: adding a second, milder piece of good news must not make a video
        // rank lower than it did on the strong signal alone.
        var strongAlone = NicheRecommender.FuseOverall([(1.0, Belief(0.95, 1.0, 1.0))]);
        var strongPlusWeak = NicheRecommender.FuseOverall(
        [
            (1.0, Belief(0.95, 1.0, 1.0)),
            (1.0, Belief(0.35, 1.0, 1.0)),
        ]);

        Assert.True(strongPlusWeak.key >= strongAlone.key,
            $"a weak positive pulled the key down: {strongAlone.key:F4} → {strongPlusWeak.key:F4}");
    }

    [Fact]
    public void R5_ConfidentNegativesBite()
    {
        var good = NicheRecommender.FuseOverall([(1.0, Belief(0.9, 1.0, 1.0))]);
        var goodButOneBadAspect = NicheRecommender.FuseOverall(
        [
            (1.0, Belief(0.9, 1.0, 1.0)),
            (1.0, Belief(-0.9, 1.0, 1.0)),
        ]);

        Assert.True(goodButOneBadAspect.key < good.key);
        Assert.Equal(0, goodButOneBadAspect.key, Tolerance);   // symmetric up/down: they cancel exactly
    }

    [Fact]
    public void R10_TheKeyIsMonotoneInEachAspectValue()
    {
        // The property the design panel's 400k-trial search was checking: raising one aspect can never lower K.
        var previous = double.NegativeInfinity;
        for (var v = -1.0; v <= 1.0001; v += 0.02)
        {
            var (key, _) = NicheRecommender.FuseOverall(
            [
                (1.0, Belief(v, 0.9, 0.9)),
                (0.7, Belief(0.4, 0.8, 0.8)),
                (0.3, Belief(-0.6, 0.5, 0.5)),
            ]);
            Assert.True(key >= previous - Tolerance, $"key is not monotone in aspect value at v={v:F2}");
            previous = key;
        }
    }

    [Fact]
    public void R9_LeversScaleInfluenceMonotonically()
    {
        var previous = double.NegativeInfinity;
        foreach (var lever in new[] { 0.0, 0.1, 0.25, 0.5, 0.75, 1.0, 1.5, 2.0 })
        {
            var (key, _) = NicheRecommender.FuseOverall([(lever, Belief(0.9, 1.0, 1.0))]);
            Assert.True(key >= previous - Tolerance, $"raising the lever to {lever} did not raise influence");
            previous = key;
        }
        Assert.Equal(0, NicheRecommender.FuseOverall([(0.0, Belief(0.9, 1.0, 1.0))]).key);
    }

    [Fact]
    public void R3_LowCoverageWeakensTheScoreButNotTheDirection()
    {
        var confident = NicheRecommender.FuseOverall([(1.0, Belief(0.9, 1.0, 1.0))]);
        var thin = NicheRecommender.FuseOverall([(1.0, Belief(0.9, 0.05, 0.05))]);

        Assert.True(thin.key > 0, "a thin reading should still point the right way");
        Assert.True(thin.key < confident.key * 0.25, "a junk-only aspect must barely move the key");
    }

    [Fact]
    public void S3_MixedButCertainStaysHighlyConfident()
    {
        // "great content + terrible audio ⇒ HIGH overall confidence (a certain conclusion; the training grounds
        // must NOT waste probes on it)." Cross-aspect conflict is tension, not uncertainty.
        var (_, conf) = NicheRecommender.FuseOverall(
        [
            (1.0, Belief(0.95, 1.0, 0.95)),
            (1.0, Belief(-0.95, 1.0, 0.95)),
        ]);

        Assert.True(conf > 0.9, $"mixed-but-certain read as uncertain (conf {conf:F2})");
    }

    [Fact]
    public void OverallConfidenceIsLeverWeightedAndIgnoresRemovedAspects()
    {
        var (_, conf) = NicheRecommender.FuseOverall(
        [
            (1.0, Belief(0.5, 1.0, 1.0)),
            (3.0, Belief(0.5, 0.0, 0.0)),
            (0.0, Belief(0.5, 1.0, 1.0)),   // lever 0 — removed, so it must not count either way
        ]);

        Assert.Equal(0.25, conf, Tolerance);
    }

    [Fact]
    public void NoAspectsMeansNeutralAndUnconfident()
    {
        var (key, conf) = NicheRecommender.FuseOverall([]);

        Assert.Equal(0, key);
        Assert.Equal(0, conf);
    }

    [Fact]
    public void ExtremeBeliefsStayFinite()
    {
        // u is clipped to ±0.98 before atanh precisely so a saturated aspect can't produce an infinite key and
        // poison the sort.
        var (key, _) = NicheRecommender.FuseOverall(
        [
            (4.0, Belief(1.0, 1.0, 1.0)),
            (4.0, Belief(1.0, 1.0, 1.0)),
        ]);

        Assert.True(double.IsFinite(key), "a saturated aspect produced a non-finite ranking key");
    }
}
