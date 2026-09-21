using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One independent plant colony, rendered with depth into a transparent UI layer.
/// Roots use each person's current landmarks. No camera pixels are modified.</summary>
public sealed class NarcissusFaceGrowth : IDisposable
{
    // Centre first, then forehead, eyes, nasolabial folds, mouth and chin.
    public static readonly int[] Anchors={168,6,2,0,17,108,9,337,105,66,107,336,296,334,
        159,133,362,386,50,100,329,280,187,203,423,411,61,40,270,291,211,181,405,431,170,200,395,175};
    public static float Delay(int plant)
    {
        if (plant<5) return plant*.7f;
        // Every last flower finishes opening at t=21; delays never exceed 11.
        return 3f+8f*(plant-5)/(Anchors.Length-6f);
    }
    static NarcissusModel _model;
    readonly MidFaceEraseMask _owner;
    RawImage _image;
    Mesh _mesh;
    Material _material;
    RenderTexture _target;
    Vector3[] _vertices;
    float _lastRender=-100;
    bool _failed;
    int _trackId=-1;
    public NarcissusFaceGrowth(MidFaceEraseMask owner) { _owner=owner; }

    public void Update(FacelessFaceRenderer face,RawImage skinLayer,int cameraWidth,int cameraHeight,float visibility)
    {
        if (face==null || !face.Ready || !face.IsTracking || skinLayer==null || !skinLayer.enabled ||
            !_owner.growNarcissus || !_owner.playEntryAnimation)
        { if (_image!=null) _image.enabled=false; return; }
        float seconds=Mathf.Max(0,face.PresentationSeconds-Mathf.Max(.1f,_owner.entryDurationSeconds));
        seconds=seconds*21f/Mathf.Max(.1f,_owner.growthDurationSeconds);
        if (seconds<=0) { if (_image!=null) _image.enabled=false; return; }
        if (_trackId!=face.TrackId) { _trackId=face.TrackId; _lastRender=-100; }
        if (_image!=null) { _image.enabled=true; _image.color=new Color(1,1,1,visibility); }
        // Shared matched landmarks/clock, capped at 20 geometry uploads a second.
        if (Time.unscaledTime-_lastRender<.05f) return;
        _lastRender=Time.unscaledTime;
        RenderTexture previous=RenderTexture.active;
        bool previousSrgb=GL.sRGBWrite;
        try
        {
            EnsureResources(skinLayer);
            float width=face.Regions.Width;
            Rect bounds=face.Regions.Bounds;
            bounds.xMin-=width*.19f; bounds.xMax+=width*.19f;
            bounds.yMin-=width*.19f; bounds.yMax+=width*.19f;
            Quaternion rotation=face.HasPose?face.HeadPose.rotation:Quaternion.identity;
            Vector3 normal=rotation*Vector3.forward;
            float denominator=Mathf.Max(.3f,Mathf.Abs(normal.z));
            float slopeX=-normal.x/denominator, slopeY=-normal.y/denominator;
            Vector2 center=face.Points[168];
            for (int flower=0; flower<Anchors.Length; flower++)
            {
                Vector2 p=face.Points[Anchors[flower]];
                Vector3 root=new Vector3(p.x,p.y,(p.x-center.x)*slopeX+(p.y-center.y)*slopeY);
                float variation=.94f+.09f*Mathf.Sin(flower*2.399f);
                // Bloom radius ~12% of face width; overlapping corollas cover the central skin.
                float scale=width*.12f*variation;
                Quaternion twist=Quaternion.AngleAxis(flower*137.508f,Vector3.forward);
                _model.Evaluate(seconds,Delay(flower),Time.unscaledTime*1.15f+flower*.7f,
                    root,rotation*twist,scale,_vertices,flower*_model.VertexCount);
            }
            _mesh.vertices=_vertices;
            _mesh.RecalculateNormals();
            _mesh.bounds=new Bounds(new Vector3(bounds.center.x,bounds.center.y,0),new Vector3(bounds.width,bounds.height,width*12));
            _material.SetVector("_PlantBounds",new Vector4(bounds.xMin,bounds.yMin,bounds.width,bounds.height));
            _material.SetFloat("_DepthScale",Mathf.Max(width*12,1));
            int rw=Mathf.Max(64,Mathf.RoundToInt(512*bounds.width/Mathf.Max(bounds.width,bounds.height)));
            int rh=Mathf.Max(64,Mathf.RoundToInt(512*bounds.height/Mathf.Max(bounds.width,bounds.height)));
            if (_target==null || _target.width!=rw || _target.height!=rh)
            {
                // Quantize dimensions to reduce reallocations on small head motion.
                rw=Mathf.CeilToInt(rw/64f)*64; rh=Mathf.CeilToInt(rh/64f)*64;
                if (_target==null || _target.width!=rw || _target.height!=rh)
                {
                    if (_target!=null) { _target.Release(); Release(_target); }
                    _target=new RenderTexture(rw,rh,24,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear)
                    { name="Narcissus colony "+_trackId,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp,
                        hideFlags=HideFlags.HideAndDontSave,useMipMap=false,antiAliasing=1 };
                    if (!_target.Create()) throw new InvalidOperationException("Cannot allocate narcissus image.");
                }
            }
            if (!_target.IsCreated() && !_target.Create()) throw new InvalidOperationException("Cannot restore narcissus image.");
            Graphics.SetRenderTarget(_target); GL.sRGBWrite=false;
            GL.Clear(true,true,Color.clear);
            if (!_material.SetPass(0)) throw new InvalidOperationException("Narcissus material pass is unavailable.");
            Graphics.DrawMeshNow(_mesh,Matrix4x4.identity);
            _image.texture=_target;
            Fit(bounds,skinLayer.uvRect,cameraWidth,cameraHeight);
            _image.color=new Color(1,1,1,visibility); _image.enabled=true;
            _failed=false;
        }
        catch (Exception error)
        {
            if (_image!=null) _image.enabled=false;
            if (!_failed) Debug.LogError("Narcissus growth: "+error.Message,_owner);
            _failed=true;
        }
        finally { RenderTexture.active=previous; GL.sRGBWrite=previousSrgb; }
    }

