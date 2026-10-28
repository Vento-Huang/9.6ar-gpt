#if UNITY_EDITOR
using System;
using System.Globalization;
using System.IO;

/// <summary>Export settings and timeline math, independent of Unity rendering.</summary>
[Serializable]
public sealed class NarcissusRecordingOptions
{
    public enum Resolution { HD720p, FullHD1080p, UHD4K }
    public enum Format { MP4_H264, MOV_ProRes422HQ, PNG_Sequence }
    public enum Quality { Standard, High, Maximum }
    public int totalSeconds=13, revealSeconds=3, fps=30;
    public float secondsPerTurn=10f;
    public float faceRevealStartSeconds=10f;
    public bool useExplicitRevealStart;
    public float rotationStopSeconds;
    public bool useCustomRotationStop;
    public Resolution resolution=Resolution.FullHD1080p;
    public Format format=Format.MP4_H264;
    public Quality quality=Quality.High;

    public int Width => resolution==Resolution.UHD4K?3840:resolution==Resolution.HD720p?1280:1920;
    public int Height => Width*9/16;
    public double ReverseSeconds => useExplicitRevealStart?(double)(decimal)faceRevealStartSeconds:totalSeconds-revealSeconds;
    // Use the decimal value entered in the float field (e.g. 7.3 rather than
    // 7.3000001907) to avoid adding an unwanted video frame at decimal periods.
    public double RotationPeriodSeconds => (double)(decimal)secondsPerTurn;
    public double DegreesPerSecond => 360.0/RotationPeriodSeconds;
    // Reveal keeps its existing timeline, independently of the new stop target.
    public double RevealStartSeconds => useExplicitRevealStart?(double)(decimal)faceRevealStartSeconds:Math.Max(1,TurnsAt(ReverseSeconds))*RotationPeriodSeconds;
    public double RevealEndSeconds => RevealStartSeconds+revealSeconds;
    // The flag preserves old serialized settings; editing the field opts in.
    public double RotationStopTargetSeconds => useCustomRotationStop?(double)(decimal)rotationStopSeconds:RevealStartSeconds;
    public int TotalRotationTurns => TurnsAt(RotationStopTargetSeconds);
    public double RotationStopSeconds => TotalRotationTurns*RotationPeriodSeconds;
    public double ExtraRotationSeconds => Math.Max(0,RotationStopSeconds-RotationStopTargetSeconds);
    public double ActualSeconds => useExplicitRevealStart?totalSeconds:Math.Max(RevealEndSeconds,RotationStopSeconds);
    public double FinalHoldSeconds => Math.Max(0,ActualSeconds-Math.Max(RevealEndSeconds,RotationStopSeconds));
    public string TimingError => useExplicitRevealStart&&RotationStopSeconds>totalSeconds+1e-7
        ?string.Format(CultureInfo.InvariantCulture,"Rotation finishes at {0:0.###} s, after the {1} s video ends. Choose an earlier stop target, a shorter turn period, or a longer total duration.",RotationStopSeconds,totalSeconds):null;
    public int FrameCount => (int)Math.Ceiling(ActualSeconds*fps-1e-7);
    public double EncodedDurationSeconds => FrameCount/(double)fps;
    public int Crf => quality==Quality.Maximum?14:quality==Quality.Standard?22:18;
    public string Extension => format==Format.MOV_ProRes422HQ?".mov":".mp4";

