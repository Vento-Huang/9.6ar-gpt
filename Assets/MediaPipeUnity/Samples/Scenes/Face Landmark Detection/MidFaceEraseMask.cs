using System;
using System.Collections.Generic;
using Mediapipe.Unity;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Camera-space skin reconstruction. Only trusted skin contributes to the fill;
/// local analytic feature/fold masks composite it once over the original video.
/// No face-shaped mesh, large-radius raw-video taps, CPU camera readback or
/// per-frame Texture2D allocations.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(10000)] // after the MediaPipe annotation LateUpdate
public class MidFaceEraseMask : MonoBehaviour
{
    [Header("Automatic references / 自动关联")]
    public Transform pointListAnnotation;
    public RawImage screenImage;
    public Shader reconstructionShader;
    public Shader compositeShader;
    public Shader surfaceShader;

    [Header("Final skin / 最终皮肤")]
    [Range(0f, 1f)] public float effectAmount = 1f;
    [Range(0.95f, 1.15f)] public float maskScale = 1f;
    [Range(0.025f, 0.10f)] public float featherFraction = 0.09f;
    [Range(0f, 0.12f)] public float volume = 0f;
    [Tooltip("Preserve spatial cheek lighting before reconstructing missing skin. Zero disables this illumination trend for comparison.")]
    [Range(0f, 1f)] public float localColorStrength = 1f;
    [Tooltip("Soften isolated bright skin reflections relative to nearby cheek lighting. Keeps ordinary gradients and shadows.")]
    [Range(0f, 1f)] public float highlightSuppression = 0.85f;
    [Range(0f, 1f)] public float fineGrain = 0.35f;
    [Tooltip("Only the smooth skin field is downsampled. Original video and mask edges stay at native resolution.")]
    public int reconstructionResolution = 256;
    [Range(0.01f, 0.18f)] public float colorSmoothingSeconds = 0.065f;
    [Range(2f, 10f)] public float landmarkCutoff = 4f;

    [Header("Entry timeline / 入场渐变")]
    [Tooltip("Fade from the live face to the existing final skin. All regions and enclosed gaps transition together.")]
    public bool playEntryAnimation = true;
    [Min(0.1f)] public float entryDurationSeconds = 2f;
    [Tooltip("Replay after this many seconds without a detected face. Short tracking losses preserve entry progress; this is not person identification.")]
    [Min(0.5f)] public float resetAfterAbsence = 3f;
    [Range(0.08f, 0.5f)] public float lostFaceFadeSeconds = 0.18f;

    [Header("Narcissus / 水仙生长")]
    public bool growNarcissus = true;
    [Tooltip("Growth begins after the two-second skin entry. Each viewer has an independent clock.")]
    [Min(0.1f)] public float growthDurationSeconds = 21f;

    [Header("Debug / 调试")]
    public bool showLandmarks = false;
    public bool showMask = false;
    public bool showControls = true;

    public const int MaximumFaces = 6;
    public int ActiveFaceCount { get; private set; }
    public bool IsTracking => ActiveFaceCount > 0;
    public float FacePresence => IsTracking ? 1f : 0f;
    public float PresentationSeconds => Primary != null ? Primary.PresentationSeconds : 0f;
    public float EntryProgress => Primary != null ? Primary.EntryProgress : 0f;
    public float GrowthProgress => Primary != null ? Primary.GrowthProgress : 0f;
    public bool GrowthReady => Primary != null && IsTracking && playEntryAnimation
        && Primary.PresentationSeconds >= Mathf.Max(0.1f, entryDurationSeconds);
    public Matrix4x4 HeadPose => Primary != null ? Primary.HeadPose : Matrix4x4.identity;
    public event Action<MidFaceEraseMask> FrameUpdated;

