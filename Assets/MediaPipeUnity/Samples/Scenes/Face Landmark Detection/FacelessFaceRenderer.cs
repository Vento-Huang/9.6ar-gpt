using System;
using UnityEngine;

/// <summary>
/// One person's accepted skin reconstruction, with independent landmarks,
/// cheek samples, temporal history and entry clock. The source must always be
/// the untouched camera frame, never another person's composite output.
/// Owned by MidFaceEraseMask; this class does not create scene components.
/// </summary>
public sealed class FacelessFaceRenderer : IDisposable
{
    const int LandmarkCount = 468;
    readonly MidFaceEraseMask _settings;
    readonly FacelessRegions _regions = new FacelessRegions();
    readonly FacelessSurface _surface = new FacelessSurface();
    readonly FacelessInterior _interior = new FacelessInterior();
    readonly Vector2[] _points = new Vector2[LandmarkCount];
    readonly Vector2[] _lastRaw = new Vector2[LandmarkCount];
    readonly Vector2[] _velocity = new Vector2[LandmarkCount];
    readonly float[] _stages = new float[FacelessRegions.Count];
    Material _reconstruction, _composite;
    RenderTexture[] _known, _temp, _filled, _history;
    RenderTexture _donorTexture, _guideTexture;
    int _historyIndex, _size, _cameraWidth, _cameraHeight;
    bool _historyReady, _pointsReady, _entryFrameReady, _replayRequested;
    bool _disposed, _reportedFailure;
    float _lastUpdateTime = -1f;

    public Material Composite => _composite;
    public FacelessRegions Regions => _regions;
    /// <summary>Camera-texture pixel coordinates. Treat this array as read-only.</summary>
    public Vector2[] Points => _points;
    public int TrackId { get; private set; } = -1;
    public float PresentationSeconds { get; private set; }
    public float EntryProgress => _settings.playEntryAnimation
        ? Ramp(PresentationSeconds, 0f, EntryDuration) : 1f;
    public float GrowthProgress { get; private set; }
    /// <summary>A retained output exists, including while holding a lost frame.</summary>
    public bool Ready { get; private set; }
    public bool IsTracking { get; private set; }
    public Matrix4x4 HeadPose { get; private set; } = Matrix4x4.identity;
    public bool HasPose { get; private set; }
    float EntryDuration => Mathf.Max(0.1f, _settings.entryDurationSeconds);

