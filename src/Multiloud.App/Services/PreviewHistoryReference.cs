using System.Diagnostics.CodeAnalysis;
using Multiloud.Core;

namespace Multiloud.App.Services;

/// <summary>
/// Remembers an exact ready variant without extending its audio-buffer lifetime.
/// Removed tracks already own their active preview; the normal bounded LRU owns
/// any additional variants. History can reuse either owner after cache eviction.
/// </summary>
public sealed class PreviewHistoryReference
{
    private readonly WeakReference<RenderResult> render;
    private readonly WeakReference<List<PeakBin>> peaks;

    public PreviewHistoryReference(string key, RenderResult render, List<PeakBin> peaks)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(peaks);
        Key = key;
        this.render = new(render);
        this.peaks = new(peaks);
    }

    public string Key { get; }

    public bool TryGet(string effectiveKey, [NotNullWhen(true)] out CachedPreview? preview)
    {
        if (Key == effectiveKey && render.TryGetTarget(out var ready) && peaks.TryGetTarget(out var display))
        {
            preview = new(ready, display);
            return true;
        }
        preview = null;
        return false;
    }
}
