using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Pulsar.Broadcast.GdMusic;

internal sealed record GdSong(string Id, string Source, string Name, string Artist, string Album);

/// <summary>Client for GD音乐台's documented public API.</summary>
internal sealed class GdMusicClient : IDisposable
{
    internal const string ApiUrl = "https://music-api.gdstudio.xyz/api.php";
    private readonly HttpClient http;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Queue<DateTimeOffset> requests = new();
    internal string CacheDirectory { get; }

    internal GdMusicClient(string cacheDirectory, HttpMessageHandler? handler = null)
    {
        CacheDirectory = cacheDirectory;
        Directory.CreateDirectory(cacheDirectory);
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(90);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Pulsar-GD/1.0");
    }

    private async Task<string> Api(params (string Key, string Value)[] args)
    {
        await gate.WaitAsync(lifetime.Token);
        try
        {
            var now = DateTimeOffset.UtcNow;
            while (requests.TryPeek(out var oldest) && now - oldest >= TimeSpan.FromMinutes(5)) requests.Dequeue();
            if (requests.Count >= 50) throw new IOException("GD 接口请求达到每 5 分钟 50 次限制，请稍后再试。");
            requests.Enqueue(now);
            var query = string.Join("&", args.Select(a => Uri.EscapeDataString(a.Key) + "=" + Uri.EscapeDataString(a.Value)));
            using var response = await http.GetAsync(ApiUrl + "?" + query, lifetime.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new IOException("GD 接口限流，请稍后再试。");
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(lifetime.Token);
        }
        finally { gate.Release(); }
    }

    internal async Task<IReadOnlyList<GdSong>> Search(string keyword, string source, int page)
        => ParseSongs(await Api(("types", "search"), ("source", source), ("name", keyword.Trim()),
            ("count", "20"), ("pages", Math.Max(1, page).ToString())));

    internal static IReadOnlyList<GdSong> ParseSongs(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new IOException("GD 搜索接口未返回歌曲列表，请稍后再试或更换平台。");
        var songs = new List<GdSong>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string Read(string field) => item.TryGetProperty(field, out var value) &&
                value.ValueKind is JsonValueKind.String or JsonValueKind.Number ? value.ToString() : "";
            var id = Read("id");
            var source = Read("source");
            var name = Read("name");
            if (id.Length == 0 || source.Length == 0 || name.Length == 0) continue;
            var artist = item.TryGetProperty("artist", out var artists) && artists.ValueKind == JsonValueKind.Array
                ? string.Join(" / ", artists.EnumerateArray().Select(a => a.ToString())) : Read("artist");
            songs.Add(new GdSong(id, source, name, artist, Read("album")));
        }
        return songs.DistinctBy(s => (s.Source, s.Id)).ToArray();
    }

    internal async Task<string> Prepare(GdSong song, int bitrate)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(song.Source + ":" + song.Id + ":" + bitrate)));
        var existing = Directory.GetFiles(CacheDirectory, key + ".*").FirstOrDefault(p => !p.EndsWith(".partial") && new FileInfo(p).Length > 0);
        if (existing is not null) return existing;
        using var document = JsonDocument.Parse(await Api(("types", "url"), ("source", song.Source),
            ("id", song.Id), ("br", bitrate.ToString())));
        if (!document.RootElement.TryGetProperty("url", out var value) || value.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.IsLoopback || uri.UserInfo.Length > 0)
            throw new IOException("GD 没有返回可播放音源，请选择其他版本或平台。");
        if (IPAddress.TryParse(uri.Host, out var address) &&
            (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
             address.GetAddressBytes()[0] is 0 or 10 or 127 or >= 224 ||
             (address.GetAddressBytes()[0] == 192 && address.GetAddressBytes()[1] == 168) ||
             (address.GetAddressBytes()[0] == 172 && address.GetAddressBytes()[1] is >= 16 and <= 31) ||
             (address.GetAddressBytes()[0] == 169 && address.GetAddressBytes()[1] == 254)))
            throw new IOException("GD 返回了无效的音源地址。");
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is "text/html" or "application/json" || response.Content.Headers.ContentLength > 256 * 1024 * 1024)
            throw new IOException("音源响应无效或超过 256 MB，请选择其他版本。");
        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (extension is not (".mp3" or ".flac" or ".m4a" or ".ogg" or ".opus" or ".aac" or ".wav"))
            extension = contentType switch { "audio/flac" or "audio/x-flac" => ".flac", "audio/mp4" => ".m4a", "audio/ogg" => ".ogg", _ => ".mp3" };
        var path = Path.Combine(CacheDirectory, key + extension);
        var partial = path + ".partial";
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(lifetime.Token);
            await using (var output = File.Create(partial))
            {
                var buffer = new byte[81920];
                long length = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, lifetime.Token)) > 0)
                {
                    length += count;
                    if (length > 256 * 1024 * 1024) throw new IOException("音源超过 256 MB。");
                    await output.WriteAsync(buffer.AsMemory(0, count), lifetime.Token);
                }
                if (length == 0) throw new IOException("音源为空，请选择其他版本。");
            }
            File.Move(partial, path, true);
            return path;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public void Dispose() { lifetime.Cancel(); http.Dispose(); }
}
