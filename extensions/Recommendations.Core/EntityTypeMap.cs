using Cove.Core.Entities;

namespace Recommendations.Core;

/// <summary>
/// Translates between the contract's lowercase entity-type strings (e.g. "video") and Cove's three
/// parallel host-type enums, which use different numeric values for the same logical type.
/// </summary>
internal static class EntityTypeMap
{
    public static bool TryAffinity(string entityType, out AffinityHostType host)
    {
        host = entityType.ToLowerInvariant() switch
        {
            "video" => AffinityHostType.Video,
            "image" => AffinityHostType.Image,
            "performer" => AffinityHostType.Performer,
            "face" => AffinityHostType.Face,
            "tag" => AffinityHostType.Tag,
            "studio" => AffinityHostType.Studio,
            "gallery" => AffinityHostType.Gallery,
            "group" => AffinityHostType.Group,
            "audio" => AffinityHostType.Audio,
            "text" => AffinityHostType.Text,
            "segment" => AffinityHostType.Segment,
            _ => (AffinityHostType)0,
        };
        return host != 0;
    }

    public static bool TryRating(string entityType, out RatingHostType host)
    {
        host = entityType.ToLowerInvariant() switch
        {
            "video" => RatingHostType.Video,
            "image" => RatingHostType.Image,
            "performer" => RatingHostType.Performer,
            "segment" => RatingHostType.Segment,
            "face" => RatingHostType.Face,
            "tag" => RatingHostType.Tag,
            "studio" => RatingHostType.Studio,
            "gallery" => RatingHostType.Gallery,
            "group" => RatingHostType.Group,
            "audio" => RatingHostType.Audio,
            "text" => RatingHostType.Text,
            _ => (RatingHostType)0,
        };
        return host != 0;
    }

    public static string ToEntityType(AffinityHostType host) => host switch
    {
        AffinityHostType.Video => "video",
        AffinityHostType.Image => "image",
        AffinityHostType.Performer => "performer",
        AffinityHostType.Face => "face",
        AffinityHostType.Tag => "tag",
        AffinityHostType.Studio => "studio",
        AffinityHostType.Gallery => "gallery",
        AffinityHostType.Group => "group",
        AffinityHostType.Audio => "audio",
        AffinityHostType.Text => "text",
        AffinityHostType.Segment => "segment",
        _ => "unknown",
    };

    /// <summary>Convert a rating host-type to the matching affinity host-type (by logical type, not value).</summary>
    public static AffinityHostType RatingToAffinity(RatingHostType host) => host switch
    {
        RatingHostType.Video => AffinityHostType.Video,
        RatingHostType.Image => AffinityHostType.Image,
        RatingHostType.Performer => AffinityHostType.Performer,
        RatingHostType.Segment => AffinityHostType.Segment,
        RatingHostType.Face => AffinityHostType.Face,
        RatingHostType.Tag => AffinityHostType.Tag,
        RatingHostType.Studio => AffinityHostType.Studio,
        RatingHostType.Gallery => AffinityHostType.Gallery,
        RatingHostType.Group => AffinityHostType.Group,
        RatingHostType.Audio => AffinityHostType.Audio,
        RatingHostType.Text => AffinityHostType.Text,
        _ => (AffinityHostType)0,
    };
}
