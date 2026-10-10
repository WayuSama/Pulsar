using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Pulsar.Broadcast;
using Pulsar.Broadcast.GdMusic;
using Pulsar.Broadcast.Local;

namespace Pulsar.Windows;

internal sealed partial class BroadcastTab
{
    private static readonly string[] GdSources = ["netease", "joox", "bilibili", "tencent", "kuwo", "tidal", "qobuz", "apple", "ytmusic", "spotify"];
    private static readonly string[] GdSourceLabels = ["网易云", "JOOX", "哔哩哔哩", "QQ音乐", "酷我", "TIDAL", "Qobuz", "Apple Music", "YouTube Music", "Spotify"];
    private static readonly int[] GdBitrates = [128, 192, 320, 740, 999];
    private string gdKeyword = "";
    private int gdSource;
    private int gdQuality;
    private int gdPage = 1;
    private int gdBusy;
    private volatile string gdResult = "搜索歌曲或歌手，选择 Play 播放或 Queue 加入队列。";
    private volatile IReadOnlyList<GdSong> gdSongs = Array.Empty<GdSong>();
    private string gdSearchKeyword = "";
    private string gdSearchSource = "netease";

    private void GdAction(Func<GdMusicClient, Task> action)
    {
        var client = plugin.GdMusic;
        if (client is null || Interlocked.CompareExchange(ref gdBusy, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await action(client); }
            catch (OperationCanceledException) { gdResult = "操作已取消。"; }
            catch (Exception e) { gdResult = "GD音乐台：" + e.Message; }
            finally { Interlocked.Exchange(ref gdBusy, 0); }
        });
    }

    private void SearchGdMusic(int page)
    {
        var keyword = page == 1 ? gdKeyword.Trim() : gdSearchKeyword;
        var source = page == 1 ? GdSources[gdSource] : gdSearchSource;
        if (keyword.Length == 0) return;
        GdAction(async client =>
        {
            gdResult = "正在搜索…";
            var songs = await client.Search(keyword, source, page);
            gdSearchKeyword = keyword;
            gdSearchSource = source;
            gdPage = page;
            gdSongs = songs;
            gdResult = songs.Count == 0 ? "没有找到歌曲，可更换关键词或平台。" : $"第 {page} 页，共 {songs.Count} 首；播放时检查音源是否可用。";
        });
    }

    private void PlayGdMusic(int index, bool queue)
    {
        var songs = gdSongs;
        if (index < 0 || index >= songs.Count) return;
        var song = songs[index];
        var bitrate = GdBitrates[gdQuality];
        GdAction(async client =>
        {
            gdResult = "正在准备：" + song.Name;
            var path = await client.Prepare(song, bitrate);
            if (plugin.Configuration.BroadcastMode != BroadcastMode.GdMusic)
            { gdResult = "已切换来源，取消播放。"; return; }
            var player = plugin.Broadcast?.ActiveLocalSource ?? throw new InvalidOperationException("播放器未就绪。");
            var track = new LocalTrack(path, song.Name, song.Name + " — " + song.Artist, "GD音乐台 / " + song.Source);
            plugin.Broadcast?.SetProvider(BroadcastProvider.Local);
            if (queue) player.AddToEnd(track); else player.PlayNow(track);
            gdResult = (queue ? "已加入队列：" : "开始播放：") + song.Name;
        });
    }

    private void DrawGdMusic()
    {
        ImGui.TextWrapped("GD音乐台(music.gdstudio.xyz)");
        using (ImRaii.Disabled(Volatile.Read(ref gdBusy) != 0 || plugin.GdMusic is null))
        {
            ImGui.Combo("音乐平台", ref gdSource, GdSourceLabels, GdSourceLabels.Length);
            ImGui.Combo("音质", ref gdQuality, new[] { "128 kbps", "192 kbps", "320 kbps", "16-bit 无损", "24-bit 无损" }, 5);
            ImGui.TextDisabled(gdSource <= 2 ? "服务方当前标记为稳定的平台" : "此平台可能暂未开放或不稳定");
            ImGui.InputText("歌曲 / 歌手", ref gdKeyword, 512);
            if (ImGui.Button("搜索##gd")) SearchGdMusic(1);
            ImGui.SameLine();
            using (ImRaii.Disabled(gdPage <= 1))
                if (ImGui.Button("上一页##gd")) SearchGdMusic(gdPage - 1);
            ImGui.SameLine();
            using (ImRaii.Disabled(gdSongs.Count < 20))
                if (ImGui.Button("下一页##gd")) SearchGdMusic(gdPage + 1);
            var songs = gdSongs;
            for (var index = 0; index < songs.Count; index++)
            {
                var song = songs[index];
                using var id = ImRaii.PushId(song.Source + ":" + song.Id);
                if (ImGui.SmallButton("Play")) PlayGdMusic(index, false);
                ImGui.SameLine();
                if (ImGui.SmallButton("Queue")) PlayGdMusic(index, true);
                ImGui.SameLine();
                ImGui.TextUnformatted(song.Name + " — " + song.Artist + " | " + song.Album);
            }
        }
        ImGui.TextWrapped(gdResult);
        var player = plugin.Broadcast?.ActiveLocalSource;
        if (player is null) return;
        if (!ReferenceEquals(player, lastPlayer))
        { player.Volume(broadcastMuted ? 0f : volume); lastPlayer = player; }
        DrawNowPlaying(player.Frame?.Snapshot);
        DrawControls(player);
        DrawSeek(player);
        DrawUpNext(player);
    }
}
