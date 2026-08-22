using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace BeamSplit.Core;

public sealed record OfficialMod(
    string Title,
    string Author,
    string Category,
    string Tagline,
    string Version,
    string Downloads,
    string Rating,
    string Updated,
    Uri DetailsUri,
    Uri? ImageUri);

public sealed record RepoDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double Percent => TotalBytes is > 0 ? BytesReceived * 100d / TotalBytes.Value : 0;
}

/// <summary>
/// Read-only browser and explicit downloader for BeamNG's official resource website.
/// The private in-game JSON API requires an authenticated game session, so BeamSplit
/// deliberately uses only public beamng.com listing/detail/download links and never
/// handles forum credentials or cookies.
/// </summary>
public static partial class OfficialModRepository
{
    public static readonly Uri BaseUri = new("https://www.beamng.com/");
    private const long MaxDownloadBytes = 4L * 1024 * 1024 * 1024;

    private static readonly HttpClient Client = CreateClient();

    public static async Task<IReadOnlyList<OfficialMod>> BrowseAsync(
        string order, int page, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(order)) query.Add("order=" + Uri.EscapeDataString(order));
        if (page > 1) query.Add("page=" + page);
        var relative = "resources/" + (query.Count == 0 ? "" : "?" + string.Join("&", query));
        var html = await Client.GetStringAsync(new Uri(BaseUri, relative), cancellationToken);
        return ParseListing(html);
    }

    internal static IReadOnlyList<OfficialMod> ParseListing(string html)
    {
        var results = new List<OfficialMod>();
        foreach (Match blockMatch in ResourceBlockRegex().Matches(html))
        {
            var block = blockMatch.Value;
            var title = TitleRegex().Match(block);
            if (!title.Success) continue;
            var details = SafeOfficialUri(WebUtility.HtmlDecode(title.Groups[1].Value));
            if (details is null) continue;

            var info = DetailsRegex().Match(block);
            var image = ImageRegex().Match(block);
            results.Add(new OfficialMod(
                Clean(title.Groups[2].Value),
                info.Success ? Clean(info.Groups[1].Value) : "BeamNG community",
                info.Success ? Clean(info.Groups[2].Value) : "Other",
                Value(TaglineRegex(), block),
                Value(VersionRegex(), block),
                Value(DownloadsRegex(), block),
                Value(RatingRegex(), block),
                Value(UpdatedRegex(), block),
                details,
                image.Success ? SafeOfficialUri(WebUtility.HtmlDecode(image.Groups[1].Value)) : null));
        }
        return results;
    }

    public static async Task<string> DownloadAsync(OfficialMod mod,
        IProgress<RepoDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureOfficial(mod.DetailsUri);
        var detailHtml = await Client.GetStringAsync(mod.DetailsUri, cancellationToken);
        var downloadUri = ParseDownloadUri(detailHtml)
            ?? throw new InvalidOperationException("This resource has no valid guest Download Now link. Open its official page for requirements or external files.");
        using var response = await Client.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { Scheme: "https" })
            throw new InvalidOperationException("The official download redirected to a non-HTTPS location.");
        var length = response.Content.Headers.ContentLength;
        if (length is > MaxDownloadBytes)
            throw new InvalidOperationException("The resource is larger than BeamSplit's 4 GB safety limit.");

        var finalName = FileName(response, mod);
        Directory.CreateDirectory(ModManager.RepositorySource);
        var destination = ModManager.SafeDestination(ModManager.RepositorySource, finalName);
        var temporary = destination + ".download";
        try
        {
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long received = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    received += read;
                    if (received > MaxDownloadBytes)
                        throw new InvalidOperationException("The resource exceeded BeamSplit's 4 GB safety limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(new RepoDownloadProgress(received, length));
                }
            }

            ValidateZip(temporary);
            File.Move(temporary, destination, true);
            return destination;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    internal static Uri? ParseDownloadUri(string html)
    {
        var link = DownloadRegex().Match(html);
        return link.Success ? SafeOfficialUri(WebUtility.HtmlDecode(link.Groups[1].Value)) : null;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 8
        }) { Timeout = TimeSpan.FromMinutes(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BeamSplit/1.8 (+https://github.com/KumaraKyle/BeamSplit)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return client;
    }

    private static void ValidateZip(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count == 0) throw new InvalidDataException("The downloaded ZIP is empty.");
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith('/') || normalized.Split('/').Any(part => part == ".."))
                throw new InvalidDataException("The downloaded ZIP contains an unsafe path.");
        }
    }

    private static string FileName(HttpResponseMessage response, OfficialMod mod)
    {
        var responseName = Path.GetFileName(response.RequestMessage?.RequestUri?.AbsolutePath ?? "");
        if (responseName.Length > 0) responseName = Uri.UnescapeDataString(responseName);
        var name = response.Content.Headers.ContentDisposition?.FileNameStar
                   ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                   ?? responseName;
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            name = Regex.Replace(mod.Title.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_') + ".zip";
        name = Path.GetFileName(name);
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("BeamNG returned an invalid ZIP filename.");
        return name;
    }

    private static Uri? SafeOfficialUri(string relative)
    {
        if (!Uri.TryCreate(BaseUri, relative.Trim(), out var uri)) return null;
        return uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("www.beamng.com", StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }

    private static void EnsureOfficial(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("www.beamng.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only official beamng.com resource pages are allowed.");
    }

    private static string Value(Regex regex, string input)
    {
        var match = regex.Match(input);
        return match.Success ? Clean(match.Groups[1].Value) : "";
    }

    private static string Clean(string value) => Regex.Replace(
        WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ")), @"\s+", " ").Trim();

    [GeneratedRegex(@"<li\s+class=""resourceListItem[^>]*>.*?</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ResourceBlockRegex();
    [GeneratedRegex(@"<h3\s+class=""title"">.*?<a\s+href=""(resources/[^""]+\.\d+/)""[^>]*>(.*?)</a>.*?</h3>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();
    [GeneratedRegex(@"class=""resourceIcon""[^>]*>\s*<img\s+[^>]*src=""([^""]+)""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ImageRegex();
    [GeneratedRegex(@"<span\s+class=""version"">(.*?)</span>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();
    [GeneratedRegex(@"<div\s+class=""resourceDetails muted"">\s*<a[^>]*>(.*?)</a>.*?<a\s+href=""resources/categories/[^""]+""[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DetailsRegex();
    [GeneratedRegex(@"<div\s+class=""tagLine"">(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TaglineRegex();
    [GeneratedRegex(@"resourceDownloads""[^>]*>.*?<dd>(.*?)</dd>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DownloadsRegex();
    [GeneratedRegex(@"class=""ratings""\s+title=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex RatingRegex();
    [GeneratedRegex(@"resourceUpdated.*?data-datestring=""([^""]+)""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex UpdatedRegex();
    [GeneratedRegex(@"<a\s+href=""(resources/[^""]+/download\?version=\d+)""\s+class=""inner""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex DownloadRegex();
}
