using Velopack;
using Velopack.Sources;

namespace DLS;

/// <summary>Finds updates in the public, binary-only release repository.</summary>
internal static class UpdateService
{
    public const string RepositoryUrl =
        "https://github.com/TaterOfTheValley/dls-display-layout-system-releases";

    // The current alpha builds need to see later alpha releases. A stable build only
    // follows stable releases unless the user explicitly installs an alpha build.
    public static UpdateManager CreateManager() => new(
        new GithubSource(RepositoryUrl, accessToken: null,
            prerelease: AppInfo.Version.Contains('-', StringComparison.Ordinal)));
}
