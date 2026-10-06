using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraKingdoms.Rules.Replay;
using AstraKingdoms.V2.Common;

namespace AstraKingdoms.V2.Replays
{
    /// <summary>Encoding target (plan: PROPOSED 30-second 720p clip, lower-quality fallback after device testing).</summary>
    public sealed class ClipQuality
    {
        public string Name { get; }
        public int Width { get; }
        public int Height { get; }
        public int Fps { get; }
        public int BitrateBps { get; }

        public ClipQuality(string name, int width, int height, int fps, int bitrateBps)
        {
            Name = name;
            Width = width;
            Height = height;
            Fps = fps;
            BitrateBps = bitrateBps;
        }

        public static readonly ClipQuality Hd720 = new ClipQuality("720p", 1280, 720, 30, 4000000);
        public static readonly ClipQuality Fallback480 = new ClipQuality("480p", 854, 480, 30, 2000000);

        /// <summary>Bitrate × duration plus 5% container overhead.</summary>
        public long EstimatedBytes(int durationMs) => (long)BitrateBps / 8 * durationMs / 1000 * 105 / 100;
    }

    /// <summary>PROPOSED storage rules for exported clips.</summary>
    public sealed class ClipStoragePolicy
    {
        public int MaxClipsKept { get; set; } = 5;
        public long MaxTotalBytes { get; set; } = 100L * 1024 * 1024;
        /// <summary>Free space that must remain on the phone after an export.</summary>
        public long MinFreeBytesAfterExport { get; set; } = 200L * 1024 * 1024;

        public static readonly ClipStoragePolicy Default = new ClipStoragePolicy();
    }

    public sealed class ExportedClip
    {
        public string Path { get; }
        public long Bytes { get; }
        public DateTimeOffset CreatedAt { get; }

        public ExportedClip(string path, long bytes, DateTimeOffset createdAt)
        {
            Path = path;
            Bytes = bytes;
            CreatedAt = createdAt;
        }
    }

    /// <summary>File access for exports. Temp directories are per job; finished clips live in an app-private exports folder.</summary>
    public interface IExportFileSystem
    {
        string CreateTempDirectory(string jobId);
        void DeleteDirectory(string path);
        IReadOnlyList<string> TempDirectories();
        /// <summary>Moves a finished clip from a temp directory into the exports folder; returns its final path.</summary>
        string MoveToExports(string tempFile, string fileName);
        IReadOnlyList<ExportedClip> Exports();
        void DeleteFile(string path);
        long FreeBytes();
        bool Exists(string path);
    }

    /// <summary>System.IO implementation (usable on the device with Unity's temporaryCachePath / persistentDataPath).</summary>
    public sealed class DirectoryExportFileSystem : IExportFileSystem
    {
        private readonly string _tempRoot;
        private readonly string _exportRoot;
        private readonly Func<long> _freeBytes;
        private readonly Func<DateTimeOffset> _now;

        public DirectoryExportFileSystem(string tempRoot, string exportRoot, Func<long> freeBytes, Func<DateTimeOffset> now)
        {
            _tempRoot = tempRoot ?? throw new ArgumentNullException(nameof(tempRoot));
            _exportRoot = exportRoot ?? throw new ArgumentNullException(nameof(exportRoot));
            _freeBytes = freeBytes ?? (() => long.MaxValue);
            _now = now ?? (() => DateTimeOffset.UtcNow);
            Directory.CreateDirectory(_tempRoot);
            Directory.CreateDirectory(_exportRoot);
        }

        public string CreateTempDirectory(string jobId)
        {
            string path = System.IO.Path.Combine(_tempRoot, "clip-" + jobId);
            Directory.CreateDirectory(path);
            return path;
        }

        public void DeleteDirectory(string path)
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        public IReadOnlyList<string> TempDirectories() =>
            Directory.Exists(_tempRoot) ? Directory.GetDirectories(_tempRoot, "clip-*").OrderBy(p => p, StringComparer.Ordinal).ToArray() : Array.Empty<string>();

