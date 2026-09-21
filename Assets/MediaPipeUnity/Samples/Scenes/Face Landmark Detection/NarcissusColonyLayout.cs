using System;
using UnityEngine;

/// <summary>Stable per-track variation and conservative crown spacing, independent of frame rate.
/// Crown envelopes include petals, folded buds, corona and breathing. Stems/leaves may weave.</summary>
public sealed class NarcissusColonyLayout
{
    public const int Count=38;
    public readonly float[] Sizes=new float[Count], Delays=new float[Count];
    public readonly Quaternion[] Rotations=new Quaternion[Count];
    public readonly Vector3[] Jitter=new Vector3[Count], Centers=new Vector3[Count];
    readonly int[] _order=new int[Count];
    readonly Vector3[] _localShifts=new Vector3[Count];
    bool _planned;
    public static float Noise(int seed,int index)
    {
        unchecked
        {
            uint n=(uint)seed ^ ((uint)index*747796405u+2891336453u);
            n=(n^(n>>16))*2246822519u;n=(n^(n>>13))*3266489917u;
            return ((n^(n>>16))&0xFFFFFFu)/16777216f;
        }
    }
    public void Reset(int seed)
    {
        _planned=false;
        for(int i=0;i<Count;i++) _order[i]=i;
        for(int i=Count-1;i>0;i--)
        { int j=(int)(Noise(seed,i)* (i+1)); int swap=_order[i];_order[i]=_order[j];_order[j]=swap; }
        for(int rank=0;rank<Count;rank++)
        {
            int i=_order[rank]; float n=Noise(seed,100+i);
            // Eight focal blooms, twelve medium blooms and eighteen small ones.
            Sizes[i]=rank<8?Mathf.Lerp(.105f,.135f,n):rank<20?Mathf.Lerp(.071f,.099f,n):Mathf.Lerp(.040f,.065f,n);
            Delays[i]=i<5?i*.65f:Mathf.Lerp(2.7f,11f,Noise(seed,200+i));
            Rotations[i]=Quaternion.Euler((Noise(seed,300+i)-.5f)*26,(Noise(seed,400+i)-.5f)*26,Noise(seed,500+i)*360);
            Jitter[i]=new Vector3((Noise(seed,600+i)-.5f)*.035f,(Noise(seed,700+i)-.5f)*.035f,Noise(seed,800+i)*.045f);
        }
        Delays[_order[Count-1]]=11f; // The final small bloom completes at growth second 21.
        Array.Sort(_order,(a,b)=>Sizes[b].CompareTo(Sizes[a]));
    }

    /// <summary>Place the current crown envelopes, not only the final blooms.
    /// Search nearby tangential positions, then add the minimum outward depth
    /// required to clear every already placed crown. No stochastic frame updates.</summary>
    public void Solve(Vector3[] desired,float[] radii,Quaternion faceRotation,float width)
    {
        Vector3 right=faceRotation*Vector3.right,up=faceRotation*Vector3.up,normal=faceRotation*Vector3.forward;
        for(int order=0;order<Count;order++)
        {
            int i=_order[order];
            if(radii[i]<width*.0001f) { Centers[i]=desired[i]; continue; }
            Vector3 best=desired[i],bestShift=Vector3.zero;float bestCost=float.PositiveInfinity;
            for(int candidate=0;candidate<(_planned?1:25);candidate++)
            {
                float ring=candidate==0?0:1+(candidate-1)/8;
                float angle=((candidate-1)%8)*Mathf.PI*.25f;
                Vector3 shift=_planned?faceRotation*(_localShifts[i]*width*Mathf.Clamp01(radii[i]/Mathf.Max(width*Sizes[i]*1.13f,.00001f))):
                    (right*Mathf.Cos(angle)+up*Mathf.Sin(angle))*(ring*width*.021f);
                Vector3 p=desired[i]+shift;
                float lift=0;
                for(int previous=0;previous<order;previous++)
                {
                    int j=_order[previous];if(radii[j]<width*.0001f)continue;
                    Vector3 delta=p-Centers[j];float dz=Vector3.Dot(delta,normal);
                    float lateral=Mathf.Max(0,delta.sqrMagnitude-dz*dz);
                    float distance=radii[i]+radii[j]+width*.004f;
                    if(lateral<distance*distance)
                        lift=Mathf.Max(lift,Mathf.Sqrt(distance*distance-lateral)-dz);
                }
                float cost=shift.sqrMagnitude+lift*lift*.55f;
                if(cost<bestCost) { bestCost=cost;best=p+normal*lift;bestShift=shift; }
            }
            Centers[i]=best;
            if(!_planned) _localShifts[i]=Quaternion.Inverse(faceRotation)*bestShift/Mathf.Max(width,.001f);
        }
        _planned=true;
    }
}