    public void Normalize()
    {
        totalSeconds=Math.Max(2,Math.Min(3600,totalSeconds));
        revealSeconds=Math.Max(1,Math.Min(totalSeconds-1,revealSeconds));
        if(float.IsNaN(faceRevealStartSeconds)||float.IsInfinity(faceRevealStartSeconds)){faceRevealStartSeconds=10;useExplicitRevealStart=false;}
        faceRevealStartSeconds=Math.Max(0,Math.Min(totalSeconds-revealSeconds,faceRevealStartSeconds));
        if(float.IsNaN(secondsPerTurn)||float.IsInfinity(secondsPerTurn)||secondsPerTurn<=0)secondsPerTurn=10f;
        secondsPerTurn=Math.Max(.1f,Math.Min(3600f,secondsPerTurn));
        if(float.IsNaN(rotationStopSeconds)||float.IsInfinity(rotationStopSeconds)){rotationStopSeconds=0;useCustomRotationStop=false;}
        rotationStopSeconds=Math.Max(0,Math.Min(7200f,rotationStopSeconds));
        if(fps!=24&&fps!=25&&fps!=30&&fps!=60)fps=30;
        if(!Enum.IsDefined(typeof(Resolution),resolution))resolution=Resolution.FullHD1080p;
        if(!Enum.IsDefined(typeof(Format),format))format=Format.MP4_H264;
        if(!Enum.IsDefined(typeof(Quality),quality))quality=Quality.High;
    }
    // Keep the true encoded frame cadence so different clip lengths have the
    // same angular speed. Reserve the final reveal frame for the completed face.
    public float FrameTime(int index) => index>=FrameCount-1?(float)ActualSeconds:(float)(Math.Max(0,index)/(double)fps);
    public float GrowthTime(float seconds) => ReverseSeconds<=0?0:(float)(21*(1-Math.Max(0,Math.Min(1,(double)seconds/ReverseSeconds))));
    int TurnsAt(double seconds) => Math.Max(0,(int)Math.Ceiling(seconds/RotationPeriodSeconds-1e-9));
    // The main rotation speed depends only on secondsPerTurn, never film length.
    public double RotationDegrees(double seconds) => Math.Max(0,Math.Min(RotationStopSeconds,seconds))*DegreesPerSecond;
    public float DisplayRotationDegrees(float seconds)
    {
        // Finish the current revolution in its original direction and speed.
        // Only stop once a full turn has naturally reached the front.
        return seconds>=(float)RotationStopSeconds?0f:(float)(RotationDegrees(seconds)%360.0);
    }
    public string EncoderArguments(string folder,string video)
    {
        if(format==Format.PNG_Sequence)throw new InvalidOperationException("PNG sequences do not use a video encoder.");
        string codec=format==Format.MOV_ProRes422HQ
            ?"-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le -vendor apl0"
            :"-c:v libx264 -preset medium -crf "+Crf+" -pix_fmt yuv420p -movflags +faststart";
        return "-y -nostdin -framerate "+fps+" -start_number 0 -i "+Quote(Path.Combine(folder,"frame_%05d.png"))+
            " -frames:v "+FrameCount+" -an "+codec+" "+Quote(video);
    }
    public string Description(int seed) => string.Format(CultureInfo.InvariantCulture,
        "Narcissus: {0}x{1}, {2} fps, {3} frames, target {4} seconds; actual timeline {12:0.######} seconds; encoded {14:0.######} seconds.\nFormat: {5}; MP4 quality: {6}.\n"+
        "Original Unity colony generator, 38 landmark anchors.\nLayout seed: {7}\n"+
        "0-{8}s: reverse growth 21 to 0. Constant rotation at {9:0.###} seconds per turn ({10:0.###} degrees/second).\n"+
        "Rotation stop requested at {15:0.######}s; continue at the same speed until {16:0.######}s to complete {11} full turns.\n"+
        "{13:0.######}-{17:0.######}s: reveal the face independently of rotation; hold the revealed face afterwards. No forced alignment or reversal.\n"+
        "Final still face hold: {18:0.######} seconds. Timeline mode: {19}.\n",
        Width,Height,fps,FrameCount,totalSeconds,format,quality,seed,ReverseSeconds,secondsPerTurn,DegreesPerSecond,TotalRotationTurns,ActualSeconds,RevealStartSeconds,EncodedDurationSeconds,RotationStopTargetSeconds,RotationStopSeconds,RevealEndSeconds,FinalHoldSeconds,useExplicitRevealStart?"explicit reveal start, fixed total duration":"legacy automatic reveal start");
    static string Quote(string s)=>"\""+s.Replace("\\","\\\\").Replace("\"","\\\"")+"\"";
}

/// <summary>Editor preview transport; never changes the offline export clock.</summary>
public sealed class NarcissusRecordingPreviewClock
{
    public double Seconds {get;private set;}
    public bool IsPlaying {get;private set;}
    double lastClock;
    public void Pause(){IsPlaying=false;}
    public void Seek(double seconds,double duration){Pause();Seconds=Math.Max(0,Math.Min(duration,seconds));}
    public void Play(double now,double duration,bool fromStart=false)
    {
        if(fromStart||Seconds>=duration)Seconds=0;
        lastClock=now;IsPlaying=duration>0;
    }
    public bool Advance(double now,double duration)
    {
        if(!IsPlaying)return false;
        double elapsed=Math.Max(0,now-lastClock);lastClock=now;
        double next=Math.Min(duration,Seconds+elapsed);
        bool changed=next!=Seconds;Seconds=next;
        if(Seconds>=duration)Pause();
        return changed;
    }
}
#endif