    void Fit(Rect bounds,Rect parentUV,int w,int h)
    {
        float x0=(bounds.xMin/w-parentUV.x)/parentUV.width, x1=(bounds.xMax/w-parentUV.x)/parentUV.width;
        float y0=(bounds.yMin/h-parentUV.y)/parentUV.height, y1=(bounds.yMax/h-parentUV.y)/parentUV.height;
        _image.rectTransform.anchorMin=new Vector2(Mathf.Min(x0,x1),Mathf.Min(y0,y1));
        _image.rectTransform.anchorMax=new Vector2(Mathf.Max(x0,x1),Mathf.Max(y0,y1));
        _image.rectTransform.offsetMin=_image.rectTransform.offsetMax=Vector2.zero;
        _image.uvRect=new Rect(parentUV.width<0?1:0,parentUV.height<0?1:0,Mathf.Sign(parentUV.width),Mathf.Sign(parentUV.height));
    }
    void EnsureResources(RawImage parent)
    {
        if (_model==null) _model=new NarcissusModel();
        if (_material==null)
        {
            var shader=Resources.Load<Shader>("FacelessNarcissus");
            if (shader==null || !shader.isSupported) throw new InvalidOperationException("Missing/unsupported FacelessNarcissus shader.");
            _material=new Material(shader) {hideFlags=HideFlags.HideAndDontSave};
        }
        if (_mesh==null)
        {
            int count=_model.VertexCount*Anchors.Length;
            _vertices=new Vector3[count]; var uv=new Vector2[count]; var colors=new Color[count];
            var indices=new int[_model.Triangles.Length*Anchors.Length];
            for (int flower=0;flower<Anchors.Length;flower++)
            {
                int first=flower*_model.VertexCount;
                Array.Copy(_model.UV,0,uv,first,_model.VertexCount);
                Array.Copy(_model.Colors,0,colors,first,_model.VertexCount);
                for (int j=0;j<_model.Triangles.Length;j++) indices[flower*_model.Triangles.Length+j]=first+_model.Triangles[j];
            }
            _mesh=new Mesh {name="Narcissus living colony",hideFlags=HideFlags.HideAndDontSave};
            _mesh.MarkDynamic(); _mesh.vertices=_vertices; _mesh.uv=uv; _mesh.colors=colors; _mesh.triangles=indices;
        }
        if (_image==null)
        {
            var go=new GameObject("Narcissus growth",typeof(RectTransform),typeof(CanvasRenderer),typeof(RawImage));
            go.hideFlags=HideFlags.DontSave; go.layer=parent.gameObject.layer;
            go.transform.SetParent(parent.transform,false);
            _image=go.GetComponent<RawImage>(); _image.raycastTarget=false;
        }
    }
    public void Dispose()
    {
        if (_image!=null) { _image.enabled=false; Release(_image.gameObject); }
        if (_target!=null) { _target.Release(); Release(_target); }
        Release(_mesh); Release(_material);
        _image=null; _target=null; _mesh=null; _material=null; _vertices=null;
    }
    static void Release(UnityEngine.Object value)
    { if (value==null) return; if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); }
}