    public FacelessFaceRenderer(MidFaceEraseMask settings)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        _settings = settings;
        // The manager checks supported/editor shader variants before creating
        // any slots, avoiding six copies of the same shader error message.
        if (settings.reconstructionShader == null || settings.compositeShader == null ||
            settings.surfaceShader == null)
            throw new ArgumentException("Faceless shader references must be resolved first.", nameof(settings));
        try
        {
            _reconstruction = new Material(settings.reconstructionShader)
            { name = "Faceless person reconstruction", hideFlags = HideFlags.HideAndDontSave };
            _composite = new Material(settings.compositeShader)
            { name = "Faceless person composite", hideFlags = HideFlags.HideAndDontSave };
            _composite.SetFloat("_Amount", 0f);
            _composite.SetFloat("_VideoVisibility", 1f);
            _composite.SetFloat("_OverlayOnly", 1f);
            _composite.SetVector("_FrameU", new Vector4(1, 0, 0, 0));
            _composite.SetVector("_FrameV", new Vector4(0, 1, 0, 0));
            // Union complete cores and enclosed gaps before applying the one
            // entry weight. Independent region fades would expose seams.
            for (int i = 0; i < _stages.Length; i++) _stages[i] = 1f;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Assign a new geometric track without inheriting another face.</summary>
    public void Reset(int trackId)
    {
        TrackId = trackId;
        Ready = IsTracking = HasPose = false;
        _pointsReady = _historyReady = false;
        _historyIndex = 0;
        _lastUpdateTime = -1f;
        _replayRequested = false;
        HeadPose = Matrix4x4.identity;
        ResetEntryClock();
        if (_composite != null) _composite.SetFloat("_Amount", 0f);
    }

    /// <summary>
    /// Called only for a fresh matched detection. elapsed is valid visible
    /// presentation time, zero on reacquisition. Filtering uses its own capped
    /// detection interval so a slow camera cannot stretch the entry duration.
    /// </summary>
    public bool Update(Texture rawSource, Vector2[] rawPoints, Matrix4x4 pose,
        bool hasPose, float now, float elapsed, bool paired)
    {
        if (_disposed) return false;
        if (rawSource == null || rawSource.width < 32 || rawSource.height < 32 ||
            rawPoints == null || rawPoints.Length < LandmarkCount || !Finite(now))
            return RejectFrame();
        for (int i = 0; i < LandmarkCount; i++)
            if (!Finite(rawPoints[i].x) || !Finite(rawPoints[i].y)) return RejectFrame();

        Ready = false;
        if (_cameraWidth != rawSource.width || _cameraHeight != rawSource.height)
        {
            _cameraWidth = rawSource.width;
            _cameraHeight = rawSource.height;
            _pointsReady = _historyReady = _entryFrameReady = false;
        }
        float rawDelta = _lastUpdateTime >= 0f ? now - _lastUpdateTime : 1f / 30f;
        float landmarkDelta = Mathf.Clamp(rawDelta, 0.008f, 0.2f);
        float skinDelta = Mathf.Clamp(rawDelta, 0.001f, 0.1f);
        try
        {
            UpdateLandmarks(rawPoints, landmarkDelta, paired);
            if (!_regions.Build(_points, _settings.maskScale, _settings.featherFraction) ||
                !_surface.Render(_lastRaw, _cameraWidth, _cameraHeight, _settings.surfaceShader) ||
                !EnsureTextures()) return RejectFrame();
            RenderSkin(rawSource, skinDelta);
            if (_replayRequested)
            {
                ResetEntryClock();
                _replayRequested = false;
            }
            // The first usable frame, and the first frame after missing, never
            // count the period when this person was not visible.
            if (_entryFrameReady && Finite(elapsed))
                PresentationSeconds += Mathf.Max(0f, elapsed);
            _entryFrameReady = true;
            _lastUpdateTime = now;
            HeadPose = pose;
            HasPose = hasPose;
            Ready = IsTracking = true;
            GrowthProgress = _settings.playEntryAnimation
                ? Ramp(PresentationSeconds, EntryDuration, EntryDuration + 9f) : 0f;
            _reportedFailure = false;
            return true;
        }
        catch (Exception error)
        {
            if (!_reportedFailure)
            {
                Debug.LogError("Faceless person " + TrackId + ": " + error.Message, _settings);
                _reportedFailure = true;
            }
            return RejectFrame();
        }
    }

    bool RejectFrame()
    {
        Ready = false;
        MarkMissing();
        return false;
    }

    /// <summary>
    /// Retain the last completed output for a held paired image, but discard
    /// temporal continuity before the next image. The manager hides this
    /// person's overlay whenever it advances to a frame without this person.
    /// </summary>
    public void MarkMissing()
    {
        IsTracking = false;
        _pointsReady = _historyReady = _entryFrameReady = false;
    }

    public void Replay() { _replayRequested = true; }

    void ResetEntryClock()
    {
        PresentationSeconds = GrowthProgress = 0f;
        _entryFrameReady = false;
    }

    void UpdateLandmarks(Vector2[] rawPoints, float dt, bool paired)
    {
        if (_pointsReady && Vector2.Distance(rawPoints[1], _points[1]) > Mathf.Max(_regions.Width * 0.4f, 30f))
            _pointsReady = _historyReady = false;
        for (int i = 0; i < LandmarkCount; i++)
        {
            Vector2 raw = rawPoints[i];
            if (!_pointsReady)
            {
                _points[i] = _lastRaw[i] = raw;
                _velocity[i] = Vector2.zero;
                continue;
            }
            Vector2 speed = (raw - _lastRaw[i]) / dt;
            _velocity[i] = Vector2.Lerp(_velocity[i], speed, 1f - Mathf.Exp(-dt * 12f));
            float cutoff = _settings.landmarkCutoff + 14f * _velocity[i].magnitude / Mathf.Max(_regions.Width, 40f);
            float alpha = 1f / (1f + 1f / (2f * Mathf.PI * cutoff * dt));
            _points[i] = Vector2.Lerp(_points[i], raw, alpha);
            if (paired) _points[i] = raw + Vector2.ClampMagnitude(_points[i] - raw, 0.35f);
            _lastRaw[i] = raw;
        }
        _pointsReady = true;
    }

    bool EnsureTextures()
    {
        int size = Mathf.ClosestPowerOfTwo(Mathf.Clamp(_settings.reconstructionResolution, 128, 512));
        if (_known != null && size == _size) return true;
        ReleaseTextures();
        RenderTextureFormat format = RenderTextureFormat.ARGBHalf;
        if (!SystemInfo.SupportsRenderTextureFormat(format)) format = RenderTextureFormat.ARGBFloat;
        if (!SystemInfo.SupportsRenderTextureFormat(format))
            throw new InvalidOperationException("This GPU cannot render floating-point skin buffers.");
        _size = size;
        int levels = 1;
        for (int s = size; s > 4; s >>= 1) levels++;
        try
        {
            _known = new RenderTexture[levels];
            _temp = new RenderTexture[levels];
            _filled = new RenderTexture[levels];
            for (int i = 0, s = size; i < levels; i++, s >>= 1)
            {
                _known[i] = NewTexture(s, s, format, "Trusted skin");
                _temp[i] = NewTexture(s, s, format, "Skin filter");
                _filled[i] = NewTexture(s, s, format, "Skin reconstruction");
            }
            // Allocate into the owned array one at a time: a failed second
            // allocation must not orphan the first history buffer.
            _history = new RenderTexture[2];
            _history[0] = NewTexture(size, size, format, "Skin history A");
            _history[1] = NewTexture(size, size, format, "Skin history B");
            _donorTexture = NewTexture(6, 1, format, "Cheek samples");
            _guideTexture = NewTexture(size, size, format, "Local skin illumination");
            _historyReady = false;
            return true;
        }
        catch
        {
            ReleaseTextures();
            throw;
        }
    }

    static RenderTexture NewTexture(int w, int h, RenderTextureFormat format, string label)
    {
        var rt = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear)
        {
            name = label, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            useMipMap = false, autoGenerateMips = false, antiAliasing = 1, hideFlags = HideFlags.HideAndDontSave
        };
        try
        {
            if (!rt.Create()) throw new InvalidOperationException("Cannot allocate " + label + ".");
            return rt;
        }
        catch
        {
            Release(rt);
            throw;
        }
    }

