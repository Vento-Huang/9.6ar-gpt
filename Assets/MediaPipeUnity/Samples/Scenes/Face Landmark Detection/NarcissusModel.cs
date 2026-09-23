using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Original procedural narcissus: six tepals, fluted corona, stem and two strap leaves.
/// Reusable topology; opening changes vertex positions rather than revealing a flat sticker.</summary>
public sealed class NarcissusModel
{
    public readonly Vector3[] Open, Closed;
    public readonly Vector2[] UV;
    public readonly Color[] Colors;
    public readonly int[] Parts, Triangles;
    public static readonly Vector3 FlowerBase = new Vector3(0, 0.25f, 1.2f);
    public static readonly Vector3 AttachedBase = new Vector3(0, 0.25f, 0.19f);
    public int VertexCount => Open.Length;
    readonly List<Vector3> _open = new List<Vector3>(), _closed = new List<Vector3>();
    readonly List<Vector2> _uv = new List<Vector2>();
    readonly List<Color> _colors = new List<Color>();
    readonly List<int> _parts = new List<int>(), _indices = new List<int>();

    public NarcissusModel()
    {
        // Material category travels in color alpha; the render shader outputs opaque coverage.
        Color stem = new Color(.18f,.26f,.12f,0), leaf = new Color(.22f,.32f,.16f,.33f);
        Color petal = new Color(.91f,.91f,.79f,.66f), cup = new Color(.82f,.60f,.19f,1);
        int start = _open.Count;
        for (int row=0; row<=7; row++)
        {
            float t=row/7f;
            Vector3 center=StemPoint(t);
            for (int side=0; side<=8; side++)
            {
                float a=side/8f*Mathf.PI*2, radius=Mathf.Lerp(.055f,.035f,t);
                Add(center+new Vector3(Mathf.Cos(a)*radius,Mathf.Sin(a)*radius,0),Vector3.zero,
                    new Vector2(side/8f,t),stem,0);
            }
        }
        Grid(start,8,9,false);
        for (int blade=0; blade<2; blade++)
        {
            start=_open.Count;
            float direction=blade==0?-1:1;
            for (int row=0; row<=8; row++)
            {
                float t=row/8f;
                Vector3 center=new Vector3(direction*(.15f*t+.92f*t*t),
                    (blade==0?1.2f:-.85f)*t, .15f*Mathf.Sin(t*Mathf.PI));
                for (int side=0; side<3; side++)
                {
                    float across=side-1;
                    float width=.12f*Mathf.Pow(Mathf.Max(0,Mathf.Sin(Mathf.PI*t)),.7f)+.001f;
                    Vector3 p=center+new Vector3(across*width,0,(1-Mathf.Abs(across))*.06f*Mathf.Sin(t*Mathf.PI));
                    Add(p,p*.025f,new Vector2(t,(across+1)*.5f),leaf,1);
                }
            }
            ThickGrid(start,9,3,.025f,blade==0);
        }
        for (int petalIndex=0; petalIndex<6; petalIndex++)
        {
            start=_open.Count;
            float a=petalIndex*Mathf.PI/3;
            Vector3 radial=new Vector3(Mathf.Cos(a),Mathf.Sin(a),0);
            Vector3 tangent=new Vector3(-radial.y,radial.x,0);
            for (int row=0; row<=12; row++)
            {
                float t=row/12f;
                for (int side=0; side<=6; side++)
                {
                    float across=side/3f-1;
                    float width=.44f*Mathf.Pow(Mathf.Max(0,Mathf.Sin(Mathf.PI*t)),.65f)+.004f;
                    Vector3 p=radial*(.12f+.92f*t)+tangent*(across*width);
                    p.z=.12f*Mathf.Sin(t*Mathf.PI)-.12f*t*t+.10f*across*across;
                    p.z+=.008f*Mathf.Sin(t*Mathf.PI)*Mathf.Cos(across*Mathf.PI*3);
                    p.z+=.012f*Mathf.Sin(t*Mathf.PI*2+petalIndex)*across;
                    Vector3 bud=radial*(.055f+.10f*Mathf.Sin(t*Mathf.PI))+tangent*(across*width*.14f);
                    bud.z=.88f*t;
                    Add(p,bud,new Vector2(t,(across+1)*.5f),petal,2);
                }
            }
            ThickGrid(start,13,7,.018f,false);
        }
        start=_open.Count;
        float[] radii={.15f,.19f,.235f,.275f,.30f,.303f,.28f,.245f,.19f};
        float[] heights={.015f,.10f,.23f,.34f,.405f,.42f,.40f,.30f,.10f};
        for (int row=0; row<radii.Length; row++)
        for (int side=0; side<=48; side++)
        {
            float a=side/48f*Mathf.PI*2;
            float flute=1+.055f*Mathf.Cos(12*a);
            Vector3 p=new Vector3(Mathf.Cos(a)*radii[row]*flute,Mathf.Sin(a)*radii[row]*flute,
                heights[row]+(.016f*Mathf.Sin(12*a)+.006f*Mathf.Sin(24*a))*Mathf.Pow(heights[row]/.42f,3));
            Add(p,new Vector3(p.x*.22f,p.y*.22f,p.z*.7f),new Vector2(side/48f,row/(radii.Length-1f)),cup,3);
        }
        Grid(start,radii.Length,49,true);
        // Close the throat with a small golden dome: the corona is a cup, not a hole through the flower.
        start=_open.Count;
        Add(new Vector3(0,0,.16f),new Vector3(0,0,.11f),new Vector2(.5f,.9f),cup,3);
        for(int side=0;side<=24;side++)
        {
            float a=side/24f*Mathf.PI*2;
            Vector3 p=new Vector3(.15f*Mathf.Cos(a),.15f*Mathf.Sin(a),.06f);
            Add(p,new Vector3(p.x*.22f,p.y*.22f,p.z*.7f),new Vector2(side/24f,.7f),cup,3);
            if(side>0) Triangle(start,start+side,start+side+1);
        }
        // Six stamens with paired pollen lobes and a central three-lobed stigma.
        // All stay inside the existing cup radius and height protection volume.
        Color filament=new Color(.92f,.72f,.30f,1),pollen=new Color(.95f,.68f,.13f,1);
        for(int stamen=0;stamen<6;stamen++)
        {
            float angle=stamen*Mathf.PI/3;
            Vector3 basePoint=new Vector3(Mathf.Cos(angle)*.065f,Mathf.Sin(angle)*.065f,.13f);
            Vector3 tip=new Vector3(Mathf.Cos(angle)*.105f,Mathf.Sin(angle)*.105f,.285f+(stamen%2)*.035f);
            Tube(basePoint,tip,.009f,filament);
            for(int lobe=0;lobe<2;lobe++)
                Ellipsoid(tip+new Vector3((lobe==0?-1:1)*.014f,0,0),new Vector3(.016f,.027f,.022f),pollen);
        }
        Tube(new Vector3(0,0,.13f),new Vector3(0,0,.35f),.012f,filament);
        for(int lobe=0;lobe<3;lobe++)
        {
            float a=lobe*Mathf.PI*2/3;
            Ellipsoid(new Vector3(Mathf.Cos(a)*.014f,Mathf.Sin(a)*.014f,.355f),new Vector3(.021f,.021f,.012f),filament);
        }
        Open=_open.ToArray(); Closed=_closed.ToArray(); UV=_uv.ToArray(); Colors=_colors.ToArray();
        Parts=_parts.ToArray(); Triangles=_indices.ToArray();
    }

