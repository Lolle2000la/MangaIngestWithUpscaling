namespace MangaIngestWithUpscaling.Shared.Services.Upscaling;

/// <summary>
/// Rejects archive entry names that could escape the output archive on extraction (absolute or
/// parent-traversing paths). Shared by the whole-CBZ writer and the streamed path so both agree on
/// which entries are carried into a produced CBZ.
/// </summary>
public static class ArchiveEntryNameGuard
{
    public static bool IsSafeEntryName(string name) =>
        !string.IsNullOrEmpty(name)
        && !Path.IsPathRooted(name)
        // Reject Windows drive/UNC forms independent of the host OS: on Linux Path.IsPathRooted
        // returns false for "C:\evil.jpg" and "\\server\share\evil.jpg". Only a drive prefix is
        // rejected, not a colon anywhere: a colon is legal on Linux and rejecting it silently dropped
        // legitimate pages (the chapter was still reported upscaled). A drive-relative "C:evil.jpg"
        // or an NTFS alternate-data-stream name is left to third-party Windows extractors, which the
        // server itself never uses.
        && !name.StartsWith('/')
        && !name.StartsWith('\\')
        && !IsWindowsDriveForm(name)
        && !name.Split('/', '\\').Any(IsUnsafeSegment);

    /// <summary>
    ///     True for a Windows absolute drive prefix ("C:\", "C:/"), which must not be interpreted as a
    ///     drive path even when the host is Linux. A drive-relative "C:evil" and a legal Linux name like
    ///     "a:b.jpg" are not rejected: dropping them silently lost pages.
    /// </summary>
    private static bool IsWindowsDriveForm(string name) =>
        name.Length >= 3
        && char.IsAsciiLetter(name[0])
        && name[1] == ':'
        && (name[2] == '/' || name[2] == '\\');

    /// <summary>
    /// True for a path component that is a parent traversal. Windows removes trailing spaces and dots
    /// from a component, so ".. " normalizes to ".." there. A "." component and an empty component
    /// (from "a//b") collapse harmlessly and must be preserved: rejecting them silently dropped pages
    /// while the chapter was still reported upscaled.
    /// </summary>
    private static bool IsUnsafeSegment(string segment)
    {
        // Anything with a non-dot/space character is an ordinary name.
        if (segment.TrimEnd(' ', '.').Length > 0)
        {
            return false;
        }

        // Only dots and spaces: a parent traversal when it has at least two dots ("..", ".. ", "...").
        return segment.Count(c => c == '.') >= 2;
    }
}
