using System.IO;
using System.Security.Cryptography;
using System.Net.Http;

namespace BeamSplit.Core;

/// <summary>Fail-closed verification for GitHub release assets.</summary>
public static class DownloadVerifier
{
    public static string RequireSha256(string? digest)
    {
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("GitHub did not provide a SHA-256 digest for this asset.");
        var hex = digest[prefix.Length..].Trim();
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
            throw new InvalidDataException("The release asset has an invalid SHA-256 digest.");
        return hex.ToLowerInvariant();
    }

    public static async Task VerifyAsync(string path, string? digest, CancellationToken ct = default)
    {
        var expected = RequireSha256(digest);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual), Convert.FromHexString(expected)))
            throw new InvalidDataException($"SHA-256 mismatch for {Path.GetFileName(path)}.");
    }

    public static async Task<string> DownloadAsync(HttpClient http, string url, string destination,
        string? digest, CancellationToken ct = default)
    {
        RequireSha256(digest);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + $".{Guid.NewGuid():N}.download";
        try
        {
            await using (var source = await http.GetStreamAsync(url, ct))
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(output, ct);
                await output.FlushAsync(ct);
            }
            await VerifyAsync(temp, digest, ct);
            File.Move(temp, destination, true);
            return destination;
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
