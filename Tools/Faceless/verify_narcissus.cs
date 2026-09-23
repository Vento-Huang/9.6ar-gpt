// Compile with the actual NarcissusModel.cs and NarcissusColonyLayout.cs. Math stubs are independent of Unity.
// Exports the actual evaluated mesh, not a separately generated approximation.
using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UnityEngine;
static class NarcissusChecks
{
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    static float[] XYZ(Vector3 p) => new[]{p.x,p.y,p.z};
    static void Main()
    {
        var model=new NarcissusModel(); var vertices=new Vector3[model.VertexCount];
        Check(model.VertexCount*38+12*13*6<120000,"colony exceeds detailed mesh budget");
        Check(model.Triangles.All(i=>i>=0&&i<model.VertexCount),"invalid topology");
        model.Evaluate(0,0,0,Vector3.zero,Quaternion.identity,1,vertices,0);
        Check(vertices.All(p=>p.magnitude<1e-7),"birth must start at root");
        var frames=new System.Collections.Generic.Dictionary<string,float[][]>();
        foreach(float seconds in new[]{0f,3f,7f,14f,20f,21f,31f})
        {
            model.Evaluate(seconds,11,0,Vector3.zero,Quaternion.identity,1,vertices,0);
            Check(vertices.All(p=>float.IsFinite(p.x)&&float.IsFinite(p.y)&&float.IsFinite(p.z)),"nonfinite vertex");
            frames[seconds.ToString(CultureInfo.InvariantCulture)]=vertices.Select(XYZ).ToArray();
        }
        Check(frames["21"].Zip(frames["31"],(a,b)=>a.SequenceEqual(b)).All(x=>x),"latest flower unfinished at 21");
        Check(!frames["20"].Zip(frames["21"],(a,b)=>a.SequenceEqual(b)).All(x=>x),"latest flower finishes too early");
        model.Evaluate(21,0,0,Vector3.zero,Quaternion.identity,1,vertices,0);
        int nondegenerate=0;
        for(int i=0;i<model.Triangles.Length;i+=3)
        {
            var a=vertices[model.Triangles[i]];var b=vertices[model.Triangles[i+1]];var c=vertices[model.Triangles[i+2]];
            if(Vector3.Cross(b-a,c-a).magnitude>1e-8) nondegenerate++;
        }
        Check(nondegenerate==model.Triangles.Length/3,"degenerate mature geometry");
        var edges=new System.Collections.Generic.Dictionary<long,int>();
        for(int t=0;t<model.Triangles.Length;t+=3)
        {
            int part=model.Parts[model.Triangles[t]];if(part!=1 && part!=2)continue;
            for(int e=0;e<3;e++)
            {
                int a=model.Triangles[t+e],b=model.Triangles[t+(e+1)%3];
                long key=((long)Math.Min(a,b)<<32)|(uint)Math.Max(a,b);
                edges[key]=edges.ContainsKey(key)?edges[key]+1:1;
            }
        }
        Check(edges.Count>0 && edges.Values.All(n=>n==2),"petal/leaf shell has an open or nonmanifold edge");
        for(int i=0;i<model.VertexCount;i++)if(model.Parts[i]==3)
        {
            Vector3 p=model.Open[i];
            Check(p.x*p.x+p.y*p.y<.33f*.33f && p.z<.47f,"cup details exceed protected volume");
        }
        Console.WriteLine("PASS: watertight leaf/petal shells and cup details inside existing protection volume.");
        var anchorText=File.ReadAllText("Assets/MediaPipeUnity/Samples/Scenes/Face Landmark Detection/NarcissusFaceGrowth.cs").Split(new[]{"Anchors={"},StringSplitOptions.None)[1].Split(new[]{"};"},StringSplitOptions.None)[0];
        var anchors=Regex.Matches(anchorText,@"\d+").Cast<Match>().Select(m=>int.Parse(m.Value)).ToArray();
        Check(anchors.Length==NarcissusColonyLayout.Count,"layout and anchors disagree");
        var canonical=File.ReadAllLines(Environment.GetEnvironmentVariable("NARCISSUS_CANONICAL"))
            .Where(l=>l.StartsWith("v ")).Select(l=>l.Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(x=>float.Parse(x,CultureInfo.InvariantCulture)).ToArray()).Select(v=>new Vector3(v[0],v[1],0)).ToArray();
        Vector3 midpoint=new Vector3(canonical.Average(v=>v.x),canonical.Average(v=>v.y),0);
        float width=canonical.Max(v=>v.x)-canonical.Min(v=>v.x);
        var roots=anchors.Select(i=>canonical[i]-midpoint).ToArray();
        var colony=new NarcissusColonyLayout();var desired=new Vector3[38];var radii=new float[38];
        float minimumMargin=float.PositiveInfinity;
        foreach(int seed in new[]{1,7,19,103})
        foreach(float yaw in new[]{-80f,-45f,0f,45f,80f})
        {
            colony.Reset(seed);var rotation=Quaternion.Euler(12,yaw,9);
            Check(colony.Sizes.Max()/colony.Sizes.Min()>2,"insufficient size variation");
            Check(colony.Delays.Max()==11 && colony.Delays.Min()>=0,"21-second schedule changed");
            for(int i=0;i<38;i++)
            {
                desired[i]=rotation*(roots[i]+colony.Rotations[i]*((NarcissusModel.AttachedBase+new Vector3(0,0,.12f))*(width*colony.Sizes[i]*1.35f))+colony.Jitter[i]*width);
                radii[i]=width*colony.Sizes[i]*1.35f*1.13f;
            }
            colony.Solve(desired,radii,rotation,width);
            colony.Solve(desired,radii,rotation,width);
            for(int i=0;i<38;i++)for(int j=0;j<i;j++)
            {
                float margin=(colony.Centers[i]-colony.Centers[j]).magnitude-radii[i]-radii[j];
                minimumMargin=Math.Min(minimumMargin,margin);
            }
            for(int i=0;i<38;i++)
            {
                float depth=Vector3.Dot(colony.Centers[i]-desired[i],rotation*Vector3.forward);
                Check(depth<=width*.02201f && depth>=-.00001f,"crown lifted away from skin");
            }
            var previous=colony.Centers.ToArray();colony.Solve(desired,radii,rotation,width);
            Check(previous.Zip(colony.Centers,(x,y)=>(x-y).magnitude).Max()<.00001f,"stationary layout jitter");
        }
        colony.Reset(19);
        var snapshots=new System.Collections.Generic.Dictionary<string,float[][]>();
        var colonyVertices=new Vector3[model.VertexCount*38];
        foreach(float seconds in new[]{100f,7f,14f,21f})
        {
            for(int i=0;i<38;i++)
            {
                float d=colony.Delays[i],stem=NarcissusModel.Ease(seconds,d,d+6),bud=NarcissusModel.Ease(seconds,d+2,d+6);
                desired[i]=roots[i]+colony.Rotations[i]*((NarcissusModel.AttachedBase*stem+new Vector3(0,0,.12f)*bud)*(width*colony.Sizes[i]*1.35f))+colony.Jitter[i]*(width*stem);
                radii[i]=width*colony.Sizes[i]*1.35f*1.13f*bud;
            }
            colony.Solve(desired,radii,Quaternion.identity,width);
            for(int i=0;i<38;i++)
            {
                float d=colony.Delays[i],stem=NarcissusModel.Ease(seconds,d,d+6);
                Vector3 baseCenter=desired[i]-colony.Jitter[i]*(width*stem);
                model.Evaluate(seconds,d,0,roots[i],colony.Rotations[i],width*colony.Sizes[i]*1.35f,colonyVertices,i*model.VertexCount,colony.Centers[i]-baseCenter,true);
            }
            snapshots[seconds.ToString(CultureInfo.InvariantCulture)]=colonyVertices.Select(XYZ).ToArray();
        }
        Console.WriteLine("PASS: 20 seeded/rotated depth-bounded crown layouts, stationary stability, variable scales and schedule; envelope overlap permitted; minimum envelope gap="+minimumMargin);
        var attached=new Vector3[model.VertexCount];
        model.Evaluate(21,0,0,Vector3.zero,Quaternion.identity,1,attached,0,Vector3.zero,true);
        Check(attached.Where((p,i)=>model.Parts[i]==0).Max(p=>p.z)<.191f,"stem is too long");
        for(int i=0;i<model.VertexCount;i++)
            if(model.Parts[i]>=2)
                Check((attached[i]-vertices[i]-new Vector3(0,0,NarcissusModel.AttachedBase.z-NarcissusModel.FlowerBase.z)).magnitude<.00001f,"flower shape changed");
        Console.WriteLine("PASS: attached stem <=0.19 model units; mature flower geometry preserved by translation.");
        var swayed=new Vector3[model.VertexCount];
        Vector3 swayTest=new Vector3(.02f,-.01f,0);
        model.Evaluate(21,0,0,Vector3.zero,Quaternion.identity,1,swayed,0,Vector3.zero,true,swayTest);
        for(int i=0;i<model.VertexCount;i++)
        {
            if(model.Parts[i]>=2) Check((swayed[i]-attached[i]-swayTest).magnitude<.00001f,"flower joint translation broken");
            if(model.Parts[i]==0 && model.UV[i].y==0) Check((swayed[i]-attached[i]).magnitude<.00001f,"root joint moved");
        }
        Console.WriteLine("PASS: joint deformation preserves pinned roots and rigid flower crowns.");
        string destination=Environment.GetEnvironmentVariable("NARCISSUS_OUTPUT");
        if(!string.IsNullOrEmpty(destination))
        {
            Directory.CreateDirectory(destination);
            var obj=new StringBuilder("# Original procedural Narcissus. Mature mesh; growth is driven in Unity.\nmtllib Narcissus.mtl\n");
            foreach(var p in vertices) obj.AppendFormat(CultureInfo.InvariantCulture,"v {0:R} {1:R} {2:R}\n",p.x,p.y,p.z);
            foreach(var uv in model.UV) obj.AppendFormat(CultureInfo.InvariantCulture,"vt {0:R} {1:R}\n",uv.x,uv.y);
            var normals=new Vector3[model.VertexCount];
            for(int j=0;j<model.Triangles.Length;j+=3)
            {
                int a=model.Triangles[j],b=model.Triangles[j+1],c=model.Triangles[j+2];
                Vector3 n=Vector3.Cross(vertices[b]-vertices[a],vertices[c]-vertices[a]);
                normals[a]+=n;normals[b]+=n;normals[c]+=n;
            }
            foreach(var n in normals)
            {var unit=n/Math.Max(n.magnitude,.000001f);obj.AppendFormat(CultureInfo.InvariantCulture,"vn {0:R} {1:R} {2:R}\n",unit.x,unit.y,unit.z);}
            int last=-1;
            for(int i=0;i<model.Triangles.Length;i+=3)
            {
                int part=model.Parts[model.Triangles[i]];
                var materialColor=model.Colors[model.Triangles[i]];
                if(part==3 && materialColor.r>.9f) part=materialColor.b>.2f?4:5;
                if(part!=last) { obj.Append("g part_"+part+"\nusemtl part_"+part+"\n"); last=part; }
                int a=model.Triangles[i]+1,b=model.Triangles[i+1]+1,c=model.Triangles[i+2]+1;
                obj.Append($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}\n");
            }
            File.WriteAllText(Path.Combine(destination,"Narcissus.obj"),obj.ToString());
            File.WriteAllText(Path.Combine(destination,"Narcissus.mtl"),"newmtl part_0\nKd 0.18 0.26 0.12\nNs 35\n\nnewmtl part_1\nKd 0.22 0.32 0.16\nNs 35\n\nnewmtl part_2\nKd 0.91 0.91 0.79\nNs 45\n\nnewmtl part_3\nKd 0.82 0.60 0.19\nNs 40\n\nnewmtl part_4\nKd 0.92 0.72 0.30\nNs 35\n\nnewmtl part_5\nKd 0.95 0.68 0.13\nNs 25\n");
            string preview=Environment.GetEnvironmentVariable("NARCISSUS_PREVIEW_DATA");
            if(!string.IsNullOrEmpty(preview)) File.WriteAllText(preview,JsonSerializer.Serialize(new {
                open=model.Open.Select(XYZ),closed=model.Closed.Select(XYZ),parts=model.Parts,triangles=model.Triangles,
                uv=model.UV.Select(p=>new[]{p.x,p.y}),colors=model.Colors.Select(c=>new[]{c.r,c.g,c.b,c.a}),frames=frames,
                mature=vertices.Select(XYZ),colonies=snapshots,roots=roots.Select(XYZ),sizes=colony.Sizes,delays=colony.Delays}));
        }
        Console.WriteLine($"PASS: {model.VertexCount} vertices / {nondegenerate} triangles; root birth, finite stages, 21-second completion, 38-flower 32-bit index budget; actual mesh exported.");
    }
}
namespace UnityEngine
{
    public struct Vector2 {public float x,y;public Vector2(float x,float y){this.x=x;this.y=y;}}
    public struct Color {public float r,g,b,a;public Color(float r,float g,float b,float a){this.r=r;this.g=g;this.b=b;this.a=a;}}
    public struct Quaternion {
        System.Numerics.Quaternion q;
        public static Quaternion identity=>new Quaternion{q=System.Numerics.Quaternion.Identity};
        public static Quaternion Euler(float x,float y,float z)=>new Quaternion{q=System.Numerics.Quaternion.CreateFromYawPitchRoll(y*Mathf.PI/180,x*Mathf.PI/180,z*Mathf.PI/180)};
        public static Quaternion Inverse(Quaternion a)=>new Quaternion{q=System.Numerics.Quaternion.Inverse(a.q)};
        public static Vector3 operator*(Quaternion a,Vector3 p){var v=System.Numerics.Vector3.Transform(new System.Numerics.Vector3(p.x,p.y,p.z),a.q);return new Vector3(v.X,v.Y,v.Z);}
    }
    public struct Vector3
    {
        public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
        public static Vector3 zero=>new Vector3(0,0,0);public static Vector3 right=>new Vector3(1,0,0);public static Vector3 up=>new Vector3(0,1,0);public static Vector3 forward=>new Vector3(0,0,1);public float sqrMagnitude=>x*x+y*y+z*z;public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;public static Vector3 operator/(Vector3 a,float b)=>a*(1/b);public float magnitude=>(float)Math.Sqrt(x*x+y*y+z*z);
        public static Vector3 operator+(Vector3 a,Vector3 b)=>new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator-(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator*(Vector3 a,float b)=>new Vector3(a.x*b,a.y*b,a.z*b);
        public static Vector3 Lerp(Vector3 a,Vector3 b,float t)=>a+(b-a)*Mathf.Clamp01(t);
        public static Vector3 Cross(Vector3 a,Vector3 b)=>new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
    }
    public static class Mathf
    {
        public const float PI=(float)Math.PI;
        public static float Sqrt(float x)=>(float)Math.Sqrt(x);public static float Sin(float x)=>(float)Math.Sin(x);public static float Cos(float x)=>(float)Math.Cos(x);
        public static float Pow(float x,float y)=>(float)Math.Pow(x,y);public static float Abs(float x)=>Math.Abs(x);
        public static float Min(float x,float y)=>Math.Min(x,y);public static float Max(float x,float y)=>Math.Max(x,y);public static float Clamp01(float x)=>Math.Max(0,Math.Min(1,x));
        public static float Lerp(float a,float b,float t)=>a+(b-a)*Clamp01(t);
    }
}