    // Slots are internal storage; TrackId identifies a temporary geometric
    // track across changing detection-array order, not a person's identity.
    readonly FacelessTrackAssigner _assigner = new FacelessTrackAssigner();
    readonly FacelessFaceRenderer[] _faces = new FacelessFaceRenderer[MaximumFaces];
    readonly RawImage[] _layers = new RawImage[MaximumFaces];
    readonly NarcissusFaceGrowth[] _plants = new NarcissusFaceGrowth[MaximumFaces];
    readonly Vector2[][] _detections = new Vector2[MaximumFaces][];
    readonly Rect[] _bounds = new Rect[MaximumFaces];
    readonly Matrix4x4[] _poses = new Matrix4x4[MaximumFaces];
    readonly bool[] _hasPoses = new bool[MaximumFaces];
    readonly int[] _drawOrder = new int[MaximumFaces];
    readonly List<Renderer> _visuals = new List<Renderer>();
    readonly Dictionary<Renderer, bool> _originalVisibility = new Dictionary<Renderer, bool>();
    readonly Dictionary<Renderer, bool> _pointVisuals = new Dictionary<Renderer, bool>();
    FaceLandmarkerResultAnnotationController _controller;
    FaceLandmarkerRunner _runner;
    Material _baseMaterial, _originalMaterial;
    bool _boundImage, _wasPaired, _waitingForCamera, _replayAllRequested;
    int _version = -1, _cameraWidth, _cameraHeight;
    float _nextFind, _nextShaderCheck, _lastSeen = -100f, _lastPair = -100f;
    float _videoVisibility = 1f, _captureFps;
    FacelessFaceRenderer Primary
    {
        get
        {
            for (int i = 0; i < MaximumFaces; i++)
                if (_faces[i] != null && _faces[i].IsTracking && _faces[i].Ready) return _faces[i];
            return null;
        }
    }

    void OnEnable()
    {
        var legacyRenderer = GetComponent<MeshRenderer>();
        if (legacyRenderer != null) legacyRenderer.enabled = false;
        _nextFind = _nextShaderCheck = 0f;
        _lastSeen = _lastPair = -100f;
        _version = -1; _videoVisibility = 1f; _captureFps = 0f;
        _waitingForCamera = _replayAllRequested = false; ActiveFaceCount = 0;
        _assigner.Reset();
        for (int i = 0; i < MaximumFaces; i++) _detections[i] = new Vector2[468];
        if (reconstructionShader == null) reconstructionShader = Shader.Find("Hidden/Faceless/SkinReconstruction");
        if (compositeShader == null) compositeShader = Shader.Find("Faceless/SkinComposite");
        if (surfaceShader == null) surfaceShader = Shader.Find("Hidden/Faceless/SurfaceGuard");
        if (!CheckShaders()) { enabled = false; return; }
        _baseMaterial = new Material(compositeShader) { hideFlags = HideFlags.HideAndDontSave };
        _baseMaterial.SetFloat("_Amount", 0f);
        _baseMaterial.SetFloat("_OverlayOnly", 0f);
        _baseMaterial.SetVector("_FrameU", new Vector4(1, 0, 0, 0));
        _baseMaterial.SetVector("_FrameV", new Vector4(0, 1, 0, 0));
        _baseMaterial.SetTexture("_SurfaceTex", Texture2D.blackTexture);
    }

    bool CheckShader(Shader shader)
    {
        if (shader == null) { Debug.LogError("Faceless: missing skin shader reference.", this); return false; }
#if UNITY_EDITOR
        if (UnityEditor.ShaderUtil.ShaderHasError(shader))
        {
            foreach (var message in UnityEditor.ShaderUtil.GetShaderMessages(shader))
                Debug.LogError($"Faceless shader {shader.name}: {message.message} ({message.file}:{message.line})", this);
            return false;
        }
#endif
        if (!shader.isSupported)
        { Debug.LogError($"Faceless: {shader.name} is unsupported on {SystemInfo.graphicsDeviceType}.", this); return false; }
        return true;
    }
    bool CheckShaders() => CheckShader(reconstructionShader) && CheckShader(compositeShader) && CheckShader(surfaceShader);

    void FindReferences()
    {
        if (Time.unscaledTime < _nextFind) return;
        _nextFind = Time.unscaledTime + 0.5f;
        if (_runner == null) _runner = FindFirstObjectByType<FaceLandmarkerRunner>();
        if (_controller == null) _controller = FindFirstObjectByType<FaceLandmarkerResultAnnotationController>();
        if (screenImage == null)
        {
            foreach (var img in FindObjectsByType<RawImage>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                // Generated overlays have a Faceless name and must never be
                // mistaken for the source when the sample recreates its UI.
                if (img.gameObject.name.StartsWith("Faceless track ") || img.gameObject.name == "Narcissus growth") continue;
                if (img.GetComponent<Mediapipe.Unity.Screen>() != null ||
                    img.GetComponentInParent<Mediapipe.Unity.Screen>() != null)
                { screenImage = img; break; }
            }
        }
    }