    // Closed thin shell: front, back and welded perimeter. Not a double-sided plane.
    void ThickGrid(int first,int rows,int columns,float thickness,bool reverse)
    {
        int count=rows*columns,triStart=_indices.Count;
        Grid(first,rows,columns,reverse);
        int triEnd=_indices.Count,back=_open.Count;
        for(int i=0;i<count;i++)
        {
            int v=first+i;
            Add(_open[v]-new Vector3(0,0,thickness),_closed[v]-new Vector3(0,0,thickness*.18f),_uv[v],_colors[v],_parts[v]);
        }
        var edges=new Dictionary<long,int[]>();
        for(int t=triStart;t<triEnd;t+=3)
        {
            int a=_indices[t],b=_indices[t+1],c=_indices[t+2];
            Triangle(back+a-first,back+c-first,back+b-first);
            int[] vertices={a,b,c};
            for(int edge=0;edge<3;edge++)
            {
                int x=vertices[edge],y=vertices[(edge+1)%3];
                long key=((long)Math.Min(x,y)<<32)|(uint)Math.Max(x,y);
                if(edges.ContainsKey(key))edges.Remove(key);else edges[key]=new[]{x,y};
            }
        }
        foreach(var e in edges.Values)
        { int a=e[0],b=e[1],ab=back+a-first,bb=back+b-first;Triangle(a,ab,b);Triangle(b,ab,bb); }
    }
    void FlowerVertex(Vector3 p,Vector2 uv,Color color)
    { Add(p,new Vector3(p.x*.2f,p.y*.2f,p.z*.7f),uv,color,3); }
    void Tube(Vector3 a,Vector3 b,float radius,Color color)
    {
        int first=_open.Count;
        for(int row=0;row<2;row++)for(int side=0;side<8;side++)
        {
            float angle=side*Mathf.PI/4;
            FlowerVertex((row==0?a:b)+new Vector3(Mathf.Cos(angle)*radius,Mathf.Sin(angle)*radius,0),new Vector2(side/8f,.8f),color);
        }
        for(int side=0;side<8;side++)
        {int n=(side+1)%8;Triangle(first+side,first+n,first+8+side);Triangle(first+n,first+8+n,first+8+side);}
        int bottom=_open.Count;FlowerVertex(a,new Vector2(.5f,.8f),color);
        int top=_open.Count;FlowerVertex(b,new Vector2(.5f,.8f),color);
        for(int side=0;side<8;side++)
        {int n=(side+1)%8;Triangle(bottom,first+n,first+side);Triangle(top,first+8+side,first+8+n);}
    }
    void Ellipsoid(Vector3 center,Vector3 radius,Color color)
    {
        int first=_open.Count;
        FlowerVertex(center+new Vector3(0,0,radius.z),new Vector2(.5f,.8f),color);
        for(int row=1;row<4;row++)for(int side=0;side<8;side++)
        {
            float lat=row*Mathf.PI/4,a=side*Mathf.PI/4;
            FlowerVertex(center+new Vector3(Mathf.Sin(lat)*Mathf.Cos(a)*radius.x,Mathf.Sin(lat)*Mathf.Sin(a)*radius.y,Mathf.Cos(lat)*radius.z),new Vector2(side/8f,.8f),color);
        }
        int bottom=_open.Count;FlowerVertex(center-new Vector3(0,0,radius.z),new Vector2(.5f,.8f),color);
        for(int side=0;side<8;side++)
        {
            int n=(side+1)%8;Triangle(first,first+1+side,first+1+n);
            for(int row=0;row<2;row++)
            {int a=first+1+row*8+side,b=first+1+row*8+n;Triangle(a,a+8,b);Triangle(b,a+8,b+8);}
            Triangle(bottom,first+17+n,first+17+side);
        }
    }

