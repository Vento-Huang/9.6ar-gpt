#if UNITY_EDITOR
using System;

/// <summary>Optional model-specific skin boundary for offline recording only.
/// Coordinates use the original model XY plane, before any turn or flattening.</summary>
public static class NarcissusRecordingSkinCoverage
{
    public static byte[] Build(int width,int height,float[] outline,float centerY,float halfHeight,float feather)
    {
        if(width<=0||height<=0||!Finite(centerY)||!Finite(halfHeight)||halfHeight<=0)
            throw new ArgumentException("Invalid recording skin coverage projection.");
        var pixels=new byte[checked(width*height)];
        // Missing outlines deliberately preserve all previous model recordings.
        if(outline==null||outline.Length==0){for(int i=0;i<pixels.Length;i++)pixels[i]=255;return pixels;}
        if(outline.Length<6||outline.Length%2!=0||!Finite(feather)||feather<=0)
            throw new ArgumentException("Recording skin outline needs at least three XY points and a positive feather.");
        double minX=double.MaxValue,minY=minX,maxX=double.MinValue,maxY=maxX,area=0;
        int count=outline.Length/2;
        for(int i=0,j=count-1;i<count;j=i++){
            double x=outline[i*2],y=outline[i*2+1];
            if(!Finite(outline[i*2])||!Finite(outline[i*2+1]))throw new ArgumentException("Recording skin outline contains a non-finite point.");
            minX=Math.Min(minX,x);maxX=Math.Max(maxX,x);minY=Math.Min(minY,y);maxY=Math.Max(maxY,y);
            area+=outline[j*2]*y-x*outline[j*2+1];
        }
        if(Math.Abs(area)<1e-12)throw new ArgumentException("Recording skin outline has no area.");
        double scale=2.0*halfHeight;
        int x0=(int)Math.Max(0,Math.Min(width,Math.Floor((minX/scale+.5)*width)));
        int x1=(int)Math.Max(0,Math.Min(width-1,Math.Ceiling((maxX/scale+.5)*width)));
        int y0=(int)Math.Max(0,Math.Min(height,Math.Floor(((minY-centerY)/scale+.5)*height)));
        int y1=(int)Math.Max(0,Math.Min(height-1,Math.Ceiling(((maxY-centerY)/scale+.5)*height)));
        for(int py=y0;py<=y1;py++)for(int px=x0;px<=x1;px++){
            double x=((px+.5)/width-.5)*scale,y=((py+.5)/height-.5)*scale+centerY;
            bool inside=false;double distance2=double.MaxValue;
            for(int i=0,j=count-1;i<count;j=i++){
                double ax=outline[j*2],ay=outline[j*2+1],bx=outline[i*2],by=outline[i*2+1];
                if((ay>y)!=(by>y)&&x<(bx-ax)*(y-ay)/(by-ay)+ax)inside=!inside;
                double dx=bx-ax,dy=by-ay,length2=dx*dx+dy*dy;
                double t=length2>0?Math.Max(0,Math.Min(1,((x-ax)*dx+(y-ay)*dy)/length2)):0;
                double ex=x-ax-t*dx,ey=y-ay-t*dy;
                distance2=Math.Min(distance2,ex*ex+ey*ey);
            }
            // Feather inward only: the skin effect never expands into hair.
            if(inside){double t=Math.Min(1,Math.Sqrt(distance2)/feather);pixels[py*width+px]=(byte)Math.Round(255*t*t*(3-2*t));}
        }
        return pixels;
    }
    static bool Finite(float value)=>!float.IsNaN(value)&&!float.IsInfinity(value);
}
#endif
