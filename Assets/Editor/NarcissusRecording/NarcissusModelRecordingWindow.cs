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
    string modelPath,calibrationPath,outputDirectory,status="Choose the supplied GLB and its calibration JSON.";
    MidFaceEraseMask sceneSettings,settings; int seed=1,frameIndex; float previewTime; bool exporting;
    GameObject settingsObject,layerObject; RawImage layer;
    Calibration calibration; FacelessFaceRenderer face; NarcissusFaceGrowth growth;
    Mesh portrait,colony,pollen; Texture2D colour,readback;
    Material headMaterial,plantMaterial; RenderTexture raw,skin,mask,output;
    Vector2[] landmarks; Vector3[] colonyVertices,pollenVertices;
    const int ReferenceSize=1024,Width=1920,Height=1080,Fps=30,FrameCount=13*Fps;
    float WorldPerPixel => 2*calibration.referenceHalfHeight/ReferenceSize;

    [MenuItem("Tools/Narcissus/3D Model Reverse Recorder")]
    public static void Open(){GetWindow<NarcissusModelRecordingWindow>("Narcissus model recorder");}
    void OnEnable(){EditorApplication.update+=Tick;sceneSettings=Object.FindObjectOfType<MidFaceEraseMask>();}
    void OnDisable(){EditorApplication.update-=Tick;Release();}
    void OnGUI()
    {
        EditorGUILayout.LabelField("Original Unity effect · 10 s reverse + 3 s face reveal",EditorStyles.boldLabel);
        EditorGUILayout.HelpBox("Uses NarcissusFaceGrowth directly: the same 38 anchors, seeded layout, flower model, roots, cup guards and fireflies. The 3D model replaces camera input. The live scene is not edited.",MessageType.Info);
        EditorGUI.BeginDisabledGroup(exporting);
        sceneSettings=(MidFaceEraseMask)EditorGUILayout.ObjectField("Copy live effect settings",sceneSettings,typeof(MidFaceEraseMask),true);
        seed=EditorGUILayout.IntField("Layout seed (track ID)",seed);
        if(GUILayout.Button("1 · Choose portrait GLB")){string p=EditorUtility.OpenFilePanel("Choose Tripo portrait","","glb");if(!string.IsNullOrEmpty(p))modelPath=p;}
        EditorGUILayout.LabelField(Path.GetFileName(modelPath??""));
        if(GUILayout.Button("2 · Choose calibration JSON")){string p=EditorUtility.OpenFilePanel("Choose model calibration","","json");if(!string.IsNullOrEmpty(p))calibrationPath=p;}
        EditorGUILayout.LabelField(Path.GetFileName(calibrationPath??""));
        if(GUILayout.Button("3 · Build / rebuild preview"))Guard(Build);
        EditorGUI.BeginDisabledGroup(output==null);
        EditorGUI.BeginChangeCheck();float time=EditorGUILayout.Slider("Seconds",previewTime,0,13);
        if(EditorGUI.EndChangeCheck()){previewTime=time;Guard(()=>RenderFrame(previewTime));}
        if(GUILayout.Button("4 · Export 13-second film")){
            string folder=EditorUtility.OpenFolderPanel("Choose export parent folder","","");
            if(!string.IsNullOrEmpty(folder)){
                outputDirectory=Path.Combine(folder,"Narcissus-"+DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.CreateDirectory(outputDirectory);frameIndex=0;exporting=true;status="Exporting frames…";
            }
        }
        EditorGUI.EndDisabledGroup();EditorGUI.EndDisabledGroup();
        if(exporting&&GUILayout.Button("Cancel export")){exporting=false;status="Cancelled; completed frames retained.";}
        EditorGUILayout.HelpBox(status,MessageType.None);
        if(output!=null){Rect r=GUILayoutUtility.GetAspectRect(16f/9f);EditorGUI.DrawPreviewTexture(r,output,null,ScaleMode.ScaleToFit);}
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
            if(!string.Equals(actual,calibration.modelSha256,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("This calibration belongs to a different GLB. Use the original supplied model.");
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
            raw=Target(ReferenceSize,ReferenceSize);skin=Target(ReferenceSize,ReferenceSize);mask=Target(ReferenceSize,ReferenceSize);output=Target(Width,Height,true);
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
            readback=new Texture2D(Width,Height,TextureFormat.RGB24,false){hideFlags=HideFlags.HideAndDontSave};
            RenderFrame(previewTime);status="Preview ready. Review 0 s, 10 s and 13 s before export.";
        }catch{Release();throw;}
    }
    void RenderFrame(float seconds)
    {
        float t=Mathf.Clamp(seconds,0,13),g=21*Mathf.Clamp01(1-t/10),reveal=NarcissusModel.Ease(t,10,13);
        // Constant orbital speed with a one-second ease-out; exactly one revolution.
        float q=Mathf.Min(t,10),travel=q<=9?q:9+(q-9)-.5f*(q-9)*(q-9);
        Vector3 pivot=new Vector3(0,calibration.displayCenterY,0);
        Matrix4x4 rotation=Matrix4x4.Translate(pivot)*Matrix4x4.Rotate(Quaternion.Euler(0,360*travel/9.5f,0))*Matrix4x4.Translate(-pivot);
        Matrix4x4 vp=Projection(calibration.displayCenterY,calibration.displayHalfHeight,Width/(float)Height);
        headMaterial.SetFloat("_Reveal",reveal);headMaterial.SetMatrix("_RecordingRotation",rotation);headMaterial.SetMatrix("_RecordingViewProjection",vp);
        if(t<10){
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
            if(t<10){plantMaterial.SetPass(0);Graphics.DrawMeshNow(colony,Matrix4x4.identity);if(settings.showPollen){plantMaterial.SetPass(1);Graphics.DrawMeshNow(pollen,Matrix4x4.identity);}}
        });
        Repaint();
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
            float seconds=13f*frameIndex/(FrameCount-1);RenderFrame(seconds);
            var previous=RenderTexture.active;try{RenderTexture.active=output;readback.ReadPixels(new Rect(0,0,Width,Height),0,0);readback.Apply(false);}finally{RenderTexture.active=previous;}
            File.WriteAllBytes(Path.Combine(outputDirectory,"frame_"+frameIndex.ToString("D5")+".png"),readback.EncodeToPNG());
            frameIndex++;status="Exporting "+frameIndex+" / "+FrameCount;Repaint();
            if(frameIndex==FrameCount){exporting=false;Finish();}
        });
    }
    void Finish()
    {
        string executable=null;
        foreach(string p in new[]{"/opt/homebrew/bin/ffmpeg","/usr/local/bin/ffmpeg","/usr/bin/ffmpeg"})if(File.Exists(p)){executable=p;break;}
        File.WriteAllText(Path.Combine(outputDirectory,"render-info.txt"),"Narcissus: 1920x1080, 30 fps, 390 frames, 13 seconds.\nOriginal Unity colony generator, 38 landmark anchors.\nLayout seed: "+seed+"\n0-10s: reverse growth 21 to 0; 10-13s: face reveal.\n");
        if(executable==null){status="390 PNG frames saved. Import frame_00000.png as an image sequence at 30 fps. MP4 conversion requires ffmpeg.";EditorUtility.RevealInFinder(outputDirectory);return;}
        string video=Path.Combine(outputDirectory,"Narcissus-Unity-Reverse-13s.mp4");
        var start=new ProcessStartInfo(executable,"-y -framerate 30 -i "+Quote(Path.Combine(outputDirectory,"frame_%05d.png"))+" -c:v libx264 -crf 18 -pix_fmt yuv420p -movflags +faststart "+Quote(video)){UseShellExecute=false,CreateNoWindow=true};
        Process process=Process.Start(start);status="Frames saved; ffmpeg is encoding MP4…";
        EditorApplication.CallbackFunction poll=null;poll=()=>{if(!process.HasExited)return;int exit=process.ExitCode;process.Dispose();EditorApplication.update-=poll;if(this==null)return;status=exit==0?"MP4 and editable PNG sequence saved.":"MP4 encoding failed; the complete PNG sequence is retained.";EditorUtility.RevealInFinder(outputDirectory);Repaint();};EditorApplication.update+=poll;
    }
    static string Quote(string s)=>"\""+s.Replace("\\","\\\\").Replace("\"","\\\"")+"\"";
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
