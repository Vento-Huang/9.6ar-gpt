"""Headless shader and synthetic projection tests; NOT Unity/Metal or camera QA.

pip install numpy pillow scipy moderngl mediapipe scikit-image
Usage: python verify_skin.py --work /path/to/qa --egl /path/to/libEGL.so.1
work must contain astronaut.png, astronaut-landmarks.json (MediaPipe 478 pts),
and the official canonical_face_model.obj used by FacelessSurface.cs.
The shader function bodies are loaded from the project, translated to GLSL and
executed by Mesa. The surface-guard vertex/fragment bodies are also translated
and executed, using the OpenGL coordinate convention. Unity UI plumbing,
Metal's vertex convention, detection accuracy and temporal tracking are not
simulated. Head turns below are synthetic canonical model projections.
"""
import argparse, json, re, textwrap
from pathlib import Path
import numpy as np
from PIL import Image
import moderngl
from scipy.ndimage import distance_transform_edt, map_coordinates

ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "Assets/MediaPipeUnity/Samples/Scenes/Face Landmark Detection"
VERT = """#version 330
in vec2 position; out vec2 uv;
void main(){ uv=position*0.5+0.5; gl_Position=vec4(position,0,1); }
"""

def translate(s):
    s = re.sub(r'#include[^\n]*', '', s)
    s = re.sub(r'\[(unroll|loop)\]', '', s)
    s = re.sub(r':\s*SV_Target', '', s)
    for a,b in [('float2','vec2'),('float3','vec3'),('float4','vec4'),('fixed4','vec4'),
                ('tex2D','texture'),('lerp','mix'),('frac','fract')]:
        s = re.sub(r'\b'+a+r'\b',b,s)
    s = s.replace('(int)(i.uv.x * 6)', 'int(i.uv.x * 6)')
    s = re.sub(r'(vec[234]\s+\w+\s*=)\s*0;',r'\1 vec3(0);',s)
    s = s.replace('return 0;', 'return vec4(0);')
    s = re.sub(r'any\((uv|cameraUV) < 0\.0\)', r'any(lessThan(\1,vec2(0)))', s)
    s = re.sub(r'any\((uv|cameraUV) > 1\.0\)', r'any(greaterThan(\1,vec2(1)))', s)
    s = s.replace('all(atlas >= 0)', 'all(greaterThanEqual(atlas,vec2(0)))')
    s = s.replace('all(atlas <= 1)', 'all(lessThanEqual(atlas,vec2(1)))')
    return s

def uniforms(s):
    return re.sub(r'(?m)^(\s*)(float[234]?|sampler2D)(\s+_[^;]+;)',r'\1uniform \2\3',s)

def shader_body(composite=False):
    common=(SRC/'FacelessSkinCommon.cginc').read_text()
    s=(SRC/('FacelessSkinComposite.shader' if composite else 'FacelessSkinReconstruction.shader')).read_text()
    if composite:
        globals=s[s.index('sampler2D _MainTex'):s.index('struct appdata')]
        body=s[s.index('float4 frag(v2f'):s.index('ENDCG')]
        structs='struct v2f {vec2 uv; vec4 color; vec4 local;};'
    else:
        s=s[s.index('sampler2D _MainTex'):s.index('ENDCG')]
        globals=s[:s.index('float4 Gaussian')]
        body=s[s.index('float4 Gaussian'):]
        structs='struct v2f_img {vec2 uv;};'
    return translate(uniforms(common)+uniforms(globals)+structs+body)

def fit_regions(points):
    source=(SRC/'FacelessRegions.cs').read_text()
    groups=re.findall(r'new\[\] \{([\d,\s]+)\}',source)
    ids=[list(map(int,re.findall(r'\d+',g))) for g in groups]
    boundary=list(map(int,re.findall(r'\d+',source.split('BoundaryIndices =')[1].split('};')[0])))
    p=points
    height=np.linalg.norm(p[10]-p[152])
    up=(p[10]-p[152])/height; right=np.array([up[1],-up[0]])
    if right@(p[454]-p[234])<0:right=-right
    frame=np.stack((p[:468]@right,p[:468]@up),axis=-1)
    frame_min=frame.min(0);frame_max=frame.max(0)
    width=max(frame_max[0]-frame_min[0],height*.18)
    center=(frame_min+frame_max)*.5
    origin=right*center[0]+up*center[1]
    u=right*width*1.25;v=up*max(height,frame_max[1]-frame_min[1])*1.16
    def perp(v): return np.array([v[1],-v[0]])
    axes=[p[133]-p[33],p[263]-p[362],right,p[327]-p[98],p[291]-p[61],p[291]-p[61],right,
          perp(p[57]-p[203]),perp(p[287]-p[423]),perp(p[211]-p[61]),perp(p[431]-p[291]),
          p[100]-p[50],p[329]-p[280],p[208]-p[140],p[428]-p[369]]
    assert len(ids)==len(axes)==int(re.search(r'const int Count = (\d+)',source)[1])
    centers=[]; aa=[]
    for i,(group,x) in enumerate(zip(ids,axes)):
        x=x/np.linalg.norm(x) if x@x>.01 else u/np.linalg.norm(u)
        y=np.array([-x[1],x[0]])
        q=np.stack((p[group]@x,p[group]@y),axis=-1)
        low=q.min(0);high=q.max(0);c=(low+high)/2;r=(high-low)/2+width*(.020 if i>=13 else .022)
        r[0]=max(r[0],width*(.060 if i==2 else .095 if i==6 else .018))
        r[1]=max(r[1],height*(.052 if i==4 else .015))
        enclosure=max(1,float(((np.abs(q-c)/r)**4).sum(-1).max()**.25)*1.08)
        r*=enclosure
        if i==5:r[0]*=1.10
        center=x*c[0]+y*c[1]
        centers.append([*center,*r])
        edge=min(width*.09,max(width*.013,min(r)*.45 if i>=13 else r[0]*.75))
        aa.append([*x,edge,0])
    donor_ids=list(map(int,re.findall(r'\d+',source.split('DonorIndices =')[1].split('};')[0])))
    donors=np.array([[*p[i],width*.016,0] for i in donor_ids])
    b=np.c_[p[boundary],np.zeros((36,2))]
    values={'_Regions':np.array(centers),'_RegionAxes':np.array(aa),'_Boundary':b,
            '_Donors':donors,'_FrameOrigin':[*origin,width,height],'_FrameU':[*u,0,0],
            '_FrameV':[*v,min((p[10]-origin)@up-height*.05,
                             max((p[105]-origin)@up,(p[334]-origin)@up)+height*.12),0],
            '_ContourInset':width*.018,
            '_FaceBounds':[*p[:468].min(0),*p[:468].max(0)]}
    return values,ids

