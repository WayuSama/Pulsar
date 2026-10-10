using System.Threading;
using System.Threading.Tasks;
using Pulsar.Broadcast.Local;

namespace Pulsar.Broadcast.GdMusic;

// Songs are explicitly selected from API results, keeping their human-readable metadata.
// Cached audio is not automatically turned into a repeating playlist of hash filenames.
internal sealed class GdCatalogLoader(string directory) : ITrackCatalogLoader
{
    public string RootDirectory => directory;
    public Task<TrackCatalog> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new TrackCatalog([new TrackGroup(TrackCatalog.AllFilesId, "GD音乐台", [])]));
}
