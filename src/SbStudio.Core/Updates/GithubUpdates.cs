using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SbStudio.Core.Updates;

public sealed record UpdateRelease(string Version, string Notes, Uri Download, Uri Checksum, long Size);

/// <summary>Beta builds follow Beta releases until a newer stable version is available; stable builds stay stable.</summary>
public sealed class GithubUpdates(HttpClient http)
{
    public const string Repository = "https://github.com/wanjiaXG/Osu-Storyboard-Studio";
    public const string AssetName = "OsuStoryboardStudio-win-x64.zip";
    public const string ReleasesApi = "https://api.github.com/repos/wanjiaXG/Osu-Storyboard-Studio/releases";
    public const long MaxDownloadBytes = 2L * 1024 * 1024 * 1024;

    public static ReleaseVersion ParseVersion(string value) => ReleaseVersion.Parse(value);

    private static ReleaseVersion? EligibleVersion(JsonElement root, ReleaseVersion current)
    {
        if (root.GetProperty("draft").GetBoolean()) return null;
        ReleaseVersion version;
        try { version = ParseVersion(root.GetProperty("tag_name").GetString() ?? ""); }
        catch (InvalidDataException) { return null; } // Ignore unrelated tags and unsupported release channels.
        // The tag and the GitHub checkbox must agree, so a mislabelled Beta cannot reach stable users.
        if (root.GetProperty("prerelease").GetBoolean() != version.IsBeta ||
            (!current.IsBeta && version.IsBeta) || version <= current) return null;
        return version;
    }

    public static UpdateRelease? ParseRelease(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (EligibleVersion(root, ParseVersion(currentVersion)) == null) return null;
        string version = root.GetProperty("tag_name").GetString() ?? "";
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        JsonElement Find(string name) => assets.Single(a => a.GetProperty("name").GetString() == name);
        string assetName = $"OsuStoryboardStudio-{version.TrimStart('v')}-win-x64.zip";
        if (!assets.Any(a => a.GetProperty("name").GetString() == assetName)) assetName = AssetName;
        var zip = Find(assetName);
        var checksum = Find(assetName + ".sha256");
        Uri Url(JsonElement asset)
        {
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            string prefix = Repository + "/releases/download/" + version + "/";
            if (url.Scheme != "https" || !url.AbsoluteUri.StartsWith(prefix, StringComparison.Ordinal) ||
                url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
                throw new InvalidDataException("The release asset is outside the configured repository.");
            return url;
        }
        long size = zip.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaxDownloadBytes) throw new InvalidDataException("Invalid update size.");
        return new UpdateRelease(version.TrimStart('v'), root.GetProperty("body").GetString() ?? "",
            Url(zip), Url(checksum), size);
    }

    public static UpdateRelease? SelectRelease(string json, string currentVersion)
    {
        using var document = JsonDocument.Parse(json);
        return SelectRelease(document.RootElement.EnumerateArray(), currentVersion);
    }

    private static UpdateRelease? SelectRelease(IEnumerable<JsonElement> releases, string currentVersion)
    {
        var current = ParseVersion(currentVersion);
        var eligible = releases.Select(r => (Release: r, Version: EligibleVersion(r, current)))
            .Where(r => r.Version != null).ToArray();
        // A newer stable release ends Beta participation, even if a subsequent Beta also exists.
        var stable = eligible.Where(r => !r.Version!.IsBeta).ToArray();
        var selected = (stable.Length > 0 ? stable : eligible).OrderByDescending(r => r.Version).FirstOrDefault();
        return selected.Version == null ? null : ParseRelease(selected.Release.GetRawText(), currentVersion);
    }

    public async Task<UpdateRelease?> CheckAsync(string currentVersion, CancellationToken cancel)
    {
        _ = ParseVersion(currentVersion);
        var releases = new List<JsonElement>();
        // /latest excludes prereleases. Paginate the release list and compare version numbers, not dates.
        for (int page = 1; page <= 10; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{ReleasesApi}?per_page=100&page={page}");
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("OsuStoryboardStudio", currentVersion));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, cancel);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            foreach (var release in document.RootElement.EnumerateArray()) releases.Add(release.Clone());
            if (document.RootElement.GetArrayLength() < 100) return SelectRelease(releases, currentVersion);
        }
        throw new InvalidDataException("Too many release pages to safely select an update.");
    }

    public async Task DownloadAsync(UpdateRelease release, string destination, IProgress<float>? progress, CancellationToken cancel)
    {
        using var checksumResponse = await http.GetAsync(release.Checksum, HttpCompletionOption.ResponseHeadersRead, cancel);
        checksumResponse.EnsureSuccessStatusCode();
        await using var checksumStream = await checksumResponse.Content.ReadAsStreamAsync(cancel);
        byte[] small = new byte[1025];
        int count = 0, read;
        while (count < small.Length && (read = await checksumStream.ReadAsync(small.AsMemory(count), cancel)) != 0) count += read;
        string checksum = System.Text.Encoding.UTF8.GetString(small, 0, count).Trim();
        if (count == small.Length || !Regex.IsMatch(checksum, "^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Invalid SHA-256 checksum file.");

        try
        {
            using var response = await http.GetAsync(release.Download, HttpCompletionOption.ResponseHeadersRead, cancel);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancel);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long total = 0;
            while ((read = await input.ReadAsync(buffer, cancel)) != 0)
            {
                total += read;
                if (total > release.Size) throw new InvalidDataException("Update exceeds its declared size.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                hash.AppendData(buffer, 0, read);
                progress?.Report((float)total / release.Size);
            }
            if (total != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Update size or SHA-256 checksum does not match.");
        }
        catch
        {
            if (File.Exists(destination)) File.Delete(destination);
            throw;
        }
    }
}
