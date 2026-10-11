using Pulsar.Broadcast.GdMusic;
using System.Net;
using System.Text;

var root = Path.Combine(Path.GetTempPath(), "pulsar-gd-test-" + Guid.NewGuid().ToString("N"));
var fixture = """[{"id":123,"source":"netease","name":"冷雨夜","artist":["Beyond"],"album":"专辑"},{"id":"123","source":"netease","name":"重复"},{"id":"bad","name":"缺少平台"}]""";
var songs = GdMusicClient.ParseSongs(fixture);
if (songs.Count != 1 || songs[0].Artist != "Beyond" || songs[0].Name != "冷雨夜") throw new Exception("Search metadata parsing failed");
Console.WriteLine("PASS Chinese metadata, numeric IDs and duplicate filtering");
try { GdMusicClient.ParseSongs("{\"error\":\"unavailable\"}"); throw new Exception("Invalid result accepted"); }
catch (IOException) { Console.WriteLine("PASS API errors do not become songs"); }
try
{
    var handler = new FixtureHandler(fixture);
    using (var client = new GdMusicClient(root, handler))
    {
        var results = await client.Search("冷雨夜 & Beyond", "netease", 2);
        if (!handler.LastQuery.Contains("pages=2") || !handler.LastQuery.Contains("count=5") || !handler.LastQuery.Contains("%26")) throw new Exception("Query encoding or page size failed");
        Console.WriteLine("PASS search keyword escaping and paging");
        var path = await client.Prepare(results[0], 128);
        var calls = handler.Calls;
        if (!File.ReadAllBytes(path).SequenceEqual(new byte[] { 73, 68, 51, 4, 5 })) throw new Exception("Cache bytes changed");
        if (await client.Prepare(results[0], 128) != path || handler.Calls != calls) throw new Exception("Cache reuse failed");
        Console.WriteLine("PASS audio cache and reuse without additional API calls");
        handler.Url = "";
        try { await client.Prepare(results[0], 320); throw new Exception("Empty URL accepted"); }
        catch (IOException) { Console.WriteLine("PASS unavailable audio reported"); }
        handler.Url = "http://127.0.0.1/private";
        try { await client.Prepare(results[0], 320); throw new Exception("Loopback accepted"); }
        catch (IOException) { Console.WriteLine("PASS invalid local audio URL rejected"); }
        handler.Url = "https://cdn.example.test/song.mp3";
        handler.FailAudio = true;
        try { await client.Prepare(results[0], 320); throw new Exception("Failed audio accepted"); }
        catch (HttpRequestException) { Console.WriteLine("PASS audio HTTP failure reported"); }
        if (Directory.GetFiles(root, "*.partial").Length != 0) throw new Exception("Partial cache remained");
    }
    using (var limited = new GdMusicClient(root, new FixtureHandler(fixture)))
    {
        for (var i = 0; i < 50; i++) await limited.Search("fixture", "netease", 1);
        try { await limited.Search("fixture", "netease", 1); throw new Exception("Request limit was ignored"); }
        catch (IOException e) when (e.Message.Contains("50")) { Console.WriteLine("PASS 50 requests per 5 minutes enforced"); }
    }
    if (args.Contains("--live"))
    {
        using var client = new GdMusicClient(root);
        var results = await client.Search("冷雨夜", "netease", 1);
        var song = results.First(s => s.Id == "346069");
        var path = await client.Prepare(song, 128);
        Console.WriteLine($"PASS live GD search and audio retrieval: {song.Name} / {song.Artist}, {new FileInfo(path).Length} bytes");
    }
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

sealed class FixtureHandler(string fixture) : HttpMessageHandler
{
    public int Calls;
    public string LastQuery = "";
    public string Url = "https://cdn.example.test/song.mp3";
    public bool FailAudio;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Calls++;
        var uri = request.RequestUri!;
        if (uri.Host == "music-api.gdstudio.xyz")
        {
            LastQuery = uri.Query;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(uri.Query.Contains("types=search") ? fixture : System.Text.Json.JsonSerializer.Serialize(new { url = Url, br = 128 }), Encoding.UTF8, "application/json") });
        }
        return Task.FromResult(new HttpResponseMessage(FailAudio ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
        { Content = new ByteArrayContent([73, 68, 51, 4, 5]) });
    }
}
