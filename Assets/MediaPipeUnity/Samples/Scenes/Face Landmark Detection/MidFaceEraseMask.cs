using System;
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
    [Tooltip("Off: judge the final look immediately. On: normal 0-3 s, eyes 3-6, nose 6-9, mouth 9-12, complete 12-15.")]
    public bool playEntryAnimation = false;
    public float resetAfterAbsence = 1.2f;
    [Range(0.08f, 0.5f)] public float lostFaceFadeSeconds = 0.18f;

    [Header("Debug / 调试")]
    public bool showLandmarks = false;
    public bool showMask = false;
    public bool showControls = true;

    public bool IsTracking { get; private set; }
    public float FacePresence { get; private set; }
    public float PresentationSeconds { get; private set; }
    public float GrowthProgress { get; private set; }
    public bool GrowthReady => IsTracking && playEntryAnimation && PresentationSeconds >= 15f;
    public Matrix4x4 HeadPose => _controller != null ? _controller.FacePose : Matrix4x4.identity;
    public event Action<MidFaceEraseMask> FrameUpdated;

    readonly FacelessRegions _regions = new FacelessRegions();
    readonly FacelessSurface _surface = new FacelessSurface();
    readonly Vector2[] _points = new Vector2[468];
    readonly Vector2[] _lastRaw = new Vector2[468];
    readonly Vector2[] _velocity = new Vector2[468];
    readonly Transform[] _landmarkTransforms = new Transform[468];
    readonly float[] _stages = new float[FacelessRegions.Count];
    FaceLandmarkerResultAnnotationController _controller;
    FaceLandmarkerRunner _runner;
    Renderer[] _annotationRenderers;
    bool[] _originalVisibility;
    Material _reconstruction, _composite, _originalMaterial;
    RenderTexture[] _known, _temp, _filled, _history;
    RenderTexture _donorTexture, _guideTexture;
    int _historyIndex, _version = -1, _size, _cameraWidth, _cameraHeight;
    bool _historyReady, _pointsReady, _boundImage, _maskReady;
    float _nextFind, _lastLandmarkTime, _lastSeen = -100f;
    float _nextShaderCheck;
    float _videoVisibility = 1f;
    Rect _controlRect;

    void OnEnable()
    {
        // A previous scene revision serialized these components. They must not
        // render a second copy of the effect after upgrading this script.
        var legacyRenderer = GetComponent<MeshRenderer>();
        if (legacyRenderer != null) legacyRenderer.enabled = false;
        _nextFind = 0;
        _nextShaderCheck = 0;
        _version = -1;
        Array.Clear(_landmarkTransforms, 0, _landmarkTransforms.Length);
        _pointsReady = _historyReady = _maskReady = false;
        FacePresence = 0;
        PresentationSeconds = GrowthProgress = 0;
        if (reconstructionShader == null) reconstructionShader = Shader.Find("Hidden/Faceless/SkinReconstruction");
        if (compositeShader == null) compositeShader = Shader.Find("Faceless/SkinComposite");
        if (surfaceShader == null) surfaceShader = Shader.Find("Hidden/Faceless/SurfaceGuard");
        if (!CheckShader(reconstructionShader) || !CheckShader(compositeShader) || !CheckShader(surfaceShader))
        {
            enabled = false;
            return;
        }
        _reconstruction = new Material(reconstructionShader) { hideFlags = HideFlags.HideAndDontSave };
        _composite = new Material(compositeShader) { hideFlags = HideFlags.HideAndDontSave };
        _composite.SetFloat("_Amount", 0);
        _videoVisibility = 1f;
        _composite.SetFloat("_VideoVisibility", _videoVisibility);
        _composite.SetVector("_FrameU", new Vector4(1, 0, 0, 0));
        _composite.SetVector("_FrameV", new Vector4(0, 1, 0, 0));
    }

    bool CheckShader(Shader shader)
    {
        if (shader == null)
        {
            Debug.LogError("Faceless: missing skin shader reference. The original camera material is retained.", this);
            return false;
        }
#if UNITY_EDITOR
        // isSupported alone does not reliably expose failed editor variants.
        if (UnityEditor.ShaderUtil.ShaderHasError(shader))
        {
            foreach (var message in UnityEditor.ShaderUtil.GetShaderMessages(shader))
                Debug.LogError($"Faceless shader {shader.name}: {message.message} ({message.file}:{message.line})", this);
            return false;
        }
#endif
        if (!shader.isSupported)
        {
            Debug.LogError($"Faceless: {shader.name} is unsupported on {SystemInfo.graphicsDeviceType}.", this);
            return false;
        }
        return true;
    }

    void FindReferences()
    {
        if (Time.unscaledTime < _nextFind) return;
        _nextFind = Time.unscaledTime + 0.5f;
        if (_runner == null) _runner = FindFirstObjectByType<FaceLandmarkerRunner>();
        if (screenImage == null)
        {
            foreach (var img in FindObjectsByType<RawImage>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (img.GetComponent<Mediapipe.Unity.Screen>() != null ||
                    img.GetComponentInParent<Mediapipe.Unity.Screen>() != null)
                { screenImage = img; break; }
            }
        }
        if (pointListAnnotation == null)
        {
            foreach (var face in FindObjectsByType<FaceLandmarkListAnnotation>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var points = face.GetComponentInChildren<PointListAnnotation>(true);
                if (points == null || points.transform.childCount < 468) continue;
                pointListAnnotation = points.transform;
                _controller = face.GetComponentInParent<FaceLandmarkerResultAnnotationController>();
                for (int i = 0; i < 468; i++) _landmarkTransforms[i] = pointListAnnotation.GetChild(i);
                CacheVisuals();
                break;
            }
        }
        else if (_landmarkTransforms[0] == null && pointListAnnotation.childCount >= 468)
        {
            _controller = pointListAnnotation.GetComponentInParent<FaceLandmarkerResultAnnotationController>();
            for (int i = 0; i < 468; i++) _landmarkTransforms[i] = pointListAnnotation.GetChild(i);
            CacheVisuals();
        }
        if (_controller == null)
            _controller = FindFirstObjectByType<FaceLandmarkerResultAnnotationController>();
    }

    void CacheVisuals()
    {
        RestoreVisuals();
        Transform root = pointListAnnotation.parent;
        if (root.parent != null && root.parent.name.Contains("FaceLandmarkListWithIris")) root = root.parent;
        _annotationRenderers = root.GetComponentsInChildren<Renderer>(true);
        _originalVisibility = new bool[_annotationRenderers.Length];
        for (int i = 0; i < _annotationRenderers.Length; i++)
            _originalVisibility[i] = _annotationRenderers[i].forceRenderingOff;
    }

    void LateUpdate()
    {
        if (_reconstruction == null || _composite == null) return;
        if (Time.unscaledTime >= _nextShaderCheck)
        {
            _nextShaderCheck = Time.unscaledTime + 0.5f;
            if (!CheckShader(reconstructionShader) || !CheckShader(compositeShader) || !CheckShader(surfaceShader))
            {
                enabled = false; // OnDisable restores the original camera material.
                return;
            }
        }
        FindReferences();
        if (screenImage == null || screenImage.texture == null) return;
        Texture source = screenImage.texture;
        if (source.width < 32 || source.height < 32) return;
        if (_cameraWidth != source.width || _cameraHeight != source.height)
        {
            _cameraWidth = source.width; _cameraHeight = source.height;
            _pointsReady = _historyReady = _maskReady = false;
        }
        float now = Time.unscaledTime;
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
        bool paired = _runner != null && _runner.PreviewIsSynchronized;
        if (paired && _runner.LastMatchedFrameTime < 0f)
        {
            // Camera restart: never paint a previous visitor's cached skin
            // over the new session's initial black capture buffer.
            _pointsReady = _historyReady = _maskReady = false;
            FacePresence = _videoVisibility = 0f;
        }
        bool valid = _controller != null && _controller.HasFace
            && now - _controller.LastResultTime < 0.5f
            && (!paired || _runner.PreviewHasFace)
            && pointListAnnotation != null && pointListAnnotation.gameObject.activeInHierarchy
            && _landmarkTransforms[0] != null;
        IsTracking = valid;
        if (valid)
        {
            bool reacquired = now - _lastSeen > 0.5f;
            // A back/profile turn is indistinguishable from absence to a
            // single face detector. Do not reveal features by automatically
            // restarting the entry timeline on a paired-preview reacquisition.
            if (!paired && now - _lastSeen > resetAfterAbsence)
            { PresentationSeconds = 0; GrowthProgress = 0; }
            if (reacquired) _pointsReady = _historyReady = false;
            _lastSeen = now;
            if (_version != _controller.ResultVersion || !_pointsReady)
            {
                UpdateLandmarks();
                _version = _controller.ResultVersion;
                _maskReady = _regions.Build(_points, maskScale, featherFraction)
                    && _surface.Render(_lastRaw, _cameraWidth, _cameraHeight, surfaceShader);
            }
            else if (_pointsReady) _maskReady = _surface.Texture != null
                && _regions.Build(_points, maskScale, featherFraction);
            FacePresence = paired ? 1f : Mathf.MoveTowards(FacePresence, 1f, dt / 0.18f);
            _videoVisibility = Mathf.MoveTowards(_videoVisibility, 1f, dt / 0.18f);
            PresentationSeconds += dt;
        }
        else
        {
            if (paired && _historyReady)
            {
                // The runner holds the last paired frame. Keep its erasure
                // intact; fade the entire held image to black on tracking loss
                // instead of exposing the original features through the mask.
                if (now - _lastSeen > 0.18f)
                    _videoVisibility = Mathf.MoveTowards(_videoVisibility, 0f, dt / 0.25f);
            }
            else
                FacePresence = Mathf.MoveTowards(FacePresence, 0f, dt / Mathf.Max(0.08f, lostFaceFadeSeconds));
            GrowthProgress = Mathf.MoveTowards(GrowthProgress, 0f, dt / 0.8f);
            if (FacePresence == 0f) _historyReady = false;
        }
        if (!_maskReady)
        {
            FacePresence = 0f;
            if (paired) _videoVisibility = 0f;
        }
        UpdateStages();
        if (valid && _maskReady)
        {
            if (!EnsureTextures()) return;
            RenderSkin(source, dt);
        }
        // Paired preview starts black and stays protected while its first
        // surface/skin frame is prepared. Unpaired preview keeps the old rule.
        if (!_boundImage && (_historyReady || paired))
        {
            _originalMaterial = screenImage.material;
            screenImage.material = _composite;
            _boundImage = true;
        }
        ApplyComposite(_composite);
        // UI masking may return a cached stencil-material instance.
        Material drawing = screenImage.materialForRendering;
        if (_boundImage && drawing != _composite && drawing != null) ApplyComposite(drawing);
        if (_annotationRenderers != null)
        {
            for (int i = 0; i < _annotationRenderers.Length; i++)
            {
                var r = _annotationRenderers[i];
                if (r != null)
                    r.forceRenderingOff = !showLandmarks || !r.transform.IsChildOf(pointListAnnotation);
            }
        }
        FrameUpdated?.Invoke(this);
    }

    Vector2 LandmarkPixel(Transform t)
    {
        // Project through the SAME Canvas camera and RawImage rect/uvRect as
        // the source. Handles mirrored feeds, fit/resize, and rotated display.
        Canvas canvas = screenImage.canvas;
        Camera camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        Vector2 screenPoint = RectTransformUtility.WorldToScreenPoint(camera, t.position);
        RectTransformUtility.ScreenPointToLocalPointInRectangle(screenImage.rectTransform, screenPoint, camera, out Vector2 local);
        Rect rect = screenImage.rectTransform.rect, uv = screenImage.uvRect;
        return new Vector2((uv.x + (local.x - rect.xMin) / rect.width * uv.width) * _cameraWidth,
            (uv.y + (local.y - rect.yMin) / rect.height * uv.height) * _cameraHeight);
    }

    void UpdateLandmarks()
    {
        float time = _controller.LastResultTime;
        float dt = Mathf.Clamp(time - _lastLandmarkTime, 0.008f, 0.2f);
        _lastLandmarkTime = time;
        Vector2 nose = LandmarkPixel(_landmarkTransforms[1]);
        if (_pointsReady && Vector2.Distance(nose, _points[1]) > Mathf.Max(_regions.Width * 0.4f, 30f))
            _pointsReady = _historyReady = false;
        for (int i = 0; i < 468; i++)
        {
            Vector2 raw = LandmarkPixel(_landmarkTransforms[i]);
            if (!_pointsReady) { _points[i] = _lastRaw[i] = raw; _velocity[i] = Vector2.zero; continue; }
            Vector2 speed = (raw - _lastRaw[i]) / dt;
            _velocity[i] = Vector2.Lerp(_velocity[i], speed, 1f - Mathf.Exp(-dt * 12f));
            float cutoff = landmarkCutoff + 14f * _velocity[i].magnitude / Mathf.Max(_regions.Width, 40f);
            float alpha = 1f / (1f + 1f / (2f * Mathf.PI * cutoff * dt));
            _points[i] = Vector2.Lerp(_points[i], raw, alpha);
            // Keep temporal smoothing subpixel in paired mode. A slow mask
            // over a newer image reveals features even with accurate tracking.
            if (_runner != null && _runner.PreviewIsSynchronized)
                _points[i] = raw + Vector2.ClampMagnitude(_points[i] - raw, 0.35f);
            _lastRaw[i] = raw;
        }
        _pointsReady = true;
    }

    void UpdateStages()
    {
        if (!playEntryAnimation) { for (int i = 0; i < _stages.Length; i++) _stages[i] = 1f; GrowthProgress = 0; return; }
        float t = PresentationSeconds;
        _stages[0] = _stages[1] = Ramp(t, 3, 6);
        _stages[2] = Ramp(t, 5, 8);
        _stages[3] = Ramp(t, 6, 9);
        _stages[4] = Ramp(t, 8, 11);
        _stages[5] = Ramp(t, 9, 12);
        _stages[6] = Ramp(t, 12, 15);
        _stages[7] = _stages[8] = Ramp(t, 6, 9); // nose-side folds
        _stages[9] = _stages[10] = Ramp(t, 9, 12); // below mouth corners
        _stages[11] = _stages[12] = Ramp(t, 6, 9); // inner upper-cheek reflections
        _stages[13] = _stages[14] = Ramp(t, 12, 15); // inner chin transitions
        if (IsTracking) GrowthProgress = Ramp(t, 15, 24);
    }
    static float Ramp(float t, float a, float b) => Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(a, b, t));

    bool EnsureTextures()
    {
        int size = Mathf.ClosestPowerOfTwo(Mathf.Clamp(reconstructionResolution, 128, 512));
        if (_known != null && size == _size) return true;
        ReleaseTextures();
        RenderTextureFormat format = RenderTextureFormat.ARGBHalf;
        if (!SystemInfo.SupportsRenderTextureFormat(format)) format = RenderTextureFormat.ARGBFloat;
        if (!SystemInfo.SupportsRenderTextureFormat(format))
        {
            Debug.LogError("Faceless: this GPU cannot render floating-point skin buffers.", this);
            enabled = false; return false;
        }
        _size = size;
        int levels = 1;
        for (int s = size; s > 4; s >>= 1) levels++;
        _known = new RenderTexture[levels]; _temp = new RenderTexture[levels]; _filled = new RenderTexture[levels];
        for (int i = 0, s = size; i < levels; i++, s >>= 1)
        {
            _known[i] = NewTexture(s,s,format,"Trusted skin");
            _temp[i] = NewTexture(s,s,format,"Skin filter");
            _filled[i] = NewTexture(s,s,format,"Skin reconstruction");
        }
        _history = new[] {NewTexture(size,size,format,"Skin history A"),NewTexture(size,size,format,"Skin history B")};
        _donorTexture = NewTexture(6,1,format,"Cheek samples");
        _guideTexture = NewTexture(size,size,format,"Local skin illumination");
        _historyReady = false;
        return true;
    }
    static RenderTexture NewTexture(int w, int h, RenderTextureFormat format, string label)
    {
        var rt = new RenderTexture(w,h,0,format,RenderTextureReadWrite.Linear)
        {
            name=label, filterMode=FilterMode.Bilinear, wrapMode=TextureWrapMode.Clamp,
            useMipMap=false, autoGenerateMips=false, antiAliasing=1, hideFlags=HideFlags.HideAndDontSave
        };
        rt.Create(); return rt;
    }

    void RenderSkin(Texture source, float dt)
    {
        _regions.SetMaterial(_reconstruction);
        _reconstruction.SetFloat("_LocalColorStrength", localColorStrength);
        _reconstruction.SetFloat("_HighlightSuppression", highlightSuppression);
        _reconstruction.SetVector("_CameraSize", new Vector4(_cameraWidth,_cameraHeight,1f/_cameraWidth,1f/_cameraHeight));
        RenderTexture previous = RenderTexture.active;
        bool srgb = GL.sRGBWrite;
        try
        {
            GL.sRGBWrite = false; // RGBAHalf moments are numerical linear buffers.
            Graphics.Blit(source,_donorTexture,_reconstruction,0);
            _reconstruction.SetTexture("_DonorTex",_donorTexture);
            Graphics.Blit(source,_guideTexture,_reconstruction,7);
            _reconstruction.SetTexture("_GuideTex",_guideTexture);
            Graphics.Blit(source,_known[0],_reconstruction,1);
            for (int i=0;i<_known.Length-1;i++)
            {
                _reconstruction.SetVector("_Direction",new Vector4(1,0,0,0));
                Graphics.Blit(_known[i],_temp[i],_reconstruction,2);
                _reconstruction.SetVector("_Direction",new Vector4(0,1,0,0));
                Graphics.Blit(_temp[i],_known[i+1],_reconstruction,2);
            }
            int last=_known.Length-1;
            Graphics.Blit(_known[last],_filled[last],_reconstruction,3);
            for (int i=last-1;i>=0;i--)
            {
                _reconstruction.SetTexture("_KnownTex",_known[i]);
                Graphics.Blit(_filled[i+1],_filled[i],_reconstruction,4);
                for (int sweep=0;sweep<2;sweep++)
                {
                    _reconstruction.SetVector("_Direction",new Vector4(1,0,0,0));
                    Graphics.Blit(_filled[i],_temp[i],_reconstruction,5);
                    _reconstruction.SetVector("_Direction",new Vector4(0,1,0,0));
                    Graphics.Blit(_temp[i],_filled[i],_reconstruction,5);
                }
            }
            int next=1-_historyIndex;
            // Buffers above hold signed color residuals. Add the spatial
            // illumination back before temporal smoothing / final composition.
            Graphics.Blit(_filled[0],_temp[0],_reconstruction,8);
            if (!_historyReady) Graphics.Blit(_temp[0],_history[next]);
            else
            {
                _reconstruction.SetTexture("_HistoryTex",_history[_historyIndex]);
                _reconstruction.SetFloat("_TemporalWeight",1f-Mathf.Exp(-dt/Mathf.Max(colorSmoothingSeconds,0.001f)));
                Graphics.Blit(_temp[0],_history[next],_reconstruction,6);
            }
            _historyIndex=next; _historyReady=true;
        }
        finally { RenderTexture.active=previous; GL.sRGBWrite=srgb; }
    }

    void ApplyComposite(Material m)
    {
        m.SetFloat("_VideoVisibility", _videoVisibility);
        m.SetFloat("_Amount", _maskReady && _history != null ? FacePresence*effectAmount : 0f);
        if (!_maskReady || _history == null) return;
        _regions.SetMaterial(m);
        m.SetVector("_CameraSize",new Vector4(_cameraWidth,_cameraHeight,1f/_cameraWidth,1f/_cameraHeight));
        m.SetFloatArray("_Stages",_stages);
        m.SetTexture("_SkinTex",_history[_historyIndex]);
        m.SetTexture("_SurfaceTex",_surface.Texture);
        m.SetFloat("_Volume",volume); m.SetFloat("_Grain",fineGrain);
        m.SetFloat("_ShowMask",showMask ? 1f : 0f);
    }

    /// <summary>Anchors for future plant prefabs. World position lies on the video
    /// surface; rotation is the tracked head pose adjusted for preview mirroring.
    /// Recommended ids: 168 (bridge), 6 (between eyes), 0 (above mouth).</summary>
    public bool TryGetSurfaceAnchor(int landmarkId, out Pose pose, out float faceWidthWorld)
    {
        pose=default; faceWidthWorld=0;
        if (!IsTracking || !_pointsReady || screenImage == null || landmarkId<0 || landmarkId>=468) return false;
        Rect rect=screenImage.rectTransform.rect, uv=screenImage.uvRect;
        Vector2 p=_points[landmarkId];
        Vector3 local=new Vector3(rect.xMin+(p.x/_cameraWidth-uv.x)/uv.width*rect.width,
            rect.yMin+(p.y/_cameraHeight-uv.y)/uv.height*rect.height,0);
        Matrix4x4 reflection=Matrix4x4.Scale(new Vector3(Mathf.Sign(uv.width),Mathf.Sign(uv.height),1));
        Quaternion rotation=_controller.HasFacePose ? (reflection*HeadPose*reflection).rotation : Quaternion.identity;
        pose=new Pose(screenImage.rectTransform.TransformPoint(local),screenImage.rectTransform.rotation*rotation);
        faceWidthWorld=screenImage.rectTransform.TransformVector(Vector3.right*(_regions.Width/_cameraWidth*rect.width)).magnitude;
        return true;
    }

    [ContextMenu("Replay entry / 重播入场")]
    public void ReplayEntry() { playEntryAnimation=true; PresentationSeconds=GrowthProgress=0; }
    [ContextMenu("Show final skin / 显示最终皮肤")]
    public void ShowFinalSkin() { playEntryAnimation=false; effectAmount=1f; GrowthProgress=0; }

    void OnGUI()
    {
        if (Event.current.type==EventType.KeyDown && Event.current.keyCode==KeyCode.H)
        { showControls=!showControls; Event.current.Use(); }
        if (Event.current.type==EventType.KeyDown && Event.current.keyCode==KeyCode.R)
        { ReplayEntry(); Event.current.Use(); }
        if (!showControls) return;
        _controlRect=new Rect(UnityEngine.Screen.width-244,12,232,244);
        GUI.Window(GetInstanceID(),_controlRect,DrawControls,"Faceless / Skin");
    }
    void DrawControls(int id)
    {
        GUILayout.Label(IsTracking ? "Tracking / "+Mathf.RoundToInt(FacePresence*100)+"%" : "Waiting for face");
        if (_runner != null && _runner.PreviewIsSynchronized) GUILayout.Label("Paired camera + landmarks");
        GUILayout.Label("Erase / "+effectAmount.ToString("0.00"));
        effectAmount=GUILayout.HorizontalSlider(effectAmount,0,1);
        showLandmarks=GUILayout.Toggle(showLandmarks,"468 landmarks");
        showMask=GUILayout.Toggle(showMask,"Regions + boundary");
        if (GUILayout.Button("Final skin")) ShowFinalSkin();
        if (GUILayout.Button("Replay entry (R)")) ReplayEntry();
        if (GUILayout.Button("Hide controls (H)")) showControls=false;
    }

    void RestoreVisuals()
    {
        if (_annotationRenderers==null) return;
        for (int i=0;i<_annotationRenderers.Length;i++)
            if (_annotationRenderers[i]!=null) _annotationRenderers[i].forceRenderingOff=_originalVisibility[i];
        _annotationRenderers=null;
    }
    static void Release(RenderTexture rt) { if (rt!=null) {rt.Release(); Destroy(rt);} }
    void ReleaseTextures()
    {
        if (_known!=null) foreach(var t in _known) Release(t);
        if (_temp!=null) foreach(var t in _temp) Release(t);
        if (_filled!=null) foreach(var t in _filled) Release(t);
        if (_history!=null) foreach(var t in _history) Release(t);
        Release(_donorTexture);
        Release(_guideTexture);
        _known=_temp=_filled=_history=null; _donorTexture=_guideTexture=null; _historyReady=false;
    }
    void OnDisable()
    {
        if (_boundImage && screenImage!=null && screenImage.material==_composite) screenImage.material=_originalMaterial;
        _boundImage=false;
        RestoreVisuals(); ReleaseTextures();
        _surface.Dispose();
        if (_reconstruction!=null) Destroy(_reconstruction);
        if (_composite!=null) Destroy(_composite);
        _reconstruction=_composite=null;
        IsTracking=false; FacePresence=GrowthProgress=0;
    }
}
