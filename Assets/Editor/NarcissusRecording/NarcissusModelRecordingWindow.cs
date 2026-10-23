#if UNITY_EDITOR
using System;
using System.IO;
using System.Security.Cryptography;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object=UnityEngine.Object;

/// <summary>Runs the existing face reconstruction and colony generator against
/// a static model reference. Only projection, source and playback clock change.</summary>
public sealed class NarcissusModelRecordingWindow : EditorWindow
{
    [Serializable] public sealed class Calibration {
        public string modelSha256; public Vector2[] landmarks;
        public float referenceCenterY=.49f,referenceHalfHeight=.54f;
        public float displayCenterY=.76f,displayHalfHeight=.32f,surfaceFrontZ=.205f;
    }
    [SerializeField] string modelPath,calibrationPath,ffmpegPath;
    [SerializeField] NarcissusRecordingOptions options=new NarcissusRecordingOptions();
    string outputDirectory,status="Choose the supplied GLB and its calibration JSON.";
    MidFaceEraseMask sceneSettings,settings; int seed=1,frameIndex; float previewTime; bool exporting,encoding;
    Vector2 scroll;
    GameObject settingsObject,layerObject; RawImage layer;
    Calibration calibration; FacelessFaceRenderer face; NarcissusFaceGrowth growth;
    Mesh portrait,colony,pollen; Texture2D colour,readback;
    Material headMaterial,plantMaterial; RenderTexture raw,skin,mask,output;
    Vector2[] landmarks; Vector3[] colonyVertices,pollenVertices;
    const int ReferenceSize=1024;
    float WorldPerPixel => 2*calibration.referenceHalfHeight/ReferenceSize;