    static Vector3 StemPoint(float t) => new Vector3(.065f*Mathf.Sin(t*Mathf.PI), .25f*t, 1.2f*t);
    void Add(Vector3 p,Vector3 bud,Vector2 uv,Color color,int part)
    { _open.Add(p); _closed.Add(bud); _uv.Add(uv); _colors.Add(color); _parts.Add(part); }
    void Grid(int first,int rows,int columns,bool reverse)
    {
        for (int row=0; row<rows-1; row++)
        for (int col=0; col<columns-1; col++)
        {
            int a=first+row*columns+col,b=a+columns,c=a+1,d=b+1;
            if (reverse) { Triangle(a,c,b); Triangle(b,c,d); }
            else { Triangle(a,b,c); Triangle(b,d,c); }
        }
    }
    void Triangle(int a,int b,int c) { _indices.Add(a); _indices.Add(b); _indices.Add(c); }
    public static float Ease(float seconds,float begin,float end)
    { float t=Mathf.Clamp01((seconds-begin)/Mathf.Max(.001f,end-begin)); return t*t*(3-2*t); }

    /// <summary>Latest delay is 11 seconds, so every flower completes exactly by 21.
    /// All vertices collapse to their root at/before their scheduled birth.</summary>
    public void Evaluate(float seconds,float delay,float breathingPhase,Vector3 root,
        Quaternion rotation,float scale,Vector3[] output,int offset,Vector3 headOffset=default,bool attached=false,Vector3 sway=default)
    {
        float stem=Ease(seconds,delay,delay+6);
        float leaves=Ease(seconds,delay+1,delay+8);
        float bud=Ease(seconds,delay+2,delay+6);
        float bloom=Ease(seconds,delay+4,delay+10);
        float breath=1+.009f*Mathf.Sin(breathingPhase)*bloom;
        for (int i=0;i<Open.Length;i++)
        {
            Vector3 p;
            if (Parts[i]==0) p=Open[i]*stem;
            else if (Parts[i]==1) p=Vector3.Lerp(Closed[i],Open[i],leaves)*stem;
            else p=FlowerBase*stem+Vector3.Lerp(Closed[i],Open[i],bloom)*(bud*breath);
            if (attached)
            {
                if (Parts[i]==0) p.z*=AttachedBase.z/FlowerBase.z;
                else if (Parts[i]==1) p.z*=.5f;
                else p.z-=(FlowerBase.z-AttachedBase.z)*stem;
            }
            // Bend the stem continuously toward its spaced crown; roots remain attached.
            float bend=Parts[i]==0?UV[i].y*UV[i].y:Parts[i]==1?UV[i].x*.08f:1f;
            float jointT=Parts[i]==0?UV[i].y:Parts[i]==1?UV[i].x*.12f:1f;
            float jointWeight=jointT<.5f?jointT*.7f:.35f+(jointT-.5f)*1.3f;
            output[offset+i]=root+rotation*(p*scale)+headOffset*bend+sway*jointWeight;
        }
    }
}