class SurfaceGuard:
    """Execute the actual surface shader body with the embedded topology."""
    def __init__(self, ctx, work):
        self.ctx=ctx
        source=(SRC/'FacelessSurface.cs').read_text()
        self.triangles=np.array(list(map(int,re.findall(r'\d+',
            source.split('static readonly int[] SurfaceTriangles =')[1].split('};')[0]))),
            dtype='i4').reshape(-1,3)
        loop_body=source.split('static readonly int[][] FeatureLoops =')[1].split('};')[0]
        self.loops=[list(map(int,re.findall(r'\d+',g)))
                    for g in re.findall(r'new\[\] \{([\d,\s]+)\}',loop_body)]
        assert self.triangles.shape==(898,3)
        assert self.triangles.min()==0 and self.triangles.max()==467
        indices=self.triangles.tolist()
        for i,loop in enumerate(self.loops):
            indices.extend([[468+i,a,loop[(j+1)%len(loop)]] for j,a in enumerate(loop)])
        shader=(SRC/'FacelessSurface.shader').read_text()
        shader=shader[shader.index('struct VertexInput'):shader.index('ENDCG')]
        shader=translate(re.sub(r':\s*(?:SV_POSITION|POSITION)\b','',shader))
        common='#version 330\n#define UNITY_UV_STARTS_AT_TOP 0\n'+shader
        vertex=common+'\nin vec2 position; void main(){VertexInput a; a.vertex=vec4(position,0,1); gl_Position=vert(a).position;}'
        fragment=common+'\nout vec4 result; void main(){VertexOutput a; a.position=vec4(0); result=frag(a);}'
        (work/'surface.vert.glsl').write_text(vertex)
        (work/'surface.frag.glsl').write_text(fragment)
        self.program=ctx.program(vertex_shader=vertex,fragment_shader=fragment)
        self.buffer=ctx.buffer(reserve=(468+len(self.loops))*2*4)
        self.indices=ctx.buffer(np.array(indices,dtype='i4').tobytes())
        self.vao=ctx.simple_vertex_array(self.program,self.buffer,'position',index_buffer=self.indices)

    def render(self,points,target):
        vertices=np.vstack((points[:468],[points[loop].mean(0) for loop in self.loops]))
        vertices=vertices/np.array([target.width,target.height])
        self.buffer.write(vertices.astype('f4').tobytes())
        fb=self.ctx.framebuffer(color_attachments=[target]);fb.use()
        self.ctx.viewport=(0,0,target.width,target.height)
        self.ctx.disable(moderngl.CULL_FACE|moderngl.DEPTH_TEST|moderngl.BLEND)
        fb.clear(0,0,0,0)
        self.vao.render(moderngl.TRIANGLES);fb.release()

def pixel_sample(field,points):
    """Native pixel coordinates (not array index centers), bilinear sampling."""
    return map_coordinates(field,[points[:,1]-.5,points[:,0]-.5],order=1,mode='nearest')

def old_boundary_guard(points,values):
    polygon=values['_Boundary'][:,:2]
    distance=np.full(len(points),np.inf);inside=np.zeros(len(points),dtype=bool)
    for a,b in zip(polygon,np.roll(polygon,-1,axis=0)):
        edge=b-a;delta=points-a
        nearest=delta-np.clip(delta@edge/max(edge@edge,.00001),0,1)[:,None]*edge
        distance=np.minimum(distance,np.linalg.norm(nearest,axis=1))
        if abs(b[1]-a[1])>1e-12:
            crosses=((a[1]>points[:,1])!=(b[1]>points[:,1]))
            inside^=crosses&(points[:,0] < a[0]+(points[:,1]-a[1])*edge[0]/edge[1])
    inset=values['_ContourInset']
    t=np.clip((distance*(inside*2-1)-inset)/inset,0,1)
    return t*t*(3-2*t)

