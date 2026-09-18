using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyToneDown.Services;

/// <summary>
/// Produces and caches gain-adjusted copies of theme media.
/// </summary>
/// <remarks>
/// Nothing in the user's library is ever modified. Every adjusted file is written into
/// Jellyfin's cache directory under a name derived from the item, the source file's size
/// and timestamp, and the gain applied -- so changing the volume produces a new file rather
/// than invalidating anything, and replacing a theme.mp3 on disk naturally produces a new
/// cache key as well.
/// </remarks>
public sealed class GainCacheService
{
    private static readonly TimeSpan _encodeTimeout = TimeSpan.FromMinutes(5);

    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<GainCacheService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _unprocessable = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="GainCacheService"/> class.
    /// </summary>
    /// <param name="mediaEncoder">Jellyfin's media encoder, used only for the ffmpeg path.</param>
    /// <param name="logger">The logger.</param>
    public GainCacheService(IMediaEncoder mediaEncoder, ILogger<GainCacheService> logger)
    {
        _mediaEncoder = mediaEncoder;
        _logger = logger;
    }

    /// <summary>
    /// Gets the directory adjusted copies are written to.
    /// </summary>
    public string CacheRoot
    {
        get
        {
            var paths = Plugin.Instance?.AppPaths;
            var root = paths?.CachePath ?? Path.Combine(Path.GetTempPath(), "jellyfin-cache");
            return Path.Combine(root, "jellytonedown");
        }
    }

    /// <summary>
    /// Gets the gain-adjusted copy of an item, producing it if it is not cached yet.
    /// </summary>
    /// <param name="item">The theme item.</param>
    /// <param name="profile">The container to produce.</param>
    /// <param name="amplitude">The amplitude multiplier, 0-1.</param>
    /// <param name="isVideo">Whether this is a theme video.</param>
    /// <param name="sourceBitrateBps">The source audio bitrate, if known, so the adjusted
    /// copy is not encoded larger than the original.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The path to the adjusted file, or null if it could not be produced.</returns>
    public async Task<string?> GetOrCreateAsync(
        BaseItem item,
        ContainerProfile profile,
        double amplitude,
        bool isVideo,
        int? sourceBitrateBps,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(profile);

        var sourcePath = item.Path;
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
        {
            return null;
        }

        var encoderArgs = profile.BuildEncoderArgs(sourceBitrateBps).ToArray();
        var key = BuildKey(item.Id, sourcePath, profile, amplitude, encoderArgs);
        if (_unprocessable.ContainsKey(key))
        {
            return null;
        }

        var targetPath = Path.Combine(CacheRoot, key);
        if (File.Exists(targetPath))
        {
            TouchQuietly(targetPath);
            return targetPath;
        }

        var gate = _locks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another request may have produced it while we waited.
            if (File.Exists(targetPath))
            {
                return targetPath;
            }

            if (_unprocessable.ContainsKey(key))
            {
                return null;
            }

            Directory.CreateDirectory(CacheRoot);

            var tempPath = targetPath + ".partial";
            var produced = await RunFfmpegAsync(sourcePath, tempPath, profile, encoderArgs, amplitude, isVideo, cancellationToken)
                .ConfigureAwait(false);

            if (!produced)
            {
                DeleteQuietly(tempPath);
                _unprocessable.TryAdd(key, 0);
                return null;
            }

            try
            {
                File.Move(tempPath, targetPath, overwrite: true);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "JellyToneDown: could not finalise cached file {Path}", targetPath);
                DeleteQuietly(tempPath);
                return null;
            }

            _logger.LogDebug(
                "JellyToneDown: cached {Item} at {Percent:P0} amplitude as {File}",
                item.Name,
                amplitude,
                key);

            return targetPath;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Deletes every cached file.
    /// </summary>
    /// <returns>The number of files removed.</returns>
    public int Clear()
    {
        _unprocessable.Clear();

        var root = CacheRoot;
        if (!Directory.Exists(root))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(root))
        {
            if (DeleteQuietly(file))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>
    /// Gets the current size of the cache in bytes, and the number of files in it.
    /// </summary>
    /// <returns>A tuple of total bytes and file count.</returns>
    public (long Bytes, int Files) GetCacheStats()
    {
        var root = CacheRoot;
        if (!Directory.Exists(root))
        {
            return (0, 0);
        }

        long bytes = 0;
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(root))
        {
            try
            {
                bytes += new FileInfo(file).Length;
                count++;
            }
            catch (IOException)
            {
                // Raced with a delete; ignore.
            }
        }

        return (bytes, count);
    }

