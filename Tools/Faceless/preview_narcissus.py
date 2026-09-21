"""Render the C#-exported mesh with the production flower fragment shader.
Mesa/OpenGL preview only: not Unity/Metal or live tracking validation.
Usage: python preview_narcissus.py /path/to/qa /path/to/output.png
"""
import sys,json,re
from pathlib import Path
import numpy as np
import moderngl
from PIL import Image,ImageDraw,ImageFont

root=Path(__file__).resolve().parents[2]
src=root/'Assets/MediaPipeUnity/Samples/Scenes/Face Landmark Detection'
work=Path(sys.argv[1]);output=Path(sys.argv[2])
data=json.loads((work/'narcissus-model.json').read_text())
points=np.array(data['mature']);indices=np.array(data['triangles']).reshape(-1,3)
uv=np.array(data['uv']);colors=np.array(data['colors']);parts=np.array(data['parts'])
ctx=moderngl.create_standalone_context(backend='egl',libegl='libEGL.so.1')
body=(src/'Resources/FacelessNarcissus.shader').read_text().split('float4 frag(v2f i):SV_Target')[1].split('ENDCG')[0]
for a,b in [('float4','vec4'),('float3','vec3'),('float2','vec2'),('lerp','mix')]:body=re.sub(r'\b'+a+r'\b',b,body)
fragment='#version 330\n#define saturate(x) clamp(x,0.0,1.0)\nstruct v2f{vec3 normal;vec4 color;vec2 uv;};\nin vec3 N;in vec4 C;in vec2 U;out vec4 result;\nvec4 shade(v2f i)'+body+'\nvoid main(){v2f i;i.normal=N;i.color=C;i.uv=U;result=shade(i);}'
vertex='''#version 330
in vec3 P;in vec3 normal;in vec4 color;in vec2 uv;
out vec3 N;out vec4 C;out vec2 U;uniform vec4 bounds;
void main(){gl_Position=vec4((P.xy-bounds.xy)/bounds.zw*2.-1.,-P.z/30.,1.);N=normal;C=color;U=uv;}
'''
program=ctx.program(vertex_shader=vertex,fragment_shader=fragment)
# Compile the actual projection body for both UV/depth branches too. This is
# GLSL translation validation, not a claim of native Metal compilation.
production_vertex=(src/'Resources/FacelessNarcissus.shader').read_text().split('v2f vert(appdata v)')[1].split('float4 frag')[0]
for a,b in [('float4','vec4'),('float3','vec3'),('float2','vec2'),('lerp','mix')]:production_vertex=re.sub(r'\b'+a+r'\b',b,production_vertex)
for defines in ['#define UNITY_NEAR_CLIP_VALUE -1.0\n', '#define UNITY_UV_STARTS_AT_TOP 1\n#define UNITY_REVERSED_Z 1\n']:
    test_vertex='''#version 330
#define saturate(x) clamp(x,0.0,1.0)
'''+defines+'''
struct appdata {vec4 vertex;vec3 normal;vec4 color;vec2 uv;};
struct v2f {vec4 position;vec3 normal;vec4 color;vec2 uv;};
uniform vec4 _PlantBounds;uniform float _DepthScale;
in vec3 P;in vec3 normal;in vec4 color;in vec2 uv;
out vec3 N;out vec4 C;out vec2 U;
v2f project(appdata v)'''+production_vertex+'''
void main(){appdata v;v.vertex=vec4(P,1);v.normal=normal;v.color=color;v.uv=uv;
v2f o=project(v);gl_Position=o.position;N=o.normal;C=o.color;U=o.uv;}
'''
    check_program=ctx.program(vertex_shader=test_vertex,fragment_shader=fragment);check_program.release()
def render(p,tri,col,texcoord,bounds,size):
    n=np.zeros_like(p)
    cross=np.cross(p[tri[:,1]]-p[tri[:,0]],p[tri[:,2]]-p[tri[:,0]])
    for corner in range(3):np.add.at(n,tri[:,corner],cross)
    n/=np.maximum(1e-12,np.linalg.norm(n,axis=1))[:,None]
    packed=np.c_[p,n,col,texcoord].astype('f4')
    vbo=ctx.buffer(packed.tobytes());ibo=ctx.buffer(tri.astype('i4').tobytes())
    vao=ctx.vertex_array(program,[(vbo,'3f 3f 4f 2f','P','normal','color','uv')],ibo)
    texture=ctx.texture(size,4,dtype='f4');depth=ctx.depth_renderbuffer(size)
    fbo=ctx.framebuffer([texture],depth);fbo.use();fbo.clear(.045,.052,.05,1,depth=1)
    ctx.enable(moderngl.DEPTH_TEST);ctx.depth_func='<=';program['bounds'].value=bounds
    vao.render()
    result=np.frombuffer(texture.read(),dtype='f4').reshape(size[1],size[0],4)
    assert np.isfinite(result).all()
    image=Image.fromarray((np.clip(result[::-1,:,:3],0,1)*255).astype('uint8'))
    for resource in [vao,vbo,ibo,fbo,depth,texture]:resource.release()
    return image

# Angled single flower: exposes corona depth and three-dimensional leaf curvature.
y=np.deg2rad(-24);x=np.deg2rad(14)
ry=np.array([[np.cos(y),0,np.sin(y)],[0,1,0],[-np.sin(y),0,np.cos(y)]])
rx=np.array([[1,0,0],[0,np.cos(x),-np.sin(x)],[0,np.sin(x),np.cos(x)]])
single=render((points-[0,.1,.65])@(rx@ry).T,indices,colors,uv,[-1.65,-1.65,3.3,3.3],(720,720))
canonical=np.array([[float(v) for v in line.split()[1:]] for line in (work/'canonical_face_model.obj').read_text().splitlines() if line.startswith('v ')])
canonical-=canonical.mean(0)
anchors=list(map(int,re.findall(r'\d+', (src/'NarcissusFaceGrowth.cs').read_text().split('Anchors={')[1].split('};')[0])))
width=np.ptp(canonical[:,0]);scale=width*.12
opened=np.array(data['open']);closed=np.array(data['closed'])
def ease(t,a,b):
    t=np.clip((t-a)/(b-a),0,1);return t*t*(3-2*t)
def colony(seconds):
    p=np.array(data['colonies'][str(seconds)])
    tri=np.concatenate([indices+k*len(points) for k in range(len(anchors))])
    return render(p,tri,np.tile(colors,(len(anchors),1)),np.tile(uv,(len(anchors),1)),[-9,-11,18,22],(360,440))

board=Image.new('RGB',(1440,840),(11,13,12));board.paste(single,(0,75))
draw=ImageDraw.Draw(board)
try:font=ImageFont.truetype('/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf',20)
except OSError:font=ImageFont.load_default()
draw.text((30,25),'NARCISSUS  /  ORIGINAL 3D MODEL',font=font,fill=(225,225,212))
draw.text((740,25),'VARIED SIZES / SPACED FLOWER CROWNS',font=font,fill=(225,225,212))
for column,seconds in enumerate([7,21]):
    board.paste(colony(seconds),(720+column*360,150))
    draw.text((750+column*360,615),f'{seconds:02d} SECONDS',font=font,fill=(225,225,212))
draw.text((30,800),'MODEL PREVIEW  /  SYNTHETIC LANDMARKS  /  NOT A LIVE CAMERA CAPTURE',font=font,fill=(140,148,138))
output.parent.mkdir(parents=True,exist_ok=True);board.save(output)
print('PASS: actual exported model + production fragment shader; preview',output)