    void LateUpdate()
    {
        if (_baseMaterial == null) return;
        FindReferences();
        if (Time.unscaledTime >= _nextShaderCheck)
        {
            _nextShaderCheck = Time.unscaledTime + 1f;
            if (!CheckShaders()) { enabled = false; return; }
        }
        if (screenImage == null || screenImage.texture == null) return;
        Texture source = screenImage.texture; // NEVER an already composited layer.
        if (source.width < 32 || source.height < 32) return;
        bool paired = _runner != null && _runner.PreviewIsSynchronized;
        bool waiting = paired && _runner.LastMatchedFrameTime < 0f;
        if (_cameraWidth != source.width || _cameraHeight != source.height || paired != _wasPaired
            || (waiting && !_waitingForCamera))
        {
            ResetTracks();
            _cameraWidth = source.width; _cameraHeight = source.height;
            _wasPaired = paired;
        }
        _waitingForCamera = waiting;
        if (!_boundImage)
        {
            _originalMaterial = screenImage.material;
            screenImage.material = _baseMaterial;
            _boundImage = true;
        }
        float now = Time.unscaledTime, dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        bool hasPair = !waiting && _controller != null && _controller.HasFace
            && (paired ? _runner.PreviewHasFace : now - _controller.LastResultTime < 0.5f);
        if (hasPair && _controller.ResultVersion != _version)
        {
            float interval = _lastPair < 0f ? 0f : Mathf.Max(0f, now - _lastPair);
            if (interval > 0.001f)
                _captureFps = _captureFps <= 0f ? 1f / interval : Mathf.Lerp(_captureFps, 1f / interval, 0.2f);
            _lastPair = now;
            ProcessFaces(source, now, interval, paired);
            // No usable skin output for this newly published face frame:
            // hide immediately instead of fading in unprocessed features.
            if (paired && ActiveFaceCount == 0) _videoVisibility = 0f;
            _version = _controller.ResultVersion;
            RefreshVisuals();
        }
        else if (!hasPair)
        {
            ActiveFaceCount = 0;
            _assigner.Update(_bounds, 0, now, Mathf.Max(0.5f, resetAfterAbsence));
            for (int i = 0; i < MaximumFaces; i++)
            {
                if (_faces[i] != null) _faces[i].MarkMissing();
                // Only paired preview owns/fixes the old pixels. Never draw a
                // stale face over a live unpaired camera texture.
                if (!paired && _layers[i] != null) _layers[i].enabled = false;
            }
        }
        // A person's departure never darkens everyone else. The previous
        // all-faces-lost hold/fade applies only when the whole detector is empty.
        if (waiting) _videoVisibility = 0f;
        else if (hasPair && ActiveFaceCount > 0)
        {
            _lastSeen = now;
            _videoVisibility = Mathf.MoveTowards(_videoVisibility, 1f, dt / 0.18f);
        }
        else if (paired && now - _lastSeen > 0.18f)
            _videoVisibility = Mathf.MoveTowards(_videoVisibility, 0f, dt / 0.25f);
        else if (!paired) _videoVisibility = 1f;

        ApplyBase(_baseMaterial);
        var baseDrawing = screenImage.materialForRendering;
        if (baseDrawing != null && baseDrawing != _baseMaterial) ApplyBase(baseDrawing);
        for (int i = 0; i < MaximumFaces; i++)
        {
            if (_faces[i] == null || _layers[i] == null) continue;
            var layer = _layers[i];
            if (layer.enabled)
            {
                layer.texture = source;
                layer.color = screenImage.color;
                FitLayer(layer, _faces[i].Regions.Bounds);
                _faces[i].ApplyComposite(_faces[i].Composite, _videoVisibility);
                var drawing = layer.materialForRendering;
                if (drawing != null && drawing != _faces[i].Composite)
                    _faces[i].ApplyComposite(drawing, _videoVisibility);
            }
            if (!_assigner.SlotIsActive[i] && !layer.enabled)
            {
                if (_plants[i] != null) { _plants[i].Dispose(); _plants[i] = null; }
                _faces[i].Dispose(); _faces[i] = null; Destroy(layer.gameObject); _layers[i] = null;
            }
            if (_faces[i] != null)
            {
                if (_plants[i] == null && growNarcissus) _plants[i] = new NarcissusFaceGrowth(this);
                if (_plants[i] != null) _plants[i].Update(_faces[i], _layers[i], _cameraWidth, _cameraHeight, _videoVisibility);
            }
        }
        foreach (var item in _pointVisuals)
            if (item.Key != null) item.Key.forceRenderingOff = !showLandmarks || !item.Value;
        FrameUpdated?.Invoke(this);
    }