def synthetic_projection_tests(ctx,work,surface,tex,render,read):
    """Canonical geometry only: deliberately makes no detector quality claim."""
    canonical=np.array([[float(x) for x in row.split()[1:]]
                       for row in (work/'canonical_face_model.obj').read_text().splitlines()
                       if row.startswith('v ')])
    assert canonical.shape==(468,3)
    canonical-=canonical.mean(0)
    normals=np.zeros_like(canonical)
    faces=canonical[surface.triangles]
    face_normals=np.cross(faces[:,1]-faces[:,0],faces[:,2]-faces[:,0])
    for corner in range(3):np.add.at(normals,surface.triangles[:,corner],face_normals)
    if normals[1,2]<0:normals=-normals
    normals/=np.maximum(np.linalg.norm(normals,axis=1)[:,None],1e-9)
    size=384
    mask=tex((size,size));out=tex((size,size))
    white=tex((4,4),np.ones((4,4,4)))
    yy,xx=np.mgrid[:size,:size]
    color=np.stack((xx/size,yy/size,np.full_like(xx,.35,dtype=float),np.ones_like(xx)),axis=-1)
    video=tex((size,size),color)
    black=tex((size,size),np.dstack((np.zeros((size,size,3)),np.ones((size,size)))))
    cases=[]; recovered=[];worst_alpha=1.;max_outside_error=0.;max_black_error=0.
    important=np.array([1,2,0,13,14,17,61,291,159,386,105,334,206,426,202,422])
    for perspective in [False,True]:
      for yaw in [-85,-70,-45,0,45,70,85]:
       for pitch in [-25,0,25]:
        for roll in [-30,0,30]:
         y,p,r=np.deg2rad([yaw,pitch,roll])
         ry=np.array([[np.cos(y),0,np.sin(y)],[0,1,0],[-np.sin(y),0,np.cos(y)]])
         rx=np.array([[1,0,0],[0,np.cos(p),-np.sin(p)],[0,np.sin(p),np.cos(p)]])
         rz=np.array([[np.cos(r),-np.sin(r),0],[np.sin(r),np.cos(r),0],[0,0,1]])
         rotation=rz@rx@ry
         rotated=canonical@rotation.T
         facing=(normals@rotation.T)@(np.array([0.,0.,1.]))>0
         if perspective:
             view=np.array([0.,0.,45.])-rotated
             facing=((normals@rotation.T)*view).sum(-1)>0
             projected=rotated[:,:2]*(600/(45-rotated[:,2]))[:,None]
         else:projected=rotated[:,:2]*15
         for mirrored in [False,True]:
          points=projected*np.array([-1 if mirrored else 1,1])+np.array([size*.48,size*.53])
          values,groups=fit_regions(points)
          values.update(_CameraSize=[size,size,1/size,1/size],_Amount=1.,_Volume=0.,_Grain=0.,
                        _ShowMask=0.,_Stages=np.ones(len(groups)),_VideoVisibility=1.)
          features=np.unique(np.concatenate(groups))
          delta=points-values['_FrameOrigin'][:2]
          u=np.array(values['_FrameU'][:2]);v=np.array(values['_FrameV'][:2])
          atlas=np.stack((delta@u/(u@u),delta@v/(v@v)),axis=-1)+.5
          assert (atlas[features]>0).all() and (atlas[features]<1).all(),('atlas',yaw,pitch,roll,mirrored)
          surface.render(points,mask);coverage=read(mask)[:,:,0]
          # EDT measures background pixel centers, not the silhouette crossing
          # their cells. Subtract a half-pixel diagonal for a conservative bound.
          interior_distance=np.maximum(0,distance_transform_edt(coverage>.5)-np.sqrt(.5))
          distances=pixel_sample(interior_distance,points)
          eligible=features[(distances[features]>=2)&facing[features]]
          assert len(eligible)>0
          render('frag',black,out,{'_SkinTex':white,'_SurfaceTex':mask},values)
          alpha=read(out)[:,:,0]
          sampled=pixel_sample(alpha,points)
          alpha_min=float(sampled[eligible].min())
          # A second bilinear read of the composite near a rasterized edge can
          # include its AA ramp. At >=2 px from the silhouette allow 0.5% loss.
          assert alpha_min>=.995,('feature coverage',alpha_min,yaw,pitch,roll,mirrored,perspective,
                                 int(eligible[np.argmin(sampled[eligible])]))
          worst_alpha=min(worst_alpha,alpha_min)
          old=old_boundary_guard(points,values)
          fixed=important[(old[important]<.1)&(sampled[important]>.995)&(distances[important]>=2)&facing[important]]
          if len(fixed):recovered.append({'yaw':yaw,'pitch':pitch,'roll':roll,'mirrored':mirrored,
                                         'perspective':perspective,'landmarks':fixed.tolist()})
          render('frag',video,out,{'_SkinTex':white,'_SurfaceTex':mask},values)
          rendered=read(out)
          far_outside=distance_transform_edt(coverage<.5)>=2
          outside_error=float(np.abs(rendered[:,:,:3][far_outside]-color[:,:,:3][far_outside]).max())
          assert outside_error<1e-5,('outside face changed',outside_error,yaw)
          max_outside_error=max(max_outside_error,outside_error)
          render('frag',video,out,{'_SkinTex':white,'_SurfaceTex':mask},dict(values,_VideoVisibility=0.))
          black_error=float(np.abs(read(out)[:,:,:3]).max());assert black_error<1e-6
          max_black_error=max(max_black_error,black_error)
          cases.append({'yaw':yaw,'pitch':pitch,'roll':roll,'mirrored':mirrored,'perspective':perspective,
                        'eligible_feature_points':len(eligible),'min_feature_alpha':alpha_min})
    assert recovered and any(x['yaw']<0 for x in recovered) and any(x['yaw']>0 for x in recovered)
    assert any(x['mirrored'] for x in recovered) and any(not x['mirrored'] for x in recovered)
    report={'synthetic_projection_only':True,'detector_profile_accuracy_tested':False,
            'unity_editor_tested':False,'metal_tested':False,'cases':cases,
            'cases_count':len(cases),'min_interior_feature_alpha':worst_alpha,
            'outside_surface_max_error':max_outside_error,'video_hidden_max_error':max_black_error,
            'previous_oval_guard_regressions_recovered':recovered}
    (work/'profile-projection-checks.json').write_text(json.dumps(report,indent=2))
    return {key:value for key,value in report.items() if key not in ['cases','previous_oval_guard_regressions_recovered']}|{
        'previous_oval_guard_recovery_cases':len(recovered)}

