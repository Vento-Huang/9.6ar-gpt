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
    public int totalSeconds=13, revealSeconds=3, fps=30, rotationTurns=1;
    public Resolution resolution=Resolution.FullHD1080p;
    public Format format=Format.MP4_H264;
    public Quality quality=Quality.High;

    public int Width => resolution==Resolution.UHD4K?3840:resolution==Resolution.HD720p?1280:1920;
    public int Height => Width*9/16;
    public int ReverseSeconds => totalSeconds-revealSeconds;
    public double AverageSecondsPerTurn => (double)ReverseSeconds/rotationTurns;
    public int FrameCount => totalSeconds*fps;
    public int Crf => quality==Quality.Maximum?14:quality==Quality.Standard?22:18;
    public string Extension => format==Format.MOV_ProRes422HQ?".mov":".mp4";

    public void Normalize()
    {
        totalSeconds=Math.Max(2,Math.Min(3600,totalSeconds));
        revealSeconds=Math.Max(1,Math.Min(totalSeconds-1,revealSeconds));
        rotationTurns=Math.Max(1,Math.Min(360,rotationTurns));
        if(fps!=24&&fps!=25&&fps!=30&&fps!=60)fps=30;
        if(!Enum.IsDefined(typeof(Resolution),resolution))resolution=Resolution.FullHD1080p;
        if(!Enum.IsDefined(typeof(Format),format))format=Format.MP4_H264;
        if(!Enum.IsDefined(typeof(Quality),quality))quality=Quality.High;
    }
    // Sample both endpoints while retaining exactly totalSeconds * fps frames.
    public float FrameTime(int index) => (float)((double)totalSeconds*Math.Max(0,Math.Min(FrameCount-1,index))/(FrameCount-1));
    public float GrowthTime(float seconds) => (float)(21*(1-Math.Max(0,Math.Min(1,(double)seconds/ReverseSeconds))));
    public float RotationDegrees(float seconds)
    {
        double duration=ReverseSeconds, ease=Math.Min(1,duration*.1), linear=duration-ease;
        double t=Math.Max(0,Math.Min(duration,seconds));
        double travel=t<=linear?t:linear+(t-linear)-(t-linear)*(t-linear)/(2*ease);
        return (float)(360*rotationTurns*travel/(duration-ease*.5));
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
        "0-{8}s: {9} revolutions ({10:0.###} seconds per turn on average), reverse growth 21 to 0; {8}-{4}s: face reveal.\n",
        Width,Height,fps,FrameCount,totalSeconds,format,quality,seed,ReverseSeconds,rotationTurns,AverageSecondsPerTurn);
    static string Quote(string s)=>"\""+s.Replace("\\","\\\\").Replace("\"","\\\"")+"\"";
}
#endif