    void ProcessFaces(Texture source, float now, float elapsed, bool paired)
    {
        Canvas canvas = screenImage.canvas;
        Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Rect rect = screenImage.rectTransform.rect, uv = screenImage.uvRect;
        int count = 0;
        for (int detection = 0; detection < _controller.FaceCount && count < MaximumFaces; detection++)
        {
            if (!_controller.TryGetFace(detection, out var points, out var pose, out bool hasPose)) continue;
            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = new Vector2(float.MinValue, float.MinValue);
            bool finite = true;
            for (int landmark = 0; landmark < 468; landmark++)
            {
                Vector2 projected = RectTransformUtility.WorldToScreenPoint(camera, points[landmark].transform.position);
                RectTransformUtility.ScreenPointToLocalPointInRectangle(screenImage.rectTransform, projected, camera, out Vector2 local);
                Vector2 pixel = new Vector2((uv.x + (local.x - rect.xMin) / rect.width * uv.width) * _cameraWidth,
                    (uv.y + (local.y - rect.yMin) / rect.height * uv.height) * _cameraHeight);
                if (float.IsNaN(pixel.x) || float.IsInfinity(pixel.x) || float.IsNaN(pixel.y) || float.IsInfinity(pixel.y))
                { finite = false; break; }
                _detections[count][landmark] = pixel;
                min = Vector2.Min(min, pixel); max = Vector2.Max(max, pixel);
            }
            if (!finite || max.x - min.x < 2f || max.y - min.y < 2f) continue;
            _bounds[count] = Rect.MinMaxRect(min.x, min.y, max.x, max.y);
            _poses[count] = pose; _hasPoses[count] = hasPose;
            count++;
        }
        if (count > 0 && _replayAllRequested)
        {
            // Change the mode only when new face pixels are ready. Switching
            // Final skin -> entry on a held lost frame could expose that frame.
            playEntryAnimation = true;
            foreach (var face in _faces) if (face != null) face.Replay();
            _replayAllRequested = false;
        }
        _assigner.Update(_bounds, count, now, Mathf.Max(0.5f, resetAfterAbsence));
        ActiveFaceCount = 0;
        for (int slot = 0; slot < MaximumFaces; slot++)
        {
            if (_layers[slot] != null) _layers[slot].enabled = false;
            if (!_assigner.SlotIsVisible[slot] && _faces[slot] != null) _faces[slot].MarkMissing();
        }
        for (int detection = 0; detection < count; detection++)
        {
            int slot = _assigner.DetectionToSlot[detection];
            if (slot < 0) continue;
            if (_faces[slot] == null) _faces[slot] = new FacelessFaceRenderer(this);
            var face = _faces[slot];
            if (_assigner.SlotIsNew[slot]) face.Reset(_assigner.TrackIds[slot]);
            // After any missing detection, do not charge the absence interval
            // to that person's entry clock, even while others stayed visible.
            float validElapsed = face.IsTracking ? elapsed : 0f;
            if (!face.Update(source, _detections[detection], _poses[detection], _hasPoses[detection], now, validElapsed, paired)) continue;
            if (_layers[slot] == null) _layers[slot] = CreateLayer(face);
            _layers[slot].enabled = true;
            _layers[slot].material = face.Composite;
            _drawOrder[ActiveFaceCount++] = slot;
        }
        // Face area approximates distance. Restore raw foreground-face pixels
        // above farther layers even while its own entry amount is still zero.
        for (int i = 1; i < ActiveFaceCount; i++)
        {
            int slot = _drawOrder[i], j = i - 1;
            float area = FaceArea(slot);
            while (j >= 0 && FaceArea(_drawOrder[j]) > area)
            { _drawOrder[j + 1] = _drawOrder[j]; j--; }
            _drawOrder[j + 1] = slot;
        }
        for (int i = 0; i < ActiveFaceCount; i++) _layers[_drawOrder[i]].transform.SetAsLastSibling();
    }
    float FaceArea(int slot)
    {
        Rect b = _faces[slot].Regions.Bounds;
        return b.width * b.height;
    }