def color_gradient_tests(work,surface,tex,render,read,reconstruct,seed_texture,donor_texture):
    """Known continuous RGB fields, damaged only at excluded feature pixels.

    Execute the actual reconstruction/composite fragments twice: illumination
    guide disabled (the original normalized-pyramid baseline) and enabled.
    The independent oracle is the undamaged analytic field, not a reimplementation
    of the fill algorithm. This does not establish photographic realism.
    """
    canonical=np.array([[float(x) for x in row.split()[1:]]
                       for row in (work/'canonical_face_model.obj').read_text().splitlines()
                       if row.startswith('v ')])
    canonical-=canonical.mean(0)
    size=384
    points=canonical[:,:2]*15+np.array([size*.48,size*.53])
    values,groups=fit_regions(points)
    values.update(_CameraSize=[size,size,1/size,1/size],_Amount=1.,_Volume=0.,_Grain=0.,
                  _ShowMask=0.,_Stages=np.ones(len(groups)),_VideoVisibility=1.,_LocalColorStrength=1.,_HighlightSuppression=.85)
    surface_texture=tex((size,size));surface.render(points,surface_texture)
    output=tex((size,size));white=tex((4,4),np.ones((4,4,4)))
    black=tex((size,size),np.dstack((np.zeros((size,size,3)),np.ones((size,size)))))
    render('frag',black,output,{'_SkinTex':white,'_SurfaceTex':surface_texture},values)
    alpha=read(output)[:,:,0]
    yy,xx=np.mgrid[:size,:size];pixels=np.stack((xx+.5,yy+.5),axis=-1)
    origin=np.array(values['_FrameOrigin'][:2]);width,height=values['_FrameOrigin'][2:]
    x=(pixels[:,:,0]-origin[0])/width;y=(pixels[:,:,1]-origin[1])/height
    base=np.array([.58,.405,.305]);horizontal=np.array([.22,.12,.07]);vertical=np.array([.12,.105,.075])
    affine=base+x[:,:,None]*horizontal+y[:,:,None]*vertical
    broad_shadow=np.exp(-(((x+.18)/.42)**2+((y-.05)/.58)**2))*.085
    curved=affine-broad_shadow[:,:,None]*np.array([1.,.85,.7])
    core=alpha>.995;edge=(alpha>.1)&(alpha<.9)
    assert core.sum()>1000 and edge.sum()>1000
    features=np.unique(np.concatenate(groups))
    strokes=np.zeros((size,size),dtype=bool)
    for point in points[features]:
        strokes|=((pixels-point)**2).sum(-1)<(width*.012)**2
    # Damage lies wholly inside the excluded feature core; the clean surroundings
    # carry the exact continuous lighting that should extend into the hole.
    strokes&=alpha>.9999
    feature_ids=[159,386,1,13,14,105,334,206,426,202,422,50,280,140,369]
    report={}
    def save(name,rgb):
        Image.fromarray((np.clip(rgb[::-1],0,1)*255).astype('uint8')).save(work/name)
    for fixture,clean in [('affine',affine),('curved_shadow',curved)]:
        dirty=clean.copy();dirty[strokes]*=.14
        source=tex((size,size),np.dstack((dirty,np.ones((size,size)))))
        outcomes={};images={}
        for name,strength in [('baseline',0.),('local_color',1.)]:
            case=dict(values,_LocalColorStrength=strength)
            skin=reconstruct(source,case)
            render('frag',source,output,{'_SkinTex':skin,'_SurfaceTex':surface_texture},case)
            actual=read(output)[:,:,:3];assert np.isfinite(actual).all()
            error=actual-clean
            core_rmse=float(np.sqrt(np.mean(error[core]**2)))
            edge_rmse=float(np.sqrt(np.mean(error[edge]**2)))
            quadrants=[]
            for left,lower in [(True,False),(False,False),(True,True),(False,True)]:
                area=core&((x<0) if left else (x>=0))&((y<0) if lower else (y>=0))
                assert area.sum()>100
                quadrants.append({'clean_rgb':clean[area].mean(0).tolist(),
                                  'output_rgb':actual[area].mean(0).tolist()})
            clean_means=np.array([q['clean_rgb'] for q in quadrants])
            output_means=np.array([q['output_rgb'] for q in quadrants])
            local_rmse=float(np.sqrt(np.mean((clean_means-output_means)**2)))
            relative_spatial_variation=float(np.linalg.norm(output_means-output_means.mean(0))/
                                             np.linalg.norm(clean_means-clean_means.mean(0)))
            # Compare the error's pixel-to-pixel change around the feather: this
            # isolates a spurious seam from the legitimate clean illumination.
            gradient_error=np.stack((np.gradient(error,axis=0),np.gradient(error,axis=1)),axis=-1)
            seam_gradient_rmse=float(np.sqrt(np.mean(gradient_error[edge]**2)))
            seed=read(seed_texture)
            u=np.array(values['_FrameU'][:2]);v=np.array(values['_FrameV'][:2])
            delta=points[feature_ids]-origin
            atlas=np.stack((delta@u/(u@u),delta@v/(v@v)),axis=-1)+.5
            source_weights=pixel_sample(seed[:,:,3],atlas*seed_texture.width)
            assert np.max(source_weights)<1e-6,(fixture,name,source_weights)
            outcomes[name]={'core_rmse':core_rmse,'feather_rmse':edge_rmse,
                            'feather_gradient_rmse':seam_gradient_rmse,'quadrant_mean_rmse':local_rmse,
                            'relative_quadrant_variation':relative_spatial_variation,'quadrants':quadrants,
                            'feature_source_weight_max':float(np.max(source_weights)),
                            'donor_confidences':read(donor_texture)[0,:,3].tolist()}
            images[name]=actual
            save('color-'+fixture+'-'+name+'.png',actual)
        save('color-'+fixture+'-clean.png',clean);save('color-'+fixture+'-input.png',dirty)
        save('color-'+fixture+'-comparison.png',np.concatenate((dirty,images['baseline'],images['local_color'],clean),axis=1))
        report[fixture]=outcomes
    (work/'color-gradient-checks.json').write_text(json.dumps(report,indent=2))
    print('color checks:',json.dumps(report))
    # These are improvement gates, not claims of exact inpainting. Preserve the
    # measured numbers on disk before asserting so a failed trial is reviewable.
    for fixture,outcomes in report.items():
        baseline=outcomes['baseline'];actual=outcomes['local_color']
        for metric in ['core_rmse','feather_rmse','quadrant_mean_rmse','feather_gradient_rmse']:
            assert actual[metric]<baseline[metric],(fixture,metric,actual[metric],baseline[metric])
        assert actual['relative_quadrant_variation']>.85,(fixture,'color collapsed',actual)
    assert report['affine']['local_color']['core_rmse']<.012,report['affine']
    return report

