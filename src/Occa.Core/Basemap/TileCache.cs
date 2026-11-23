using System.Collections.Concurrent;

namespace Occa.Core.Basemap;

/// <summary>
/// Disk + memory cache of basemap tiles that honours the HTTP caching headers, which is what the Esri terms allow
/// (ArcGIS Location Platform Agreement 3.1(d)6.A; see docs/esri-offline-imagery.md). A tile past its expiry is
/// fetched again when online and used as is when offline; <see cref="Clear"/> empties the cache (7.3).
/// </summary>
public sealed class TileCache
{
    public sealed record CachedTile(byte[] Bytes, string MediaType, DateTimeOffset Fetched, DateTimeOffset? Expires)
    {
        public bool IsFresh(DateTimeOffset now) => Expires is null || Expires > now;
    }

    /// <summary>Used when the service sends no caching headers.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(1);

    private readonly string _folder;
    private readonly EsriWorldImagery _source;
    private readonly ConcurrentDictionary<(int, int, int), CachedTile> _memory = new();
    private readonly ConcurrentDictionary<(int, int, int), Task<CachedTile?>> _inFlight = new();
    private readonly SemaphoreSlim _network = new(6);

    public TileCache(string folder, EsriWorldImagery source)
    {
        _folder = folder;
        _source = source;
        Directory.CreateDirectory(folder);
    }

    public string Folder => _folder;

    private string PathFor(int x, int y, int z) => Path.Combine(_folder, z.ToString(), $"{x}_{y}.tile");

    /// <summary>The tile from memory or disk, if any (fresh or stale).</summary>
    public CachedTile? Peek(int x, int y, int z)
    {
        if (_memory.TryGetValue((x, y, z), out var hit)) return hit;
        var tile = ReadDisk(x, y, z);
        if (tile is not null) _memory[(x, y, z)] = tile;
        return tile;
    }

    /// <summary>The tile, fetching it when missing or expired. Returns the stale copy when the network fails.</summary>
    public Task<CachedTile?> GetAsync(int x, int y, int z, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var cached = Peek(x, y, z);
        if (cached is not null && cached.IsFresh(now)) return Task.FromResult<CachedTile?>(cached);
        return _inFlight.GetOrAdd((x, y, z), _ => FetchAsync(x, y, z, cached, cancellationToken));
    }

    private async Task<CachedTile?> FetchAsync(int x, int y, int z, CachedTile? stale, CancellationToken cancellationToken)
    {
        try
        {
            await _network.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var t = await _source.FetchTileAsync(x, y, z, cancellationToken).ConfigureAwait(false);
                var fetched = DateTimeOffset.UtcNow;
                DateTimeOffset? expires = t.CacheMaxAge is { } maxAge ? fetched + maxAge : t.Expires ?? fetched + DefaultLifetime;
                var tile = new CachedTile(t.Bytes, t.MediaType, fetched, expires);
                _memory[(x, y, z)] = tile;
                WriteDisk(x, y, z, tile);
                return tile;
            }
            finally
            {
                _network.Release();
            }
        }
        catch (Exception) when (stale is not null)
        {
            return stale;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        finally
        {
            _inFlight.TryRemove((x, y, z), out _);
        }
    }

    private CachedTile? ReadDisk(int x, int y, int z)
    {
        var path = PathFor(x, y, z);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            long fetched = br.ReadInt64();
            long expires = br.ReadInt64();
            var media = br.ReadString();
            int n = br.ReadInt32();
            var bytes = br.ReadBytes(n);
            return new CachedTile(bytes, media, DateTimeOffset.FromUnixTimeSeconds(fetched), expires == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(expires));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void WriteDisk(int x, int y, int z, CachedTile tile)
    {
        try
        {
            var path = PathFor(x, y, z);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(tile.Fetched.ToUnixTimeSeconds());
                bw.Write(tile.Expires?.ToUnixTimeSeconds() ?? 0);
                bw.Write(tile.MediaType);
                bw.Write(tile.Bytes.Length);
                bw.Write(tile.Bytes);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (IOException)
        {
            // A cache write failure only costs a re-download.
        }
    }

    /// <summary>Removes every cached tile (license ended, or the user asked).</summary>
    public void Clear()
    {
        _memory.Clear();
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        Directory.CreateDirectory(_folder);
    }
}