    void RenderSkin(Texture source, float dt)
    {
        // Keep the accepted reconstruction sequence and native surface guard.
        // All donors originate in this person's raw camera-space skin patches.
        _interior.Build(_regions);
        _regions.SetMaterial(_reconstruction);
        _reconstruction.SetTexture("_InteriorTex", _interior.Texture);
        _reconstruction.SetFloat("_LocalColorStrength", _settings.localColorStrength);
        _reconstruction.SetFloat("_HighlightSuppression", _settings.highlightSuppression);
        _reconstruction.SetVector("_CameraSize", new Vector4(_cameraWidth, _cameraHeight, 1f / _cameraWidth, 1f / _cameraHeight));
        RenderTexture previous = RenderTexture.active;
        bool srgb = GL.sRGBWrite;
        try
        {
            GL.sRGBWrite = false;
            Graphics.Blit(source, _donorTexture, _reconstruction, 0);
            _reconstruction.SetTexture("_DonorTex", _donorTexture);
            Graphics.Blit(source, _guideTexture, _reconstruction, 7);
            _reconstruction.SetTexture("_GuideTex", _guideTexture);
            Graphics.Blit(source, _known[0], _reconstruction, 1);
            for (int i = 0; i < _known.Length - 1; i++)
            {
                _reconstruction.SetVector("_Direction", new Vector4(1, 0, 0, 0));
                Graphics.Blit(_known[i], _temp[i], _reconstruction, 2);
                _reconstruction.SetVector("_Direction", new Vector4(0, 1, 0, 0));
                Graphics.Blit(_temp[i], _known[i + 1], _reconstruction, 2);
            }
            int last = _known.Length - 1;
            Graphics.Blit(_known[last], _filled[last], _reconstruction, 3);
            for (int i = last - 1; i >= 0; i--)
            {
                _reconstruction.SetTexture("_KnownTex", _known[i]);
                Graphics.Blit(_filled[i + 1], _filled[i], _reconstruction, 4);
                for (int sweep = 0; sweep < 2; sweep++)
                {
                    _reconstruction.SetVector("_Direction", new Vector4(1, 0, 0, 0));
                    Graphics.Blit(_filled[i], _temp[i], _reconstruction, 5);
                    _reconstruction.SetVector("_Direction", new Vector4(0, 1, 0, 0));
                    Graphics.Blit(_temp[i], _filled[i], _reconstruction, 5);
                }
            }
            int next = 1 - _historyIndex;
            Graphics.Blit(_filled[0], _temp[0], _reconstruction, 8);
            if (!_historyReady) Graphics.Blit(_temp[0], _history[next]);
            else
            {
                _reconstruction.SetTexture("_HistoryTex", _history[_historyIndex]);
                _reconstruction.SetFloat("_TemporalWeight", 1f - Mathf.Exp(-dt / Mathf.Max(_settings.colorSmoothingSeconds, 0.001f)));
                Graphics.Blit(_temp[0], _history[next], _reconstruction, 6);
            }
            _historyIndex = next;
            _historyReady = true;
        }
        finally
        {
            RenderTexture.active = previous;
            GL.sRGBWrite = srgb;
        }
    }

