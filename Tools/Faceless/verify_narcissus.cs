// Compile with the actual NarcissusModel.cs. Math stubs are independent of Unity.
// Exports the actual evaluated mesh, not a separately generated approximation.
using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text;
using System.Text.Json;
using UnityEngine;
static class NarcissusChecks
{
    static void Check(bool ok,string message) { if(!ok) throw new Exception(message); }
    static float[] XYZ(Vector3 p) => new[]{p.x,p.y,p.z};
    static void Main()
    {
        var model=new NarcissusModel(); var vertices=new Vector3[model.VertexCount];
        Check(model.VertexCount*38<65535,"colony exceeds 16-bit mesh indices");
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
        string destination=Environment.GetEnvironmentVariable("NARCISSUS_OUTPUT");
        if(!string.IsNullOrEmpty(destination))
        {
            Directory.CreateDirectory(destination);
            var obj=new StringBuilder("# Original procedural Narcissus. Mature mesh; growth is driven in Unity.\nmtllib Narcissus.mtl\n");
            foreach(var p in vertices) obj.AppendFormat(CultureInfo.InvariantCulture,"v {0:R} {1:R} {2:R}\n",p.x,p.y,p.z);
            foreach(var uv in model.UV) obj.AppendFormat(CultureInfo.InvariantCulture,"vt {0:R} {1:R}\n",uv.x,uv.y);
            int last=-1;
            for(int i=0;i<model.Triangles.Length;i+=3)
            {
                int part=model.Parts[model.Triangles[i]];
                if(part!=last) { obj.Append("g part_"+part+"\nusemtl part_"+part+"\n"); last=part; }
                int a=model.Triangles[i]+1,b=model.Triangles[i+1]+1,c=model.Triangles[i+2]+1;
                obj.Append($"f {a}/{a} {b}/{b} {c}/{c}\n");
            }
            File.WriteAllText(Path.Combine(destination,"Narcissus.obj"),obj.ToString());
            File.WriteAllText(Path.Combine(destination,"Narcissus.mtl"),"newmtl part_0\nKd 0.18 0.26 0.12\nNs 35\n\nnewmtl part_1\nKd 0.22 0.32 0.16\nNs 35\n\nnewmtl part_2\nKd 0.91 0.91 0.79\nNs 45\n\nnewmtl part_3\nKd 0.82 0.60 0.19\nNs 40\n");
            string preview=Environment.GetEnvironmentVariable("NARCISSUS_PREVIEW_DATA");
            if(!string.IsNullOrEmpty(preview)) File.WriteAllText(preview,JsonSerializer.Serialize(new {
                open=model.Open.Select(XYZ),closed=model.Closed.Select(XYZ),parts=model.Parts,triangles=model.Triangles,
                uv=model.UV.Select(p=>new[]{p.x,p.y}),colors=model.Colors.Select(c=>new[]{c.r,c.g,c.b,c.a}),frames=frames,
                mature=vertices.Select(XYZ)}));
        }
        Console.WriteLine($"PASS: {model.VertexCount} vertices / {nondegenerate} triangles; root birth, finite stages, 21-second completion, 38-flower index budget; actual mesh exported.");
    }
}
namespace UnityEngine
{
    public struct Vector2 {public float x,y;public Vector2(float x,float y){this.x=x;this.y=y;}}
    public struct Color {public float r,g,b,a;public Color(float r,float g,float b,float a){this.r=r;this.g=g;this.b=b;this.a=a;}}
    public struct Quaternion {public static Quaternion identity=>default;public static Vector3 operator*(Quaternion q,Vector3 p)=>p;}
    public struct Vector3
    {
        public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
        public static Vector3 zero=>new Vector3(0,0,0);public float magnitude=>(float)Math.Sqrt(x*x+y*y+z*z);
        public static Vector3 operator+(Vector3 a,Vector3 b)=>new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator-(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator*(Vector3 a,float b)=>new Vector3(a.x*b,a.y*b,a.z*b);
        public static Vector3 Lerp(Vector3 a,Vector3 b,float t)=>a+(b-a)*Mathf.Clamp01(t);
        public static Vector3 Cross(Vector3 a,Vector3 b)=>new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
    }
    public static class Mathf
    {
        public const float PI=(float)Math.PI;
        public static float Sin(float x)=>(float)Math.Sin(x);public static float Cos(float x)=>(float)Math.Cos(x);
        public static float Pow(float x,float y)=>(float)Math.Pow(x,y);public static float Abs(float x)=>Math.Abs(x);
        public static float Max(float x,float y)=>Math.Max(x,y);public static float Clamp01(float x)=>Math.Max(0,Math.Min(1,x));
        public static float Lerp(float a,float b,float t)=>a+(b-a)*Clamp01(t);
    }
}
