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
    public Resolution resolution=Resolution.FullHD1080p;
    public Format format=Format.MP4_H264;
    public Quality quality=Quality.High;

    public int Width => resolution==Resolution.UHD4K?3840:resolution==Resolution.HD720p?1280:1920;
    public int Height => Width*9/16;
    public int ReverseSeconds => totalSeconds-revealSeconds;
    public double DegreesPerSecond => 360.0/secondsPerTurn;
    public double TotalRotationTurns => ReverseSeconds/(double)secondsPerTurn;
    public double FaceAlignmentSeconds => Math.Min(1.0,revealSeconds);
    public int FrameCount => totalSeconds*fps;
    public int Crf => quality==Quality.Maximum?14:quality==Quality.Standard?22:18;
    public string Extension => format==Format.MOV_ProRes422HQ?".mov":".mp4";

    public void Normalize()
    {
        totalSeconds=Math.Max(2,Math.Min(3600,totalSeconds));
        revealSeconds=Math.Max(1,Math.Min(totalSeconds-1,revealSeconds));
        if(float.IsNaN(secondsPerTurn)||float.IsInfinity(secondsPerTurn)||secondsPerTurn<=0)secondsPerTurn=10f;
        secondsPerTurn=Math.Max(.1f,Math.Min(3600f,secondsPerTurn));
        if(fps!=24&&fps!=25&&fps!=30&&fps!=60)fps=30;
        if(!Enum.IsDefined(typeof(Resolution),resolution))resolution=Resolution.FullHD1080p;
        if(!Enum.IsDefined(typeof(Format),format))format=Format.MP4_H264;
        if(!Enum.IsDefined(typeof(Quality),quality))quality=Quality.High;
    }
    // Keep the true encoded frame cadence so different clip lengths have the
    // same angular speed. Reserve the final reveal frame for the completed face.
    public float FrameTime(int index) => index>=FrameCount-1?totalSeconds:(float)(Math.Max(0,index)/(double)fps);
    public float GrowthTime(float seconds) => (float)(21*(1-Math.Max(0,Math.Min(1,(double)seconds/ReverseSeconds))));
    // The main rotation speed depends only on secondsPerTurn, never film length.
    public double RotationDegrees(double seconds) => Math.Max(0,Math.Min(ReverseSeconds,seconds))*DegreesPerSecond;
    public float DisplayRotationDegrees(float seconds)
    {
        double t=Math.Max(0,Math.Min(totalSeconds,seconds)),angle=RotationDegrees(t)%360.0;
        if(t<=ReverseSeconds)return (float)angle;
        // Arbitrary clip lengths end at arbitrary orientations. Return by the
        // shortest arc during the first second of the existing reveal phase.
        double remaining=(angle+180.0)%360.0-180.0;
        double u=Math.Min(1.0,(t-ReverseSeconds)/FaceAlignmentSeconds);
        return (float)(remaining*(1-u*u*(3-2*u)));
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
        "Narcissus: {0}x{1}, {2} fps, {3} frames, {4} seconds.\nFormat: {5}; MP4 quality: {6}.\n"+
        "Original Unity colony generator, 38 landmark anchors.\nLayout seed: {7}\n"+
        "0-{8}s: constant rotation at {9:0.###} seconds per turn ({10:0.###} degrees/second, {11:0.###} turns), reverse growth 21 to 0.\n"+
        "{8}-{4}s: face reveal; align to front during the first {12:0.###} second(s), then hold front.\n",
        Width,Height,fps,FrameCount,totalSeconds,format,quality,seed,ReverseSeconds,secondsPerTurn,DegreesPerSecond,TotalRotationTurns,FaceAlignmentSeconds);
    static string Quote(string s)=>"\""+s.Replace("\\","\\\\").Replace("\"","\\\"")+"\"";
}
#endif
