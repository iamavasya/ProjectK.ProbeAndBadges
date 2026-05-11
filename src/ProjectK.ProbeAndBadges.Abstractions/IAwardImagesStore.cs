using System.IO;

namespace ProjectK.ProbeAndBadges.Abstractions;

public interface IAwardImagesStore
{
    Stream? GetAwardImageStream(int level, bool isColored);
}
