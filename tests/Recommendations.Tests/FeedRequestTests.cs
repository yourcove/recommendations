using System.Text.Json;
using Recommendations.Abstractions;
using Recommendations.Core;
using Xunit;

namespace Recommendations.Tests;

/// <summary>
/// The request-parsing the feed endpoint does on the way in.
///
/// Unlike the fusion maths, this reads UNTRUSTED input: the score and cluster criteria arrive inside the object
/// filter that Cove's own filter dialog writes, including from saved filters that may predate the current shape.
/// The stated policy is that a malformed or stale criterion is SKIPPED rather than guessed at or fatal — a bad
/// saved filter must not break the page — so these tests pin the skipping as much as the parsing.
/// </summary>
public class FeedRequestTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static List<ScoreCriterion> Parse(string json)
        => RecommendationsCoreExtension.ParseScoreCriteria(Json(json));

    // ── Score criteria ───────────────────────────────────────────────────────

    [Fact]
    public void ReadsANumericScoreCriterion()
    {
        var criteria = Parse("""
            { "recScore_performers": { "value": 0.3, "modifier": "GREATER_THAN" } }
            """);

        var one = Assert.Single(criteria);
        Assert.Equal("performers", one.Key);
        Assert.Equal(ScoreComparison.GreaterThan, one.Comparison);
        Assert.Equal(0.3, one.Value);
        Assert.Null(one.Value2);
    }

    [Fact]
    public void ReadsARangeCriterion()
    {
        var criteria = Parse("""
            { "recScore_overall": { "value": 0.2, "value2": 0.8, "modifier": "BETWEEN" } }
            """);

        var one = Assert.Single(criteria);
        Assert.Equal(ScoreComparison.Between, one.Comparison);
        Assert.Equal(0.2, one.Value);
        Assert.Equal(0.8, one.Value2);
    }

    [Fact]
    public void IgnoresKeysThatAreNotScoreCriteria()
    {
        // The score criteria share the object filter with every one of Cove's own — the prefix is the only thing
        // separating them, and picking up a VideoFilter property here would be a filter the user never set.
        var criteria = Parse("""
            {
              "rating": { "value": 3, "modifier": "GREATER_THAN" },
              "recCluster": { "value": "2" },
              "recScore_audio": { "value": 0.1, "modifier": "GREATER_THAN" }
            }
            """);

        Assert.Equal("audio", Assert.Single(criteria).Key);
    }

    [Theory]
    [InlineData("GREATER_THAN", ScoreComparison.GreaterThan)]
    [InlineData("greaterThan", ScoreComparison.GreaterThan)]
    [InlineData("greater_than", ScoreComparison.GreaterThan)]
    [InlineData("LESS_THAN", ScoreComparison.LessThan)]
    [InlineData("EQUALS", ScoreComparison.Equals)]
    [InlineData("NOT_EQUALS", ScoreComparison.NotEquals)]
    [InlineData("NOT_BETWEEN", ScoreComparison.NotBetween)]
    public void AcceptsAnyCasingOrUnderscoringOfAModifier(string modifier, ScoreComparison expected)
    {
        // Cove's filter UI sends SCREAMING_SNAKE; hand-built and older saved filters use other forms.
        Assert.Equal(expected, RecommendationsCoreExtension.ParseComparison(modifier));
    }

    [Fact]
    public void AnAbsentModifierDefaultsToGreaterThan()
    {
        // Not the generic numeric EQUALS, which on a continuous score would match nothing.
        Assert.Equal(ScoreComparison.GreaterThan, RecommendationsCoreExtension.ParseComparison(null));
        Assert.Equal(ScoreComparison.GreaterThan, RecommendationsCoreExtension.ParseComparison(""));
    }

    [Fact]
    public void AnUnknownModifierIsRejectedRatherThanGuessed()
    {
        Assert.Null(RecommendationsCoreExtension.ParseComparison("INCLUDES_ALL"));
        Assert.Null(RecommendationsCoreExtension.ParseComparison("sounds_like"));
    }

    [Theory]
    // A modifier we can't honour — filtering on a guess is worse than not filtering.
    [InlineData("""{ "recScore_performers": { "value": 0.3, "modifier": "INCLUDES_ALL" } }""")]
    // Incomplete: no bound at all.
    [InlineData("""{ "recScore_performers": { "modifier": "GREATER_THAN" } }""")]
    // A range missing its upper bound.
    [InlineData("""{ "recScore_performers": { "value": 0.2, "modifier": "BETWEEN" } }""")]
    // Non-numeric bounds (a stale filter whose editor changed shape).
    [InlineData("""{ "recScore_performers": { "value": "0.3", "modifier": "GREATER_THAN" } }""")]
    // Not an object at all.
    [InlineData("""{ "recScore_performers": 0.3 }""")]
    // Prefix with nothing after it.
    [InlineData("""{ "recScore_": { "value": 0.3, "modifier": "GREATER_THAN" } }""")]
    public void SkipsCriteriaItCannotHonour(string objectFilter)
    {
        Assert.Empty(Parse(objectFilter));
    }

    [Fact]
    public void ABadCriterionDoesNotDiscardTheGoodOnesBesideIt()
    {
        var criteria = Parse("""
            {
              "recScore_performers": { "value": 0.3, "modifier": "NONSENSE" },
              "recScore_content": { "value": 0.5, "modifier": "LESS_THAN" }
            }
            """);

        Assert.Equal("content", Assert.Single(criteria).Key);
    }

    [Fact]
    public void NoObjectFilterMeansNoCriteria()
    {
        Assert.Empty(RecommendationsCoreExtension.ParseScoreCriteria(null));
        Assert.Empty(Parse("{}"));
        Assert.Empty(RecommendationsCoreExtension.ParseScoreCriteria(Json("[]")));
    }

    // ── Cluster criterion ────────────────────────────────────────────────────

    [Fact]
    public void ReadsTheTasteClusterCriterion()
    {
        Assert.Equal("3", RecommendationsCoreExtension.ParseClusterId(Json("""{ "recCluster": { "value": "3" } }""")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "recCluster": {} }""")]
    [InlineData("""{ "recCluster": { "value": "" } }""")]
    [InlineData("""{ "recCluster": { "value": 3 } }""")]
    [InlineData("""{ "recCluster": "3" }""")]
    public void AnUnusableClusterCriterionMeansNoScope(string objectFilter)
    {
        Assert.Null(RecommendationsCoreExtension.ParseClusterId(Json(objectFilter)));
    }

    // ── Per-user preference key ──────────────────────────────────────────────

    [Fact]
    public void PreferenceKeysAreNamespacedByUser()
    {
        // The extension key-value store is shared by every user on the server, so two users in the same context
        // must never resolve to the same key — one person's chosen recommender would become everyone's.
        var alice = RecommendationsCoreExtension.PreferenceKey(1, "globalfeed", "video");
        var bob = RecommendationsCoreExtension.PreferenceKey(2, "globalfeed", "video");

        Assert.NotEqual(alice, bob);
        Assert.Contains("1", alice.Split(':'));
        Assert.Contains("2", bob.Split(':'));
    }

    [Fact]
    public void PreferenceKeysSeparateContextAndEntityType()
    {
        var feed = RecommendationsCoreExtension.PreferenceKey(1, "globalfeed", "video");
        var similar = RecommendationsCoreExtension.PreferenceKey(1, "similar", "video");
        var images = RecommendationsCoreExtension.PreferenceKey(1, "globalfeed", "image");

        Assert.Equal(3, new[] { feed, similar, images }.Distinct().Count());
    }

    [Fact]
    public void PreferenceKeysAreCaseInsensitiveAndDefaulted()
    {
        Assert.Equal(
            RecommendationsCoreExtension.PreferenceKey(1, "GlobalFeed", "Video"),
            RecommendationsCoreExtension.PreferenceKey(1, "globalfeed", "video"));
        Assert.Equal(
            RecommendationsCoreExtension.PreferenceKey(1, null, null),
            RecommendationsCoreExtension.PreferenceKey(1, "globalfeed", "any"));
    }
}