    public void ApplyComposite(Material target, float videoVisibility = 1f)
    {
        if (target == null || _disposed) return;
        target.SetFloat("_OverlayOnly", 1f);
        target.SetFloat("_VideoVisibility", videoVisibility);
        target.SetFloat("_Amount", Ready && _history != null ? _settings.effectAmount * EntryProgress : 0f);
        if (!Ready || _history == null) return;
        _regions.SetMaterial(target);
        target.SetVector("_CameraSize", new Vector4(_cameraWidth, _cameraHeight, 1f / _cameraWidth, 1f / _cameraHeight));
        target.SetFloatArray("_Stages", _stages);
        target.SetTexture("_SkinTex", _history[_historyIndex]);
        target.SetTexture("_SurfaceTex", _surface.Texture);
        target.SetTexture("_InteriorTex", _interior.Texture);
        target.SetFloat("_Volume", _settings.volume);
        target.SetFloat("_Grain", _settings.fineGrain);
        target.SetFloat("_ShowMask", _settings.showMask ? 1f : 0f);
    }

    static float Ramp(float time, float start, float end) =>
        Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(start, end, time));
    static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

    static void ReleaseObject(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(value);
        else UnityEngine.Object.DestroyImmediate(value);
    }
    static void Release(RenderTexture rt)
    {
        if (rt == null) return;
        rt.Release();
        ReleaseObject(rt);
    }
    void ReleaseTextures()
    {
        if (_known != null) foreach (var t in _known) Release(t);
        if (_temp != null) foreach (var t in _temp) Release(t);
        if (_filled != null) foreach (var t in _filled) Release(t);
        if (_history != null) foreach (var t in _history) Release(t);
        Release(_donorTexture);
        Release(_guideTexture);
        _known = _temp = _filled = _history = null;
        _donorTexture = _guideTexture = null;
        _historyReady = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Ready = IsTracking = HasPose = false;
        ReleaseTextures();
        _surface.Dispose();
        _interior.Dispose();
        ReleaseObject(_reconstruction);
        ReleaseObject(_composite);
        _reconstruction = _composite = null;
    }
}
