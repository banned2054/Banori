using System.Globalization;

namespace MediaManager.Core.Rules;

public sealed class ReleaseNamePreviewPolicy
{
    private static readonly char[] InvalidFileNameCharacters = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public string Create(string subjectName, decimal episode, int version, string preference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectName);

        var safeSubjectName = new string(subjectName.Trim()
                                                    .Select(character =>
                                                                InvalidFileNameCharacters.Contains(character)
                                                                    ? ' '
                                                                    : character)
                                                    .ToArray());
        safeSubjectName = string.Join(' ', safeSubjectName.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var episodeText = episode.ToString(episode == decimal.Truncate(episode) ? "00" : "00.###",
                                           CultureInfo.InvariantCulture);
        var versionText    = version > 1 ? $" v{version}" : string.Empty;
        var preferenceText = string.IsNullOrWhiteSpace(preference) ? string.Empty : $" [{preference}]";

        return $"{safeSubjectName} E{episodeText}{versionText}{preferenceText}";
    }
}