    [MenuItem("Tools/Narcissus/3D Model Reverse Recorder")]
    public static void Open(){GetWindow<NarcissusModelRecordingWindow>("Narcissus model recorder");}
    void OnEnable(){if(options==null)options=new NarcissusRecordingOptions();options.Normalize();EditorApplication.update+=Tick;sceneSettings=Object.FindObjectOfType<MidFaceEraseMask>();}
    void OnDisable(){EditorApplication.update-=Tick;Release();}
    void OnGUI()
    {
        scroll=EditorGUILayout.BeginScrollView(scroll);
        EditorGUILayout.LabelField("Original Unity effect · configurable model recording",EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Uses NarcissusFaceGrowth directly: the same 38 anchors, seeded layout, flower model, roots, cup guards and fireflies. The 3D model replaces camera input. The live scene is not edited.",MessageType.Info);
        EditorGUI.BeginDisabledGroup(exporting||encoding);
        EditorGUI.BeginChangeCheck();
        sceneSettings=(MidFaceEraseMask)EditorGUILayout.ObjectField("Copy live effect settings",sceneSettings,typeof(MidFaceEraseMask),true);
        seed=EditorGUILayout.IntField("Layout seed (track ID)",seed);
        if(EditorGUI.EndChangeCheck()){Release();status="Effect settings changed. Rebuild the preview.";}
        if(GUILayout.Button("1 · Choose portrait GLB")){string p=EditorUtility.OpenFilePanel("Choose Tripo portrait","","glb");if(!string.IsNullOrEmpty(p)&&p!=modelPath){Release();modelPath=p;calibrationPath=null;status="Choose the calibration JSON made for this GLB, then rebuild.";}}
        EditorGUILayout.LabelField(Path.GetFileName(modelPath??""));
        if(GUILayout.Button("2 · Choose calibration JSON")){string p=EditorUtility.OpenFilePanel("Choose model calibration","","json");if(!string.IsNullOrEmpty(p)){Release();calibrationPath=p;status="Calibration selected. Build the preview.";}}
        EditorGUILayout.LabelField(Path.GetFileName(calibrationPath??""));
        EditorGUILayout.LabelField("Film settings / 视频设置",EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        options.totalSeconds=EditorGUILayout.DelayedIntField("Total seconds / 总时长",options.totalSeconds);
        options.revealSeconds=EditorGUILayout.DelayedIntField("Face reveal seconds / 露脸",options.revealSeconds);
        options.resolution=(NarcissusRecordingOptions.Resolution)EditorGUILayout.EnumPopup("Resolution / 分辨率",options.resolution);
        options.fps=EditorGUILayout.IntPopup("Frame rate / 帧率",options.fps,new[]{"24 fps","25 fps","30 fps","60 fps"},new[]{24,25,30,60});
        options.format=(NarcissusRecordingOptions.Format)EditorGUILayout.EnumPopup("Format / 格式",options.format);
        if(options.format==NarcissusRecordingOptions.Format.MP4_H264)
            options.quality=(NarcissusRecordingOptions.Quality)EditorGUILayout.EnumPopup("MP4 quality / 画质",options.quality);
        options.Normalize();
        if(EditorGUI.EndChangeCheck()){previewTime=Mathf.Min(previewTime,options.totalSeconds);if(output!=null)Guard(()=>RenderFrame(previewTime));}
        EditorGUILayout.HelpBox("0–"+options.ReverseSeconds+" s: one rotation + reverse growth. Last "+options.revealSeconds+" s: face reveal.\n"+options.Width+" × "+options.Height+" · "+options.fps+" fps · "+options.FrameCount+" frames. Total includes the face reveal.",MessageType.Info);
        if(options.format!=NarcissusRecordingOptions.Format.PNG_Sequence){
            if(GUILayout.Button("Choose ffmpeg (optional)")){string p=EditorUtility.OpenFilePanel("Choose ffmpeg executable","","");if(!string.IsNullOrEmpty(p))ffmpegPath=p;}
            if(FindEncoder()==null)EditorGUILayout.HelpBox("ffmpeg not found. Export will save PNG frames only; MP4/MOV needs ffmpeg. You can select its executable above.",MessageType.Info);
        }
        if(GUILayout.Button("3 · Build / rebuild preview"))Guard(Build);
        EditorGUI.BeginDisabledGroup(output==null);
        EditorGUI.BeginChangeCheck();float time=EditorGUILayout.Slider("Seconds",previewTime,0,options.totalSeconds);
        if(EditorGUI.EndChangeCheck()){previewTime=time;Guard(()=>RenderFrame(previewTime));}
        if(GUILayout.Button("4 · Export "+options.totalSeconds+"-second film")){
            string folder=EditorUtility.OpenFolderPanel("Choose export parent folder","","");
            if(!string.IsNullOrEmpty(folder)){
                Guard(()=>{
                    outputDirectory=Path.Combine(folder,"Narcissus-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                    Directory.CreateDirectory(outputDirectory);frameIndex=0;exporting=true;status="Exporting frames…";
                    File.WriteAllText(Path.Combine(outputDirectory,"render-info.txt"),options.Description(seed));
                });
            }
        }
        EditorGUI.EndDisabledGroup();EditorGUI.EndDisabledGroup();
        if(exporting&&GUILayout.Button("Cancel export")){exporting=false;status="Cancelled; completed frames retained.";}
        EditorGUILayout.HelpBox(status,MessageType.None);
        if(output!=null){Rect r=GUILayoutUtility.GetAspectRect(16f/9f);EditorGUI.DrawPreviewTexture(r,output,null,ScaleMode.ScaleToFit);}
        EditorGUILayout.EndScrollView();
    }
    void Guard(Action action){try{action();}catch(Exception e){exporting=false;status=e.Message;UnityEngine.Debug.LogException(e);}}
    void Build()
    {
        if(EditorApplication.isPlaying)throw new InvalidOperationException("Stop Play mode before opening the offline recorder.");
        if(!File.Exists(modelPath)||!File.Exists(calibrationPath))throw new InvalidOperationException("Select both the GLB and calibration JSON.");
        Release();calibration=JsonUtility.FromJson<Calibration>(File.ReadAllText(calibrationPath));
        if(calibration.landmarks==null||calibration.landmarks.Length!=468||calibration.referenceHalfHeight<=0)throw new InvalidDataException("Calibration requires 468 landmarks.");
        using(var sha=SHA256.Create())using(var f=File.OpenRead(modelPath)){
            string actual=BitConverter.ToString(sha.ComputeHash(f)).Replace("-","").ToLowerInvariant();
            if(!string.Equals(actual,calibration.modelSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("This calibration belongs to a different GLB. Each portrait requires its own calibration JSON; choose the matching pair.");
        }
        try {
            NarcissusGlbReader.Load(modelPath,out portrait,out colour);
            settingsObject=new GameObject("Offline effect settings"){hideFlags=HideFlags.HideAndDontSave};settingsObject.SetActive(false);
            settings=settingsObject.AddComponent<MidFaceEraseMask>();
            if(sceneSettings!=null)EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(sceneSettings),settings);
            settings.enabled=false;settings.growNarcissus=true;settings.playEntryAnimation=true;settings.showControls=false;settings.showMask=false;
            settings.reconstructionShader=Shader.Find("Hidden/Faceless/SkinReconstruction");
            settings.compositeShader=Shader.Find("Faceless/SkinComposite");settings.surfaceShader=Shader.Find("Hidden/Faceless/SurfaceGuard");
            layerObject=new GameObject("Offline skin source",typeof(RectTransform),typeof(CanvasRenderer),typeof(RawImage)){hideFlags=HideFlags.HideAndDontSave};
            layer=layerObject.GetComponent<RawImage>();layer.enabled=true;
            headMaterial=MaterialFor("Hidden/Narcissus/RecordedHead");plantMaterial=MaterialFor("Hidden/Narcissus/RecordedColony");
            raw=Target(ReferenceSize,ReferenceSize);skin=Target(ReferenceSize,ReferenceSize);mask=Target(ReferenceSize,ReferenceSize);
            headMaterial.SetTexture("_MainTex",colour);headMaterial.SetVector("_Reference",new Vector4(0,calibration.referenceCenterY,2*calibration.referenceHalfHeight,0));
            headMaterial.SetFloat("_SurfaceFrontZ",calibration.surfaceFrontZ);headMaterial.SetFloat("_Reveal",1);headMaterial.SetMatrix("_RecordingRotation",Matrix4x4.identity);
            headMaterial.SetMatrix("_RecordingViewProjection",Projection(calibration.referenceCenterY,calibration.referenceHalfHeight,1));
            Draw(raw,()=>{headMaterial.SetPass(0);Graphics.DrawMeshNow(portrait,Matrix4x4.identity);});
            landmarks=new Vector2[468];for(int i=0;i<468;i++)landmarks[i]=new Vector2((calibration.landmarks[i].x/(2*calibration.referenceHalfHeight)+.5f)*ReferenceSize,((calibration.landmarks[i].y-calibration.referenceCenterY)/(2*calibration.referenceHalfHeight)+.5f)*ReferenceSize);
            face=new FacelessFaceRenderer(settings);face.Reset(seed);
            for(int i=0;i<8;i++)if(!face.Update(raw,landmarks,Matrix4x4.identity,true,i/30f,1/30f,true))throw new InvalidOperationException("Original skin renderer rejected the model reference. Check shader errors.");
            face.SeekPresentationTime(settings.entryDurationSeconds);face.ApplyComposite(face.Composite);face.Composite.SetFloat("_OverlayOnly",0);
            Graphics.Blit(raw,skin,face.Composite);face.Composite.SetFloat("_RecordingMaskOnly",1);Graphics.Blit(raw,mask,face.Composite);face.Composite.SetFloat("_RecordingMaskOnly",0);
            headMaterial.SetTexture("_RecordedSkin",skin);headMaterial.SetTexture("_RecordedMask",mask);
            growth=new NarcissusFaceGrowth(settings);growth.UpdateRecording(face,layer,ReferenceSize,ReferenceSize,21,21);
            if(growth.RecordingMesh==null)throw new InvalidOperationException("The original flower generator did not produce a mesh. Check Console.");
            colony=Object.Instantiate(growth.RecordingMesh);pollen=Object.Instantiate(growth.RecordingPollenMesh);colony.hideFlags=pollen.hideFlags=HideFlags.HideAndDontSave;
            RenderFrame(previewTime);status="Preview ready. Review 0 s, "+options.ReverseSeconds+" s and "+options.totalSeconds+" s before export.";
        }catch{Release();throw;}
    }
    void RenderFrame(float seconds)
    {
        EnsureOutput();
        float t=Mathf.Clamp(seconds,0,options.totalSeconds),g=options.GrowthTime(t),reveal=NarcissusModel.Ease(t,options.ReverseSeconds,options.totalSeconds);
        Vector3 pivot=new Vector3(0,calibration.displayCenterY,0);
        Matrix4x4 rotation=Matrix4x4.Translate(pivot)*Matrix4x4.Rotate(Quaternion.Euler(0,options.RotationDegrees(t),0))*Matrix4x4.Translate(-pivot);
        Matrix4x4 vp=Projection(calibration.displayCenterY,calibration.displayHalfHeight,options.Width/(float)options.Height);
        headMaterial.SetFloat("_Reveal",reveal);headMaterial.SetMatrix("_RecordingRotation",rotation);headMaterial.SetMatrix("_RecordingViewProjection",vp);
        if(t<options.ReverseSeconds){
            growth.UpdateRecording(face,layer,ReferenceSize,ReferenceSize,g,g);
            CopyGeometry();plantMaterial.CopyPropertiesFromMaterial(growth.RecordingMaterial);
            var centers=growth.RecordingMaterial.GetVectorArray("_CupCenters");var axes=growth.RecordingMaterial.GetVectorArray("_CupAxes");
            for(int i=0;i<38;i++){
                Vector3 c=ToModel(new Vector3(centers[i].x,centers[i].y,centers[i].z),SurfaceAtPixel(growth.RecordingRoots[i]));
                centers[i]=new Vector4(c.x,c.y,c.z,centers[i].w*WorldPerPixel);axes[i].w*=WorldPerPixel;
            }
            plantMaterial.SetVectorArray("_CupCenters",centers);plantMaterial.SetVectorArray("_CupAxes",axes);
            plantMaterial.SetMatrix("_RecordingRotation",rotation);plantMaterial.SetMatrix("_RecordingViewProjection",vp);
        }
        Draw(output,()=>{
            headMaterial.SetPass(0);Graphics.DrawMeshNow(portrait,Matrix4x4.identity);
            if(t<options.ReverseSeconds){plantMaterial.SetPass(0);Graphics.DrawMeshNow(colony,Matrix4x4.identity);if(settings.showPollen){plantMaterial.SetPass(1);Graphics.DrawMeshNow(pollen,Matrix4x4.identity);}}
        });
        Repaint();
    }
    void EnsureOutput()
    {
        if(output!=null&&output.width==options.Width&&output.height==options.Height&&readback!=null)return;
        if(output!=null){output.Release();Object.DestroyImmediate(output);output=null;}
        if(readback!=null){Object.DestroyImmediate(readback);readback=null;}
        output=Target(options.Width,options.Height,true);
        readback=new Texture2D(options.Width,options.Height,TextureFormat.RGB24,false){hideFlags=HideFlags.HideAndDontSave};
    }
    float SurfaceAtPixel(Vector3 p){Vector3 q=ToModel(p,0);return calibration.surfaceFrontZ-1.8f*q.x*q.x-.3f*(q.y-.74f)*(q.y-.74f);}
    Vector3 ToModel(Vector3 p,float depth){return new Vector3((p.x-ReferenceSize*.5f)*WorldPerPixel,(p.y-ReferenceSize*.5f)*WorldPerPixel+calibration.referenceCenterY,p.z*WorldPerPixel+depth);}
    void CopyGeometry()
    {
        colonyVertices=growth.RecordingMesh.vertices;int n=growth.RecordingFlowerVertexCount;
        for(int i=0;i<colonyVertices.Length;i++){
            float depth=i<n*38?SurfaceAtPixel(growth.RecordingRoots[i/n]):SurfaceAtPixel(colonyVertices[i]);
            colonyVertices[i]=ToModel(colonyVertices[i],depth);
        }
        colony.vertices=colonyVertices;colony.RecalculateNormals();colony.RecalculateBounds();
        pollenVertices=growth.RecordingPollenMesh.vertices;
        for(int i=0;i<pollenVertices.Length;i++)pollenVertices[i]=ToModel(pollenVertices[i],SurfaceAtPixel(growth.RecordingRoots[((i/4)*7)%38]));
        pollen.vertices=pollenVertices;pollen.colors=growth.RecordingPollenMesh.colors;pollen.RecalculateBounds();
    }
    void Tick()
    {
        if(!exporting)return;
        Guard(()=>{
            // Include both exact endpoints: the final encoded frame is the full face.
            float seconds=options.FrameTime(frameIndex);RenderFrame(seconds);
            var previous=RenderTexture.active;try{RenderTexture.active=output;readback.ReadPixels(new Rect(0,0,options.Width,options.Height),0,0);readback.Apply(false);}finally{RenderTexture.active=previous;}
            File.WriteAllBytes(Path.Combine(outputDirectory,"frame_"+frameIndex.ToString("D5")+".png"),readback.EncodeToPNG());
            frameIndex++;status="Exporting "+frameIndex+" / "+options.FrameCount;Repaint();
            if(frameIndex==options.FrameCount){exporting=false;Finish();}
        });
    }
    void Finish()
    {
        string executable=FindEncoder();
        if(options.format==NarcissusRecordingOptions.Format.PNG_Sequence||executable==null){
            status=options.FrameCount+" PNG frames saved. Import frame_00000.png as an image sequence at "+options.fps+" fps.";
            if(options.format!=NarcissusRecordingOptions.Format.PNG_Sequence)status+=" Video conversion requires ffmpeg.";
            EditorUtility.RevealInFinder(outputDirectory);return;
        }
        string folder=outputDirectory,video=Path.Combine(folder,"Narcissus-Unity-Reverse-"+options.totalSeconds+"s"+options.Extension);
        var start=new ProcessStartInfo(executable,options.EncoderArguments(folder,video)){UseShellExecute=false,CreateNoWindow=true};
        Process process=Process.Start(start);if(process==null)throw new InvalidOperationException("Could not start ffmpeg; PNG frames are retained.");
        encoding=true;status="Frames saved; ffmpeg is encoding "+options.format+"…";
        EditorApplication.CallbackFunction poll=null;poll=()=>{if(!process.HasExited)return;int exit=process.ExitCode;process.Dispose();EditorApplication.update-=poll;if(this==null)return;encoding=false;status=exit==0?"Video and editable PNG sequence saved.":"Video encoding failed; the complete PNG sequence is retained.";EditorUtility.RevealInFinder(folder);Repaint();};EditorApplication.update+=poll;
    }
    string FindEncoder(){foreach(string p in new[]{ffmpegPath,"/opt/homebrew/bin/ffmpeg","/usr/local/bin/ffmpeg","/usr/bin/ffmpeg"})if(!string.IsNullOrEmpty(p)&&File.Exists(p))return p;return null;}
    static Material MaterialFor(string name){Shader shader=Shader.Find(name);if(shader==null||!shader.isSupported)throw new InvalidOperationException("Missing/unsupported shader: "+name);return new Material(shader){hideFlags=HideFlags.HideAndDontSave};}
    static RenderTexture Target(int w,int h,bool display=false){var r=new RenderTexture(w,h,24,RenderTextureFormat.ARGB32,display?RenderTextureReadWrite.sRGB:RenderTextureReadWrite.Linear){hideFlags=HideFlags.HideAndDontSave,filterMode=FilterMode.Bilinear,wrapMode=TextureWrapMode.Clamp};if(!r.Create())throw new InvalidOperationException("Render target allocation failed.");return r;}
    static Matrix4x4 Projection(float centerY,float halfHeight,float aspect)=>GL.GetGPUProjectionMatrix(Matrix4x4.Ortho(-halfHeight*aspect,halfHeight*aspect,-halfHeight,halfHeight,.1f,10),true)*Matrix4x4.Translate(new Vector3(0,-centerY,-2));
    static void Draw(RenderTexture target,Action draw){var old=RenderTexture.active;bool srgb=GL.sRGBWrite;GL.PushMatrix();try{Graphics.SetRenderTarget(target);GL.sRGBWrite=target.sRGB;GL.Clear(true,true,new Color(.025f,.03f,.035f,1));draw();}finally{GL.PopMatrix();RenderTexture.active=old;GL.sRGBWrite=srgb;}}
    void Release()
    {
        exporting=false;growth?.Dispose();growth=null;face?.Dispose();face=null;
        foreach(var r in new[]{raw,skin,mask,output})if(r!=null){r.Release();Object.DestroyImmediate(r);}raw=skin=mask=output=null;
        foreach(var o in new Object[]{portrait,colony,pollen,colour,readback,headMaterial,plantMaterial,settingsObject,layerObject})if(o!=null)Object.DestroyImmediate(o);
        portrait=colony=pollen=null;colour=readback=null;headMaterial=plantMaterial=null;settingsObject=layerObject=null;
    }
}
#endif
