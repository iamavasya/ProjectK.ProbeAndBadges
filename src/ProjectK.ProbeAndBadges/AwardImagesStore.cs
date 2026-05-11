using System.IO;
using System.Reflection;
using ProjectK.ProbeAndBadges.Abstractions;

namespace ProjectK.ProbeAndBadges;

public sealed class AwardImagesStore : IAwardImagesStore
{
    private const string BaseResourcePath = "ProjectK.ProbeAndBadges.Data.awards_images";

    public Stream? GetAwardImageStream(int level, bool isColored)
    {
        var suffix = isColored ? "" : "-bw";
        var resourceName = $"{BaseResourcePath}.award{level}{suffix}.png";
        return typeof(AwardImagesStore).Assembly.GetManifestResourceStream(resourceName);
    }
}