        public string MoveToExports(string tempFile, string fileName)
        {
            string target = System.IO.Path.Combine(_exportRoot, fileName);
            if (File.Exists(target)) File.Delete(target);
            File.Move(tempFile, target);
            return target;
        }

        public IReadOnlyList<ExportedClip> Exports() =>
            Directory.GetFiles(_exportRoot, "*.mp4").Select(f => new ExportedClip(f, new FileInfo(f).Length, new DateTimeOffset(File.GetLastWriteTimeUtc(f), TimeSpan.Zero)))
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Path, StringComparer.Ordinal).ToArray();

        public void DeleteFile(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        public long FreeBytes() => _freeBytes();
        public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
        internal DateTimeOffset Now => _now();
    }

    /// <summary>Everything the renderer needs: the filtered manifest, the window, quality and where to write.</summary>
    public sealed class ClipRenderRequest
    {
        public ShareClipManifest Manifest { get; }
        public HighlightWindow Window { get; }
        public ClipQuality Quality { get; }
        public string TempDirectory { get; }

        public ClipRenderRequest(ShareClipManifest manifest, HighlightWindow window, ClipQuality quality, string tempDirectory)
        {
            Manifest = manifest;
            Window = window;
            Quality = quality;
            TempDirectory = tempDirectory;
        }
    }

    /// <summary>
    /// Renders and encodes a clip into the request's temp directory and returns the encoded file path.
    /// On the phone: the Unity frame-capture exporter (render-to-texture) feeding an encoder bridge
    /// (Android MediaCodec). It receives only the filtered manifest, never the match record.
    /// </summary>
    public interface IClipRenderer
    {
        Task<string> RenderAsync(ClipRenderRequest request, IProgress<int> percent, CancellationToken ct);
    }

    public enum ShareOutcome : byte
    {
        /// <summary>The share sheet was shown; what the user did there is not reported by the OS.</summary>
        SheetShown = 0,
        Unavailable = 1,
        Failed = 2,
    }

    /// <summary>The operating-system share sheet (NativeShare-style). Nothing is ever posted automatically.</summary>
    public interface IShareSheet
    {
        Task<ShareOutcome> ShowAsync(string filePath, string mimeType, string subject);
    }

    public enum ExportState : byte
    {
        Prepared = 0,
        Rendering = 1,
        /// <summary>The finished clip is in the exports folder, waiting for the player to preview it.</summary>
        PreviewReady = 2,
        Shared = 3,
        Discarded = 4,
        /// <summary>The app was paused/killed or the player cancelled: temp files removed, the match result untouched.</summary>
        Interrupted = 5,
        Failed = 6,
    }

    public enum ExportPrepareStatus : byte
    {
        Ready = 0,
        NotTerminal = 1,
        NotPublished = 2,
        Unsupported = 3,
        InsufficientStorage = 4,
        SharingSuspended = 5,
    }

    public sealed class ExportJob
    {
        public string Id { get; }
        public ShareClipManifest Manifest { get; }
        public HighlightWindow Window { get; }
        public ClipQuality Quality { get; }
        public ExportState State { get; internal set; }
        public string TempDirectory { get; internal set; }
        public string ClipPath { get; internal set; }
        /// <summary>Set once the player has watched the preview; sharing requires it.</summary>
        public bool PreviewConfirmed { get; internal set; }
        public string Error { get; internal set; }

        public ExportJob(string id, ShareClipManifest manifest, HighlightWindow window, ClipQuality quality)
        {
            Id = id;
            Manifest = manifest;
            Window = window;
            Quality = quality;
            State = ExportState.Prepared;
        }
    }

    public sealed class ExportPreparation
    {
        public ExportPrepareStatus Status { get; }
        public ExportJob Job { get; }
        public IReadOnlyList<string> EvictedClips { get; }