def highlight_tests(work,surface,tex,render,read,reconstruct,seed_texture,donor_texture):
    """Small highlights on known skin gradients, not a photographic oracle.

    Coverage A/B disables only the new region stages and uses the same filled
    texture. A second A/B holds all regions fixed and changes only highlight
    compression, to distinguish wider coverage from altered color processing.
    """
    canonical=np.array([[float(x) for x in row.split()[1:]]
                       for row in (work/'canonical_face_model.obj').read_text().splitlines()
                       if row.startswith('v ')])
    canonical-=canonical.mean(0)
    size=384;points=canonical[:,:2]*15+np.array([size*.48,size*.53])
    values,groups=fit_regions(points)
    values.update(_CameraSize=[size,size,1/size,1/size],_Amount=1.,_Volume=0.,_Grain=0.,
                  _ShowMask=0.,_Stages=np.ones(len(groups)),_VideoVisibility=1.,
                  _LocalColorStrength=1.,_HighlightSuppression=.85)
    surface_texture=tex((size,size));surface.render(points,surface_texture)
    output=tex((size,size));white=tex((4,4),np.ones((4,4,4)))
    black=tex((size,size),np.dstack((np.zeros((size,size,3)),np.ones((size,size)))))
    def coverage(stages):
        render('frag',black,output,{'_SkinTex':white,'_SurfaceTex':surface_texture},dict(values,_Stages=stages))
        return read(output)[:,:,0]
    stages=np.ones(len(groups));old_stages=stages.copy();old_stages[11:]=0
    alpha=coverage(stages);old_alpha=coverage(old_stages)
    new_only=np.zeros(len(groups));new_only[11:]=1
    boundary_alpha=pixel_sample(coverage(new_only),np.array(values['_Boundary'])[:,:2])
    yy,xx=np.mgrid[:size,:size];pixels=np.stack((xx+.5,yy+.5),axis=-1)
    origin=np.array(values['_FrameOrigin'][:2]);width,height=values['_FrameOrigin'][2:]
    x=(pixels[:,:,0]-origin[0])/width;y=(pixels[:,:,1]-origin[1])/height
    clean=np.array([.58,.405,.305])+x[:,:,None]*np.array([.22,.12,.07])+y[:,:,None]*np.array([.12,.105,.075])
    clean-=np.exp(-(((x+.18)/.42)**2+((y-.05)/.58)**2))[:,:,None]*np.array([.085,.07225,.0595])
    luma=np.array([.2126,.7152,.0722]);core=alpha>.995
    surface_coverage=read(surface_texture)[:,:,0]
    outside=distance_transform_edt(surface_coverage<.5)>=2
    def save(name,rgb):
        Image.fromarray((np.clip(rgb[::-1],0,1)*255).astype('uint8')).save(work/name)
    def composite(source,skin,case):
        render('frag',source,output,{'_SkinTex':skin,'_SurfaceTex':surface_texture},case)
        actual=read(output)[:,:,:3];assert np.isfinite(actual).all()
        return actual
    def rmse(error,area):return float(np.sqrt(np.mean(error[area]**2)))
    # These named anatomical points are fixed independently of the fitted mask
    # center, so a misplaced region cannot move the oracle along with itself.
    target_ids=[50,280,140,369]
    hotspots=np.zeros((size,size))
    for point in points[target_ids]:
        hotspots=np.maximum(hotspots,np.exp(-(((pixels-point)/(width*.024))**2).sum(-1)*.5))
    dirty=np.clip(clean+hotspots[:,:,None]*.3,0,1)
    source=tex((size,size),np.dstack((dirty,np.ones((size,size)))))
    skin=reconstruct(source,values)
    actual=composite(source,skin,values)
    old_actual=composite(source,skin,dict(values,_Stages=old_stages))
    area=hotspots>.6
    target_coverage=pixel_sample(alpha,points[target_ids])
    target_old_coverage=pixel_sample(old_alpha,points[target_ids])
    coverage_report={'anatomical_target_landmarks':target_ids,
                     'target_alpha':target_coverage.tolist(),'previous_stages_alpha':target_old_coverage.tolist(),
                     'input_highlight_luma_excess':float(((dirty-clean)@luma)[area].mean()),
                     'previous_stages_rmse':rmse(old_actual-clean,area),'expanded_stages_rmse':rmse(actual-clean,area),
                     'expanded_positive_luma_excess':float(np.maximum((actual-clean)@luma,0)[area].mean()),
                     'outside_surface_max_error':float(np.abs(actual[outside]-dirty[outside]).max()),
                     'new_regions_boundary_max_alpha':float(boundary_alpha.max())}
    coverage_report['per_target']=[]
    for landmark in target_ids:
        local_area=(((pixels-points[landmark])**2).sum(-1)<(width*.024)**2)
        coverage_report['per_target'].append({'landmark':landmark,
            'min_alpha':float(alpha[local_area].min()),'mean_alpha':float(alpha[local_area].mean()),
            'rmse':rmse(actual-clean,local_area),
            'positive_luma_excess':float(np.maximum((actual-clean)@luma,0)[local_area].mean()),
            'outside_surface_fraction':float((surface_coverage[local_area]<.5).mean())})
    save('highlight-coverage-comparison.png',np.concatenate((dirty,old_actual,actual,clean),axis=1))
    # Place a bright source in the soft edge, away from cheek donors and the
    # silhouette. This exercises seed compression; fully excluded highlights
    # above exercise coverage instead, and must not be confused with it.
    base_source=tex((size,size),np.dstack((clean,np.ones((size,size)))))
    reconstruct(base_source,values)
    seed=read(seed_texture)
    u=np.array(values['_FrameU'][:2]);v=np.array(values['_FrameV'][:2])
    delta=pixels-origin
    atlas=np.stack((delta@u/(u@u),delta@v/(v@v)),axis=-1)+.5
    confidence=pixel_sample(seed[:,:,3],atlas.reshape(-1,2)*seed_texture.width).reshape(size,size)
    donor_pixels=np.array(values['_Donors'])[:,:2]
    donor_distance=np.sqrt(((pixels[:,:,None,:]-donor_pixels)**2).sum(-1)).min(-1)
    candidates=(alpha>.3)&(alpha<.7)&(confidence>.65)&(donor_distance>width*.065)&(np.abs(x)<.42)
    assert candidates.sum()>10,('no eligible independent highlight source',int(candidates.sum()))
    # Favor the brightest blend fraction with valid source confidence. This
    # chooses a source-to-effect edge, not a core hole or an unmodified pixel.
    score=np.where(candidates,alpha,-1)
    sy,sx=np.unravel_index(np.argmax(score),score.shape);center=pixels[sy,sx]
    seed_hotspot=np.exp(-(((pixels-center)/(width*.025))**2).sum(-1)*.5)
    dirty=np.clip(clean+seed_hotspot[:,:,None]*.31,0,1)
    source=tex((size,size),np.dstack((dirty,np.ones((size,size)))))
    influence=(seed_hotspot>.08)&(alpha>.1);outcomes={};images={}
    for label,strength in [('uncompressed',0.),('compressed',.85)]:
        case=dict(values,_HighlightSuppression=strength)
        skin=reconstruct(source,case);actual=composite(source,skin,case)
        outcomes[label]={'influence_rmse':rmse(actual-clean,influence),
                         'core_rmse':rmse(actual-clean,core),
                         'positive_luma_excess':float(np.maximum((actual-clean)@luma,0)[influence].mean()),
                         'outside_surface_max_error':float(np.abs(actual[outside]-dirty[outside]).max())}
        images[label]=actual
    save('highlight-source-comparison.png',np.concatenate((dirty,images['uncompressed'],images['compressed'],clean),axis=1))
    donor_profiles=[]
    for yaw in [-70,-45,0,45,70]:
        angle=np.deg2rad(yaw)
        rotation=np.array([[np.cos(angle),0,np.sin(angle)],[0,1,0],[-np.sin(angle),0,np.cos(angle)]])
        rotated=canonical@rotation.T
        projected=rotated[:,:2]*(600/(45-rotated[:,2]))[:,None]+np.array([size*.48,size*.53])
        case,_=fit_regions(projected);case=dict(values,**case)
        surface.render(projected,surface_texture)
        skin=reconstruct(base_source,case)
        actual=composite(base_source,skin,case)
        confidences=read(donor_texture)[0,:,3]
        donor_profiles.append({'yaw':yaw,'confidences':confidences.tolist(),
                               'max_confidence':float(confidences.max()),
                               'finite_color':bool(np.isfinite(actual).all())})
    report={'synthetic_only':True,'unity_editor_tested':False,'metal_tested':False,
            'coverage':coverage_report,'seed_source_pixel':center.tolist(),
            'seed_source_confidence':float(confidence[sy,sx]),'source_compression':outcomes,
            'profile_donors':donor_profiles}
    (work/'highlight-checks.json').write_text(json.dumps(report,indent=2));print('highlight checks:',json.dumps(report))
    assert min(target_coverage)>.995,coverage_report
    assert coverage_report['expanded_stages_rmse']<coverage_report['previous_stages_rmse'],coverage_report
    assert coverage_report['expanded_positive_luma_excess']<coverage_report['input_highlight_luma_excess']*.25,coverage_report
    assert coverage_report['outside_surface_max_error']<1e-5,coverage_report
    assert coverage_report['new_regions_boundary_max_alpha']<.01,coverage_report
    assert outcomes['compressed']['influence_rmse']<outcomes['uncompressed']['influence_rmse'],outcomes
    assert outcomes['compressed']['positive_luma_excess']<outcomes['uncompressed']['positive_luma_excess'],outcomes
    assert outcomes['compressed']['outside_surface_max_error']<1e-5,outcomes
    for case in donor_profiles:
        assert case['max_confidence']>.02,('all cheek color sources lost',case)
    return report

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--work',type=Path,required=True);ap.add_argument('--egl');a=ap.parse_args()
    kwargs={'backend':'egl'}
    if a.egl:kwargs['libegl']=a.egl
    # GLSL permits some HLSL reserved names: catch that known translation blind
    # spot explicitly. This small lint is not an HLSL or ShaderLab compiler.
    reserved_declaration=re.compile(r'\b(?:float|half|fixed|int|uint|bool)[1-4]?(?:x[1-4])?\s+(line|point|triangle)\b')
    checked=[]
    for shader_path in sorted(SRC.glob('FacelessSkin*'))+[SRC/'FacelessSurface.shader']:
        if shader_path.suffix not in ('.shader','.cginc'):continue
        source=shader_path.read_text()
        source=re.sub(r'/\*.*?\*/|//[^\n]*','',source,flags=re.S)
        assert not reserved_declaration.search(source),('HLSL reserved identifier',shader_path)
        checked.append(shader_path.name)
    ctx=moderngl.create_standalone_context(**kwargs)
    print('renderer:',ctx.info['GL_RENDERER'])
    image=np.asarray(Image.open(a.work/'astronaut.png').convert('RGB'),dtype=np.float32)[::-1]/255
    h,w=image.shape[:2]
    points=np.array(json.loads((a.work/'astronaut-landmarks.json').read_text()))[:,:2]
    points[:,0]*=w;points[:,1]=(1-points[:,1])*h
    vals,groups=fit_regions(points)
    vals.update(_CameraSize=[w,h,1/w,1/h],_Amount=1.,_Volume=0.,_Grain=0.,_ShowMask=0.,
                _Color=[1,1,1,1],_Stages=np.ones(len(groups)),_VideoVisibility=1.,_LocalColorStrength=1.,_HighlightSuppression=.85)
    buffer=ctx.buffer(np.array([-1,-1,1,-1,-1,1,1,1],dtype='f4').tobytes())
    programs={}
    for name in ['fragDonors','fragGuide','fragSeeds','fragGaussian','fragNormalize','fragPull',
                 'fragRelax','fragTemporal','fragRestoreColor','frag']:
        composite=name=='frag'
        body=shader_body(composite)
        setup='v2f i; i.uv=uv; i.color=vec4(1); i.local=vec4(0);' if composite else 'v2f_img i; i.uv=uv;'
        # Model the UnityCG symbol that the original standalone harness omitted.
        # This deliberately fails if the project reintroduces a duplicate helper.
        builtin='float Luminance(vec3 c){return dot(c,vec3(0.22,0.707,0.071));}\n'
        frag='#version 330\n#define saturate(x) clamp(x,0.0,1.0)\nin vec2 uv; out vec4 result;\n'+builtin+body+'\nvoid main(){'+setup+'result='+name+'(i);}'
        (a.work/(name+'.glsl')).write_text(frag)
        prog=ctx.program(vertex_shader=VERT,fragment_shader=frag)
        programs[name]=(prog,ctx.simple_vertex_array(prog,buffer,'position'))
    print('compiled fragment programs:',len(programs))
    def tex(size,data=None):
        t=ctx.texture(size,4,None if data is None else data.astype('f4').tobytes(),dtype='f4')
        t.filter=(moderngl.LINEAR,moderngl.LINEAR);t.repeat_x=t.repeat_y=False
        return t
    original=tex((w,h),np.dstack((image,np.ones((h,w)))))
    surface=SurfaceGuard(ctx,a.work)
    surface_texture=tex((w,h));surface.render(points,surface_texture)
    def render(name,src,target,bindings=None,values=None):
        prog,vao=programs[name]
        for key,value in dict(vals,**(values or {})).items():
            if key not in prog:continue
            arr=np.array(value,dtype='f4')
            if arr.ndim>1 or key=='_Stages':prog[key].write(arr.tobytes())
            else:prog[key].value=float(arr) if arr.ndim==0 else tuple(arr)
        for unit,(key,t) in enumerate(dict(_MainTex=src,**(bindings or {})).items()):
            if key in prog:t.use(unit);prog[key].value=unit
        if '_MainTex_TexelSize' in prog:prog['_MainTex_TexelSize'].value=(1/src.width,1/src.height,src.width,src.height)
        fb=ctx.framebuffer(color_attachments=[target]);fb.use();ctx.viewport=(0,0,target.width,target.height)
        vao.render(moderngl.TRIANGLE_STRIP);fb.release()
    donors=tex((6,1));guide=tex((256,256));restored=tex((256,256))
    sizes=[256,128,64,32,16,8,4]
    known=[tex((s,s)) for s in sizes];temp=[tex((s,s)) for s in sizes];filled=[tex((s,s)) for s in sizes]
    def reconstruct(source,values):
        render('fragDonors',source,donors,values=values)
        render('fragGuide',source,guide,{'_DonorTex':donors},values)
        render('fragSeeds',source,known[0],{'_DonorTex':donors,'_GuideTex':guide},values)
        for i in range(len(sizes)-1):
            render('fragGaussian',known[i],temp[i],values=dict(values,_Direction=[1,0]))
            render('fragGaussian',temp[i],known[i+1],values=dict(values,_Direction=[0,1]))
        render('fragNormalize',known[-1],filled[-1],{'_DonorTex':donors},values)
        for i in range(len(sizes)-2,-1,-1):
            render('fragPull',filled[i+1],filled[i],{'_KnownTex':known[i]},values)
            for _ in range(2):
                render('fragRelax',filled[i],temp[i],{'_KnownTex':known[i]},dict(values,_Direction=[1,0]))
                render('fragRelax',temp[i],filled[i],{'_KnownTex':known[i]},dict(values,_Direction=[0,1]))
        render('fragRestoreColor',filled[0],restored,{'_GuideTex':guide},values)
        return restored
    skin=reconstruct(original,vals)
    bindings={'_SkinTex':skin,'_SurfaceTex':surface_texture}
    output=tex((w,h));render('frag',original,output,bindings)
    def read(t):return np.frombuffer(t.read(),dtype='f4').reshape(t.height,t.width,4).copy()
    result=read(output);assert np.isfinite(result).all()
    seed_data=read(known[0])
    origin=np.array(vals['_FrameOrigin'][:2])
    u=np.array(vals['_FrameU'][:2]);v=np.array(vals['_FrameV'][:2])
    excluded=[]
    for landmark in [159,386,1,13,14,105,334,206,426,202,422,50,280,140,369]:
        delta=points[landmark]-origin
        atlas=np.array([delta@u/(u@u),delta@v/(v@v)])+.5
        x,y=np.clip((atlas*256).astype(int),0,255)
        confidence=float(seed_data[y,x,3])
        assert confidence<1e-6,(landmark,confidence)
        excluded.append(landmark)
    Image.fromarray((np.clip(result[::-1,:,:3],0,1)*255).astype('uint8')).save(a.work/'result.png')
    render('frag',original,output,bindings, {'_ShowMask':1.})
    debug=read(output)
    Image.fromarray((np.clip(debug[::-1,:,:3],0,1)*255).astype('uint8')).save(a.work/'regions.png')
    render('frag',original,output,bindings, {'_Amount':0.})
    passthrough=read(output)[:,:,:3]
    err=float(np.max(np.abs(passthrough-image)));assert err<1e-5,err
    # Regions are confined to the local face bounds; outer video is untouched.
    yy,xx=np.mgrid[:h,:w];b=vals['_FaceBounds']
    outside=(xx<b[0]-2)|(xx>b[2]+2)|(yy<b[1]-2)|(yy>b[3]+2)
    outside_error=float(np.max(np.abs(result[:,:,:3][outside]-image[outside])))
    assert outside_error<1e-5,outside_error
    profiles=synthetic_projection_tests(ctx,a.work,surface,tex,render,read)
    color_report=color_gradient_tests(a.work,surface,tex,render,read,reconstruct,known[0],donors)
    highlight_report=highlight_tests(a.work,surface,tex,render,read,reconstruct,known[0],donors)
    stats={'fragment_programs':len(programs),'surface_vertex_and_fragment_compiled':True,
           'hlsl_reserved_name_lint':checked,'passthrough_max_error':err,'outside_max_error':outside_error,
           'zero_source_confidence_landmarks':excluded,
           'finite_output':True,'unity_editor_tested':False,'metal_tested':False,
           'synthetic_profiles':profiles,'synthetic_color':color_report,'synthetic_highlights':highlight_report}
    (a.work/'checks.json').write_text(json.dumps(stats,indent=2));print(stats)

if __name__=='__main__':main()
