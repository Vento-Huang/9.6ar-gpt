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
    Material _material, _displayMaterial;
    Mesh _pollenMesh;
    Vector3[] _pollenVertices;
    Color[] _pollenColors;
    const int RootCount=12, RootSteps=12, RootSides=6, PollenCount=24;
    readonly NarcissusColonyLayout _layout=new NarcissusColonyLayout();
    readonly Vector3[] _roots=new Vector3[38], _desired=new Vector3[38], _baseCenters=new Vector3[38];
    readonly Quaternion[] _rotations=new Quaternion[38];
    readonly float[] _radii=new float[38];
    bool _layoutReady, _trackingReady;
    readonly Vector2[] _stablePoints=new Vector2[468];
    Quaternion _stableRotation;
    float _stableWidth;
    readonly Vector3[] _previousRoots=new Vector3[38],_sway=new Vector3[38],_swayVelocity=new Vector3[38];
    readonly Transform[] _rootBones=new Transform[38],_midBones=new Transform[38],_tipBones=new Transform[38];
    GameObject _rigObject;
    Vector3 _previousNormal;
    const float FlowerScale=1.35f;
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
        { if (_image!=null) _image.enabled=false; _trackingReady=false; return; }
        float seconds=Mathf.Max(0,face.PresentationSeconds-Mathf.Max(.1f,_owner.entryDurationSeconds));
        seconds=seconds*21f/Mathf.Max(.1f,_owner.growthDurationSeconds);
        if (seconds<=0) { if (_image!=null) _image.enabled=false; _trackingReady=false; return; }
        if (_trackId!=face.TrackId) { _trackId=face.TrackId; _lastRender=-100; _layout.Reset(_trackId); _layoutReady=false; _trackingReady=false; }
        if (_image!=null) { _image.enabled=true; _image.color=new Color(1,1,1,visibility); }
        // Shared matched landmarks/clock, capped at 20 geometry uploads a second.
        if (Time.unscaledTime-_lastRender<.05f) return;
        float dt=Mathf.Min(.1f,Mathf.Max(.001f,Time.unscaledTime-_lastRender));
        _lastRender=Time.unscaledTime;
        RenderTexture previous=RenderTexture.active;
        bool previousSrgb=GL.sRGBWrite;
        try
        {
            EnsureResources(skinLayer);
            SmoothTracking(face,dt);
            float width=_stableWidth;
            Rect bounds=face.Regions.Bounds;
            bounds.xMin-=width*.19f; bounds.xMax+=width*.19f;
            bounds.yMin-=width*.19f; bounds.yMax+=width*.19f;
            Quaternion rotation=_stableRotation;
            Vector3 normal=rotation*Vector3.forward;
            float denominator=Mathf.Max(.3f,Mathf.Abs(normal.z));
            float slopeX=-normal.x/denominator, slopeY=-normal.y/denominator;
            Vector2 center=_stablePoints[168];
            // Plan the mature tangential layout once per track. This avoids
            // nearest-candidate switches as buds open or the head moves.
            if (!_layoutReady)
            {
                PrepareCrowns(face,rotation,width,center,slopeX,slopeY,100f);
                _layout.Solve(_desired,_radii,rotation,width); _layoutReady=true;
            }
            PrepareCrowns(face,rotation,width,center,slopeX,slopeY,seconds);
            _layout.Solve(_desired,_radii,rotation,width);
            UpdateRigMotion(rotation,width,dt);
            for (int flower=0; flower<Anchors.Length; flower++)
            {
                _model.Evaluate(seconds,_layout.Delays[flower],0f,
                    _roots[flower],_rotations[flower],width*_layout.Sizes[flower]*FlowerScale,_vertices,flower*_model.VertexCount,
                    _layout.Centers[flower]-_baseCenters[flower],true,_sway[flower]*NarcissusModel.Ease(seconds,_layout.Delays[flower],_layout.Delays[flower]+6));
                UpdateBoneNodes(flower,seconds,width);
            }
            BuildRoots(seconds,rotation,width);
            // Include spaced crowns and drifting pollen in the cropped image.
            for(int i=0;i<_vertices.Length;i++)
            {
                Vector3 v=_vertices[i];
                bounds.xMin=Mathf.Min(bounds.xMin,v.x-width*.03f);bounds.xMax=Mathf.Max(bounds.xMax,v.x+width*.03f);
                bounds.yMin=Mathf.Min(bounds.yMin,v.y-width*.03f);bounds.yMax=Mathf.Max(bounds.yMax,v.y+width*.03f);
            }
            BuildPollen(seconds,rotation,width,ref bounds);
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
            if (_owner.showPollen && _material.SetPass(1)) Graphics.DrawMeshNow(_pollenMesh,Matrix4x4.identity);
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

    void SmoothTracking(FacelessFaceRenderer face,float dt)
    {
        float width=Mathf.Max(1,face.Regions.Width);
        Quaternion pose=face.HasPose?face.HeadPose.rotation:Quaternion.identity;
        bool first=!_trackingReady;
        if(first) { _stableWidth=width;_stableRotation=pose; }
        float widthDelta=width-_stableWidth;
        if(Mathf.Abs(widthDelta)>width*.003f)
            _stableWidth+=widthDelta*(1-(float)Math.Exp(-12*dt));
        float angle=Quaternion.Angle(_stableRotation,pose);
        if(angle>.5f) _stableRotation=Quaternion.Slerp(_stableRotation,pose,1-(float)Math.Exp(-(angle>5?22:9)*dt));
        for(int i=0;i<_stablePoints.Length;i++)
        {
            Vector2 raw=face.Points[i];
            if(first) { _stablePoints[i]=raw;continue; }
            Vector2 delta=raw-_stablePoints[i];
            float distance=delta.magnitude,dead=width*.0025f;
            if(distance>dead)
                _stablePoints[i]+=delta*((1-dead/distance)*(1-(float)Math.Exp(-(distance>width*.025f?28:12)*dt)));
        }
        if(first)
        {
            Array.Clear(_sway,0,_sway.Length);Array.Clear(_swayVelocity,0,_swayVelocity.Length);
            _previousNormal=_stableRotation*Vector3.forward;
        }
        // Root history is initialized by UpdateRigMotion after PrepareCrowns.
    }
    void UpdateRigMotion(Quaternion rotation,float width,float dt)
    {
        Vector3 normal=rotation*Vector3.forward;
        Vector3 turn=normal-_previousNormal;
        for(int i=0;i<38;i++)
        {
            Vector3 movement=_trackingReady?(_roots[i]-_previousRoots[i])/width+turn*.16f:Vector3.zero;
            if(movement.magnitude>.004f)
                _swayVelocity[i]-=Vector3.ClampMagnitude(movement,.06f)*(width*2.2f);
            // Small fixed substeps keep spring damping stable at variable frame rates.
            int steps=Mathf.Max(1,Mathf.CeilToInt(dt/.01f));float h=dt/steps;
            for(int step=0;step<steps;step++)
            {
                _swayVelocity[i]+=(-_sway[i]*95f-_swayVelocity[i]*17f)*h;
                _sway[i]+=_swayVelocity[i]*h;
                _sway[i]=Vector3.ClampMagnitude(_sway[i],width*.012f);
            }
            if(_sway[i].magnitude<width*.00005f && _swayVelocity[i].magnitude<width*.0002f)
            { _sway[i]=Vector3.zero;_swayVelocity[i]=Vector3.zero; }
            _previousRoots[i]=_roots[i];
        }
        _previousNormal=normal;_trackingReady=true;
    }
    void UpdateBoneNodes(int i,float seconds,float width)
    {
        if(_rigObject==null)
        {
            _rigObject=new GameObject("Narcissus joints (camera pixel space)");
            _rigObject.hideFlags=HideFlags.DontSave;_rigObject.transform.SetParent(_owner.transform,false);
            for(int n=0;n<38;n++)
            {
                _rootBones[n]=new GameObject("Flower "+n+" Root").transform;_rootBones[n].SetParent(_rigObject.transform,false);
                _midBones[n]=new GameObject("Stem joint").transform;_midBones[n].SetParent(_rootBones[n],false);
                _tipBones[n]=new GameObject("Flower joint").transform;_tipBones[n].SetParent(_midBones[n],false);
            }
        }
        float growth=NarcissusModel.Ease(seconds,_layout.Delays[i],_layout.Delays[i]+6);
        Vector3 baseTip=_rotations[i]*(NarcissusModel.AttachedBase*(width*_layout.Sizes[i]*FlowerScale*growth));
        Vector3 bend=_layout.Centers[i]-_baseCenters[i],motion=_sway[i]*growth;
        Vector3 middle=baseTip*.5f+bend*.25f+motion*.35f;
        _rootBones[i].localPosition=_roots[i];
        _midBones[i].localPosition=middle;
        _tipBones[i].localPosition=baseTip+bend+motion-middle;
    }

    void PrepareCrowns(FacelessFaceRenderer face,Quaternion rotation,float width,Vector2 center,float slopeX,float slopeY,float seconds)
    {
        for(int i=0;i<Anchors.Length;i++)
        {
            Vector2 p=_stablePoints[Anchors[i]];
            _roots[i]=new Vector3(p.x,p.y,(p.x-center.x)*slopeX+(p.y-center.y)*slopeY);
            _rotations[i]=rotation*_layout.Rotations[i];
            float scale=width*_layout.Sizes[i]*FlowerScale, delay=_layout.Delays[i];
            float stem=NarcissusModel.Ease(seconds,delay,delay+6), bud=NarcissusModel.Ease(seconds,delay+2,delay+6);
            _baseCenters[i]=_roots[i]+_rotations[i]*((NarcissusModel.AttachedBase*stem+new Vector3(0,0,.12f)*bud)*scale);
            _desired[i]=_baseCenters[i]+rotation*(_layout.Jitter[i]*(width*stem));
            _radii[i]=scale*1.13f*bud;
        }
    }
    void BuildRoots(float seconds,Quaternion rotation,float width)
    {
        Vector3 normal=rotation*Vector3.forward,up=rotation*Vector3.up;
        int first=_model.VertexCount*Anchors.Length;
        for(int root=0;root<RootCount;root++)
        {
            int end=4+root*3;
            Vector3 a=_roots[root%3]+normal*(width*.008f),c=_roots[end]+normal*(width*.008f);
            Vector3 b=(a+c)*.5f+up*(Mathf.Sin(root*2.4f)*width*.05f)+normal*(width*.025f);
            float growth=NarcissusModel.Ease(seconds,_layout.Delays[end]*.6f,_layout.Delays[end]*.6f+8);
            for(int row=0;row<=RootSteps;row++)
            {
                float t=row/(float)RootSteps*growth;
                Vector3 p=a*((1-t)*(1-t))+b*(2*(1-t)*t)+c*(t*t);
                Vector3 tangent=(b-a)*(1-t)+(c-b)*t;
                Vector3 sideAxis=Vector3.Cross(tangent,normal).normalized;
                float radius=width*.0022f*growth*(1-.6f*t);
                for(int side=0;side<RootSides;side++)
                {
                    float angle=side*Mathf.PI*2/RootSides;
                    _vertices[first+root*(RootSteps+1)*RootSides+row*RootSides+side]=p+
                        (sideAxis*Mathf.Cos(angle)+normal*Mathf.Sin(angle))*radius;
                }
            }
        }
    }
    void BuildPollen(float seconds,Quaternion rotation,float width,ref Rect bounds)
    {
        for(int i=0;i<PollenCount;i++)
        {
            int plant=(i*7)%Anchors.Length;
            float phase=Mathf.Repeat(Time.unscaledTime*.12f+NarcissusColonyLayout.Noise(_trackId,1200+i),1);
            float alpha=Mathf.Sin(phase*Mathf.PI);
            alpha*=alpha*(.7f+.3f*Mathf.Sin(Time.unscaledTime*1.8f+i)*Mathf.Sin(Time.unscaledTime*1.8f+i))* .95f*NarcissusModel.Ease(seconds,_layout.Delays[plant]+5,_layout.Delays[plant]+8);
            if(!_owner.showPollen)alpha=0;
            Vector3 drift=new Vector3(Mathf.Sin(phase*5+i)*.18f,phase*.35f-.06f,.06f+phase*.10f);
            Vector3 p=_layout.Centers[plant]+rotation*(drift*width);
            float radius=width*Mathf.Lerp(.010f,.018f,NarcissusColonyLayout.Noise(_trackId,1300+i));
            for(int corner=0;corner<4;corner++)
            {
                _pollenVertices[i*4+corner]=p+new Vector3((corner%2*2-1)*radius,(corner/2*2-1)*radius,0);
                _pollenColors[i*4+corner]=new Color(1f,.94f,.48f,alpha);
            }
            if(alpha>.001f)
            {
                bounds.xMin=Mathf.Min(bounds.xMin,p.x-radius);bounds.xMax=Mathf.Max(bounds.xMax,p.x+radius);
                bounds.yMin=Mathf.Min(bounds.yMin,p.y-radius);bounds.yMax=Mathf.Max(bounds.yMax,p.y+radius);
            }
        }
        _pollenMesh.vertices=_pollenVertices;_pollenMesh.colors=_pollenColors;
        _pollenMesh.bounds=new Bounds(new Vector3(bounds.center.x,bounds.center.y,0),new Vector3(bounds.width,bounds.height,width*12));
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
            int count=_model.VertexCount*Anchors.Length+RootCount*(RootSteps+1)*RootSides;
            _vertices=new Vector3[count]; var uv=new Vector2[count]; var colors=new Color[count];
            var indices=new int[_model.Triangles.Length*Anchors.Length+RootCount*RootSteps*RootSides*6];
            for (int flower=0;flower<Anchors.Length;flower++)
            {
                int first=flower*_model.VertexCount;
                Array.Copy(_model.UV,0,uv,first,_model.VertexCount);
                Array.Copy(_model.Colors,0,colors,first,_model.VertexCount);
                for (int j=0;j<_model.Triangles.Length;j++) indices[flower*_model.Triangles.Length+j]=first+_model.Triangles[j];
            }
            int rootFirst=_model.VertexCount*Anchors.Length, cursor=_model.Triangles.Length*Anchors.Length;
            for(int root=0;root<RootCount;root++)
            for(int row=0;row<=RootSteps;row++)
            for(int side=0;side<RootSides;side++)
            {
                int v=rootFirst+root*(RootSteps+1)*RootSides+row*RootSides+side;
                uv[v]=new Vector2(side/(float)RootSides,row/(float)RootSteps);
                colors[v]=new Color(.105f,.18f,.065f,0);
                if(row==RootSteps)continue;
                int a=v,b=v+RootSides,c=v-side+(side+1)%RootSides,d=c+RootSides;
                indices[cursor++]=a;indices[cursor++]=b;indices[cursor++]=c;
                indices[cursor++]=b;indices[cursor++]=d;indices[cursor++]=c;
            }
            _mesh=new Mesh {name="Narcissus living colony",hideFlags=HideFlags.HideAndDontSave};
            _mesh.MarkDynamic(); _mesh.vertices=_vertices; _mesh.uv=uv; _mesh.colors=colors; _mesh.triangles=indices;
        }
        if (_pollenMesh==null)
        {
            _pollenVertices=new Vector3[PollenCount*4];_pollenColors=new Color[PollenCount*4];
            var puv=new Vector2[PollenCount*4];var normals=new Vector3[PollenCount*4];var triangles=new int[PollenCount*6];
            for(int p=0;p<PollenCount;p++)
            {
                int v=p*4,j=p*6;
                puv[v]=new Vector2(0,0);puv[v+1]=new Vector2(1,0);puv[v+2]=new Vector2(0,1);puv[v+3]=new Vector2(1,1);
                for(int n=0;n<4;n++)normals[v+n]=Vector3.forward;
                triangles[j]=v;triangles[j+1]=v+1;triangles[j+2]=v+2;
                triangles[j+3]=v+1;triangles[j+4]=v+3;triangles[j+5]=v+2;
            }
            _pollenMesh=new Mesh{name="Narcissus pollen",hideFlags=HideFlags.HideAndDontSave};_pollenMesh.MarkDynamic();
            _pollenMesh.vertices=_pollenVertices;_pollenMesh.uv=puv;_pollenMesh.normals=normals;_pollenMesh.colors=_pollenColors;_pollenMesh.triangles=triangles;
        }
        if (_displayMaterial==null)
        {
            var shader=Resources.Load<Shader>("FacelessNarcissusDisplay");
            if(shader==null || !shader.isSupported)throw new InvalidOperationException("Missing narcissus display shader.");
            _displayMaterial=new Material(shader){hideFlags=HideFlags.HideAndDontSave};
        }
        if (_image==null)
        {
            var go=new GameObject("Narcissus growth",typeof(RectTransform),typeof(CanvasRenderer),typeof(RawImage));
            go.hideFlags=HideFlags.DontSave; go.layer=parent.gameObject.layer;
            go.transform.SetParent(parent.transform,false);
            _image=go.GetComponent<RawImage>(); _image.raycastTarget=false; _image.material=_displayMaterial;
        }
    }
    public void Dispose()
    {
        if (_image!=null) { _image.enabled=false; Release(_image.gameObject); }
        if (_target!=null) { _target.Release(); Release(_target); }
        Release(_rigObject);_rigObject=null;
        Release(_mesh); Release(_material); Release(_displayMaterial); Release(_pollenMesh);
        _image=null; _target=null; _mesh=null; _material=null; _vertices=null; _pollenMesh=null; _displayMaterial=null;
    }
    static void Release(UnityEngine.Object value)
    { if (value==null) return; if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); }
}
