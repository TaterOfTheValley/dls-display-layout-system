using Velopack;
using Velopack.Sources;

namespace DLS;

/// <summary>Finds updates in the public source repository's releases.</summary>
internal static class UpdateService
{
    public const string RepositoryUrl =
        "https://github.com/TaterOfTheValley/dls-display-layout-system";

    // The current alpha builds need to see later alpha releases. A stable build only
    // follows stable releases unless the user explicitly installs an alpha build.
    public static UpdateManager CreateManager() => new(
        new GithubSource(RepositoryUrl, accessToken: null,
            prerelease: AppInfo.Version.Contains('-', StringComparison.Ordinal)));
}