        public ExportPreparation(ExportPrepareStatus status, ExportJob job, IReadOnlyList<string> evicted)
        {
            Status = status;
            Job = job;
            EvictedClips = evicted ?? Array.Empty<string>();
        }
    }

    /// <summary>
    /// Replay clip export (plan: "Replay export starts in V2, after a match is terminal"). Flow:
    /// prepare (terminal + published record, compatibility, privacy filter, highlight, storage) →
    /// render into a per-job temp directory → move to exports → preview → the player explicitly opens
    /// the OS share sheet. Interruption at any point deletes the job's temp files; the match result and
    /// records are only ever read.
    /// </summary>
    public sealed class ReplayExportService
    {
        public const string MimeType = "video/mp4";

        private readonly object _gate = new object();
        private readonly IExportFileSystem _fs;
        private readonly IClipRenderer _renderer;
        private readonly IShareSheet _share;
        private readonly ClipStoragePolicy _storage;
        private readonly Common.IFeatureGate _features;
        private readonly Func<DateTimeOffset> _now;
        private readonly Dictionary<string, ExportJob> _jobs = new Dictionary<string, ExportJob>(StringComparer.Ordinal);
        private int _counter;

        public ReplayExportService(IExportFileSystem fs, IClipRenderer renderer, IShareSheet share, ClipStoragePolicy storage = null,
            Common.IFeatureGate features = null, Func<DateTimeOffset> now = null)
        {
            _fs = fs ?? throw new ArgumentNullException(nameof(fs));
            _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
            _share = share ?? throw new ArgumentNullException(nameof(share));
            _storage = storage ?? ClipStoragePolicy.Default;
            _features = features ?? OpenFeatureGate.Instance;
            _now = now ?? (() => DateTimeOffset.UtcNow);
        }

        public ExportJob Job(string id)
        {
            lock (_gate) return _jobs.TryGetValue(id, out ExportJob j) ? j : null;
        }

        public ExportPreparation Prepare(string playerId, MatchRecord record, RecordPublication publication, string aliasA, string aliasB,
            ClipQuality quality = null, string exportNonce = null)
        {
            quality = quality ?? ClipQuality.Hd720;
            if (_features.IsSuspended(playerId, SocialFeature.ReplaySharing, _now())) return new ExportPreparation(ExportPrepareStatus.SharingSuspended, null, null);
            if (record?.Result == null) return new ExportPreparation(ExportPrepareStatus.NotTerminal, null, null);
            HighlightWindow window = HighlightSelector.Select(record);
            string nonce = exportNonce ?? Guid.NewGuid().ToString("N");
            ClipBuildStatus built = ReplayPrivacyFilter.Build(record, publication, aliasA, aliasB, window, nonce, out ShareClipManifest manifest);
            switch (built)
            {
                case ClipBuildStatus.NotTerminal: return new ExportPreparation(ExportPrepareStatus.NotTerminal, null, null);
                case ClipBuildStatus.NotPublished: return new ExportPreparation(ExportPrepareStatus.NotPublished, null, null);
                case ClipBuildStatus.Unsupported: return new ExportPreparation(ExportPrepareStatus.Unsupported, null, null);
            }
            List<string> evicted = MakeRoom(quality.EstimatedBytes(window.DurationMs));
            if (evicted == null) return new ExportPreparation(ExportPrepareStatus.InsufficientStorage, null, null);
            lock (_gate)
            {
                var job = new ExportJob("j" + (++_counter).ToString(CultureInfo.InvariantCulture) + "-" + manifest.ClipId.Substring(0, 6), manifest, window, quality);
                _jobs[job.Id] = job;
                return new ExportPreparation(ExportPrepareStatus.Ready, job, evicted);
            }
        }