    RawImage CreateLayer(FacelessFaceRenderer face)
    {
        var go = new GameObject("Faceless track " + face.TrackId, typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        go.hideFlags = HideFlags.DontSave;
        go.layer = screenImage.gameObject.layer;
        go.transform.SetParent(screenImage.transform, false);
        var image = go.GetComponent<RawImage>();
        image.raycastTarget = false;
        image.material = face.Composite;
        return image;
    }
    void FitLayer(RawImage layer, Rect faceBounds)
    {
        // Crop each UI draw to its face bounds; never shade six full screens.
        // Anchor coordinates account for uvRect mirroring and source rotation.
        Rect uv = screenImage.uvRect;
        float x0 = (faceBounds.xMin - 2f) / _cameraWidth, x1 = (faceBounds.xMax + 2f) / _cameraWidth;
        float y0 = (faceBounds.yMin - 2f) / _cameraHeight, y1 = (faceBounds.yMax + 2f) / _cameraHeight;
        float ax0 = (x0 - uv.x) / uv.width, ax1 = (x1 - uv.x) / uv.width;
        float ay0 = (y0 - uv.y) / uv.height, ay1 = (y1 - uv.y) / uv.height;
        Vector2 lo = new Vector2(Mathf.Clamp01(Mathf.Min(ax0, ax1)), Mathf.Clamp01(Mathf.Min(ay0, ay1)));
        Vector2 hi = new Vector2(Mathf.Clamp01(Mathf.Max(ax0, ax1)), Mathf.Clamp01(Mathf.Max(ay0, ay1)));
        var rect = layer.rectTransform;
        rect.anchorMin = lo; rect.anchorMax = hi;
        rect.offsetMin = rect.offsetMax = Vector2.zero;
        layer.uvRect = new Rect(uv.x + lo.x * uv.width, uv.y + lo.y * uv.height,
            (hi.x - lo.x) * uv.width, (hi.y - lo.y) * uv.height);
    }
    void ApplyBase(Material material)
    {
        material.SetFloat("_Amount", 0f); material.SetFloat("_OverlayOnly", 0f);
        material.SetFloat("_VideoVisibility", _videoVisibility);
        material.SetVector("_CameraSize", new Vector4(_cameraWidth, _cameraHeight, 1f / _cameraWidth, 1f / _cameraHeight));
    }

    void RefreshVisuals()
    {
        _visuals.Clear();
        _controller.GetComponentsInChildren<Renderer>(true, _visuals);
        foreach (var renderer in _visuals)
        {
            if (renderer == null || _originalVisibility.ContainsKey(renderer)) continue;
            _originalVisibility[renderer] = renderer.forceRenderingOff;
            var pointList = renderer.GetComponentInParent<PointListAnnotation>();
            _pointVisuals[renderer] = pointList != null && pointList.GetComponentInParent<FaceLandmarkListAnnotation>() != null;
        }
    }

    // Future plants: iterate slots, retain TrackId, and keep independent growth
    // objects for each ID. Invalid/missing tracks return false for new anchors.
    public bool TryGetFaceState(int slot, out int trackId, out float entry, out float growth)
    {
        trackId = 0; entry = growth = 0f;
        if (slot < 0 || slot >= MaximumFaces || _faces[slot] == null) return false;
        var face = _faces[slot];
        trackId = face.TrackId; entry = face.EntryProgress; growth = face.GrowthProgress;
        return face.Ready && face.IsTracking;
    }
    public bool TryGetSurfaceAnchor(int landmarkId, out Pose pose, out float faceWidthWorld)
    {
        for (int i = 0; i < MaximumFaces; i++)
            if (TryGetSurfaceAnchor(i, landmarkId, out pose, out faceWidthWorld)) return true;
        pose = default; faceWidthWorld = 0f; return false;
    }
    public bool TryGetSurfaceAnchor(int slot, int landmarkId, out Pose pose, out float faceWidthWorld)
    {
        pose = default; faceWidthWorld = 0f;
        if (slot < 0 || slot >= MaximumFaces || landmarkId < 0 || landmarkId >= 468 || screenImage == null) return false;
        var face = _faces[slot];
        if (face == null || !face.Ready || !face.IsTracking) return false;
        Rect rect = screenImage.rectTransform.rect, uv = screenImage.uvRect;
        Vector2 p = face.Points[landmarkId];
        Vector3 local = new Vector3(rect.xMin + (p.x / _cameraWidth - uv.x) / uv.width * rect.width,
            rect.yMin + (p.y / _cameraHeight - uv.y) / uv.height * rect.height, 0);
        Matrix4x4 reflection = Matrix4x4.Scale(new Vector3(Mathf.Sign(uv.width), Mathf.Sign(uv.height), 1));
        Quaternion rotation = face.HasPose ? (reflection * face.HeadPose * reflection).rotation : Quaternion.identity;
        pose = new Pose(screenImage.rectTransform.TransformPoint(local), screenImage.rectTransform.rotation * rotation);
        faceWidthWorld = screenImage.rectTransform.TransformVector(Vector3.right * (face.Regions.Width / _cameraWidth * rect.width)).magnitude;
        return true;
    }

    [ContextMenu("Replay entry / 重播入场")]
    public void ReplayEntry()
    {
        _replayAllRequested = true;
    }
    [ContextMenu("Show final skin / 显示最终皮肤")]
    public void ShowFinalSkin() { _replayAllRequested = false; playEntryAnimation = false; effectAmount = 1f; }

    void OnGUI()
    {
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.H)
        { showControls = !showControls; Event.current.Use(); }
        if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.R)
        { ReplayEntry(); Event.current.Use(); }
        if (!showControls) return;
        GUI.Window(GetInstanceID(), new Rect(UnityEngine.Screen.width - 244, 12, 232, 320), DrawControls, "Faceless / 6 faces");
    }
    void DrawControls(int id)
    {
        GUILayout.Label("Faces / " + ActiveFaceCount + " / " + MaximumFaces);
        GUILayout.Label("Camera / " + _captureFps.ToString("0.0") + " fps");
        GUILayout.Label("Entry / " + Mathf.Max(0.1f, entryDurationSeconds).ToString("0.0") + " s per person");
        GUILayout.Label("Erase / " + effectAmount.ToString("0.00"));
        effectAmount = GUILayout.HorizontalSlider(effectAmount, 0, 1);
        showLandmarks = GUILayout.Toggle(showLandmarks, "468 landmarks / face");
        showMask = GUILayout.Toggle(showMask, "Regions + boundary");
        growNarcissus = GUILayout.Toggle(growNarcissus, "Narcissus / 21 s growth");
        GUILayout.Label("Growth / " + (GrowthProgress * 100f).ToString("0") + "%");
        if (GUILayout.Button("Final skin")) ShowFinalSkin();
        if (GUILayout.Button("Replay all entries (R)")) ReplayEntry();
        if (GUILayout.Button("Hide controls (H)")) showControls = false;
    }

    void ResetTracks()
    {
        _assigner.Reset(); _version = -1; ActiveFaceCount = 0;
        _lastPair = _lastSeen = -100f; _captureFps = 0f;
        for (int i = 0; i < MaximumFaces; i++)
        {
            if (_plants[i] != null) { _plants[i].Dispose(); _plants[i] = null; }
            if (_faces[i] != null) _faces[i].Dispose();
            if (_layers[i] != null) { _layers[i].enabled = false; Destroy(_layers[i].gameObject); }
            _faces[i] = null; _layers[i] = null;
        }
    }
    void OnDisable()
    {
        if (_boundImage && screenImage != null && screenImage.material == _baseMaterial)
            screenImage.material = _originalMaterial;
        _boundImage = false;
        ResetTracks();
        foreach (var pair in _originalVisibility)
            if (pair.Key != null) pair.Key.forceRenderingOff = pair.Value;
        _originalVisibility.Clear(); _pointVisuals.Clear(); _visuals.Clear();
        if (_baseMaterial != null) Destroy(_baseMaterial);
        _baseMaterial = null;
    }
}