    /// <summary>
    /// Prunes the least recently used entries until the cache fits inside the configured ceiling.
    /// </summary>
    /// <param name="maxBytes">The ceiling, in bytes.</param>
    /// <returns>The number of files removed.</returns>
    public int Prune(long maxBytes)
    {
        var root = CacheRoot;
        if (!Directory.Exists(root) || maxBytes <= 0)
        {
            return 0;
        }

        List<FileInfo> files;
        try
        {
            files = Directory.EnumerateFiles(root)
                .Select(static f => new FileInfo(f))
                .Where(static f => f.Exists)
                .OrderBy(static f => f.LastAccessTimeUtc)
                .ToList();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "JellyToneDown: could not enumerate the cache for pruning");
            return 0;
        }

        var total = files.Sum(static f => f.Length);
        var removed = 0;

        foreach (var file in files)
        {
            if (total <= maxBytes)
            {
                break;
            }

            var length = file.Length;
            if (DeleteQuietly(file.FullName))
            {
                total -= length;
                removed++;
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation("JellyToneDown: pruned {Count} cached file(s) to stay under the size limit", removed);
        }

        return removed;
    }

    private static string BuildKey(
        Guid itemId,
        string sourcePath,
        ContainerProfile profile,
        double amplitude,
        IReadOnlyList<string> encoderArgs)
    {
        long length = 0;
        long stamp = 0;
        try
        {
            var info = new FileInfo(sourcePath);
            length = info.Length;
            stamp = info.LastWriteTimeUtc.Ticks;
        }
        catch (IOException)
        {
            // Fall through with zeroes; the key is still stable for the session.
        }

        // The encoder arguments are part of the key, so changing the encoding policy (or
        // the source's bitrate) produces a new cache entry on its own. Nothing has to
        // remember to invalidate anything.
        var material = string.Create(
            CultureInfo.InvariantCulture,
            $"{sourcePath}|{length}|{stamp}|{profile.Extension}|{string.Join(' ', encoderArgs)}");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToUpperInvariant();
        var ampUnits = (int)Math.Round(Math.Clamp(amplitude, 0d, 1d) * 100000d, MidpointRounding.AwayFromZero);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{itemId:N}-{hash[..12]}-{ampUnits:D6}.{profile.Extension}");
    }

    private static void TouchQuietly(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // Access-time updates are a nicety for pruning, not a requirement.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private async Task<bool> RunFfmpegAsync(
        string sourcePath,
        string targetPath,
        ContainerProfile profile,
        IReadOnlyList<string> encoderArgs,
        double amplitude,
        bool isVideo,
        CancellationToken cancellationToken)
    {
        var ffmpeg = _mediaEncoder.EncoderPath;
        if (string.IsNullOrEmpty(ffmpeg))
        {
            _logger.LogWarning("JellyToneDown: no ffmpeg path is configured, cannot adjust theme volume");
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false,
            RedirectStandardInput = false,
        };

        foreach (var arg in BuildArguments(sourcePath, targetPath, profile, encoderArgs, amplitude, isVideo))
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyToneDown: failed to start ffmpeg at {Path}", ffmpeg);
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_encodeTimeout);

        string stderr;
        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            _logger.LogWarning("JellyToneDown: ffmpeg timed out or was cancelled for {Source}", sourcePath);
            return false;
        }

        if (process.ExitCode != 0)
        {
            _logger.LogWarning(
                "JellyToneDown: ffmpeg exited {Code} for {Source}. This theme will play at its original volume. {Error}",
                process.ExitCode,
                sourcePath,
                stderr.Length > 1200 ? stderr[^1200..] : stderr);
            return false;
        }

        return File.Exists(targetPath) && new FileInfo(targetPath).Length > 0;
    }

    private static IEnumerable<string> BuildArguments(
        string sourcePath,
        string targetPath,
        ContainerProfile profile,
        IReadOnlyList<string> encoderArgs,
        double amplitude,
        bool isVideo)
    {
        yield return "-nostdin";
        yield return "-hide_banner";
        yield return "-loglevel";
        yield return "warning";
        yield return "-y";
        yield return "-i";
        yield return sourcePath;

        if (isVideo)
        {
            yield return "-map";
            yield return "0:v:0";
            yield return "-map";
            yield return "0:a:0";
        }
        else
        {
            // Audio only: this also drops embedded cover art, which some encoders choke on.
            yield return "-map";
            yield return "0:a:0";
        }

        yield return "-filter:a";
        yield return string.Create(CultureInfo.InvariantCulture, $"volume={amplitude:0.######}");

        foreach (var arg in encoderArgs)
        {
            yield return arg;
        }

        yield return "-f";
        yield return profile.FfmpegFormat;
        yield return targetPath;
    }

    private void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JellyToneDown: could not terminate ffmpeg");
        }
    }
}