        /// <summary>
        /// Deletes the oldest exported clips until the new one fits the clip-count and total-size limits
        /// and the phone keeps its minimum free space. Returns the deleted paths, or null when even an
        /// empty exports folder would not leave enough free space (the caller may offer the fallback quality).
        /// </summary>
        private List<string> MakeRoom(long estimate)
        {
            var evicted = new List<string>();
            List<ExportedClip> clips = _fs.Exports().OrderBy(c => c.CreatedAt).ThenBy(c => c.Path, StringComparer.Ordinal).ToList();
            long reclaimable = clips.Sum(c => c.Bytes);
            if (_fs.FreeBytes() + reclaimable - estimate < _storage.MinFreeBytesAfterExport) return null;
            long free = _fs.FreeBytes();
            while (clips.Count > 0 && (clips.Count + 1 > _storage.MaxClipsKept || clips.Sum(c => c.Bytes) + estimate > _storage.MaxTotalBytes ||
                                       free - estimate < _storage.MinFreeBytesAfterExport))
            {
                _fs.DeleteFile(clips[0].Path);
                free += clips[0].Bytes;
                evicted.Add(clips[0].Path);
                clips.RemoveAt(0);
            }
            return evicted;
        }

        /// <summary>Renders the clip. Cancellation or any failure removes the job's temp directory.</summary>
        public async Task<ExportJob> RunAsync(string jobId, IProgress<int> percent = null, CancellationToken ct = default)
        {
            ExportJob job = Job(jobId) ?? throw new ArgumentException("unknown job " + jobId);
            if (job.State != ExportState.Prepared) return job;
            job.State = ExportState.Rendering;
            job.TempDirectory = _fs.CreateTempDirectory(job.Id);
            try
            {
                string encoded = await _renderer.RenderAsync(new ClipRenderRequest(job.Manifest, job.Window, job.Quality, job.TempDirectory), percent, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                job.ClipPath = _fs.MoveToExports(encoded, "astra-clip-" + job.Manifest.ClipId + ".mp4");
                job.State = ExportState.PreviewReady;
            }
            catch (OperationCanceledException)
            {
                job.State = ExportState.Interrupted;
            }
            catch (Exception ex)
            {
                job.State = ExportState.Failed;
                job.Error = ex.GetType().Name;
            }
            finally
            {
                _fs.DeleteDirectory(job.TempDirectory);
            }
            return job;
        }

        /// <summary>The player watched the preview (the screen calls this when playback reaches the end or the player taps "looks good").</summary>
        public bool ConfirmPreview(string jobId)
        {
            ExportJob job = Job(jobId);
            if (job == null || job.State != ExportState.PreviewReady) return false;
            job.PreviewConfirmed = true;
            return true;
        }

        /// <summary>Opens the OS share sheet. Only after a previewed, finished clip and an explicit tap.</summary>
        public async Task<ShareOutcome> ShareAsync(string jobId, string subject)
        {
            ExportJob job = Job(jobId);
            if (job == null || job.State != ExportState.PreviewReady || !job.PreviewConfirmed || !_fs.Exists(job.ClipPath)) return ShareOutcome.Unavailable;
            ShareOutcome outcome = await _share.ShowAsync(job.ClipPath, MimeType, subject).ConfigureAwait(false);
            if (outcome == ShareOutcome.SheetShown) job.State = ExportState.Shared;
            return outcome;
        }

        public void Discard(string jobId)
        {
            ExportJob job = Job(jobId);
            if (job == null) return;
            if (job.ClipPath != null) _fs.DeleteFile(job.ClipPath);
            if (job.TempDirectory != null) _fs.DeleteDirectory(job.TempDirectory);
            job.State = ExportState.Discarded;
        }

        /// <summary>Startup sweep: temp directories left by a crash or a killed process. Returns how many were removed.</summary>
        public int CleanupOrphans()
        {
            HashSet<string> active;
            lock (_gate) active = new HashSet<string>(_jobs.Values.Where(j => j.State == ExportState.Rendering).Select(j => j.TempDirectory).Where(p => p != null), StringComparer.Ordinal);
            int n = 0;
            foreach (string dir in _fs.TempDirectories())
                if (!active.Contains(dir)) { _fs.DeleteDirectory(dir); n++; }
            return n;
        }
    }
}
