using System;
using System.IO;
using System.Text.Json;

// Compile with UNITY_EDITOR and NarcissusRecordingOptions.cs, without Unity stubs.
static class VerifyRecordingOptions
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static bool Near(float a,float b)=>Math.Abs(a-b)<.0002f;
    static void Main(string[] args)
    {
        var o=new NarcissusRecordingOptions();
        foreach(int duration in new[]{2,13,100,103,3600})foreach(int fps in new[]{24,25,30,60}){
            o.totalSeconds=duration;o.revealSeconds=3;o.fps=fps;o.Normalize();
            Check(o.FrameCount==duration*fps,"Exact encoded duration");
            Check(o.ReverseSeconds==duration-Math.Min(3,duration-1),"Face reveal included in total");
            Check(Near(o.FrameTime(0),0)&&Near(o.FrameTime(o.FrameCount-1),duration),"Both endpoints included");
            Check(Near(o.GrowthTime(0),21)&&Near(o.GrowthTime(o.ReverseSeconds),0),"Full growth to zero");
            Check(Near(o.RotationDegrees(0),0)&&Near(o.RotationDegrees(o.ReverseSeconds),360)&&Near(o.RotationDegrees(duration),360),"One rotation, then hold front");
            Check(Near(o.GrowthTime(duration),0),"No flowers during final reveal");
            float lastGrowth=21,lastRotation=0,lastTime=-1;
            for(int i=0;i<=100;i++){
                float t=duration*i/100f,g=o.GrowthTime(t),r=o.RotationDegrees(t);
                Check(g<=lastGrowth&&g>=0&&g<=21,"Growth monotonic and bounded");
                Check(r>=lastRotation&&r>=0&&r<=360,"Rotation monotonic and bounded");
                lastGrowth=g;lastRotation=r;
            }
            for(int frame=0;frame<o.FrameCount;frame++){
                float t=o.FrameTime(frame);Check(t>lastTime,"Frame time strictly increases");lastTime=t;
            }
        }
        o=new NarcissusRecordingOptions();
        for(int i=0;i<=130;i++){
            float t=Math.Min(i/10f,10),travel=t<=9?t:9+(t-9)-.5f*(t-9)*(t-9);
            Check(Near(o.RotationDegrees(i/10f),360*travel/9.5f),"Default 13-second motion unchanged");
        }
        foreach(NarcissusRecordingOptions.Resolution size in Enum.GetValues(typeof(NarcissusRecordingOptions.Resolution))){
            o.resolution=size;Check(o.Width%2==0&&o.Height%2==0&&o.Width*9==o.Height*16,"Encoder-compatible 16:9 size");
        }
        o.resolution=NarcissusRecordingOptions.Resolution.HD720p;Check(o.Width==1280&&o.Height==720,"720p dimensions");
        o.resolution=NarcissusRecordingOptions.Resolution.FullHD1080p;Check(o.Width==1920&&o.Height==1080,"1080p dimensions");
        o.resolution=NarcissusRecordingOptions.Resolution.UHD4K;Check(o.Width==3840&&o.Height==2160,"4K dimensions");
        o.totalSeconds=-1;o.revealSeconds=999;o.fps=0;o.Normalize();
        Check(o.totalSeconds==2&&o.revealSeconds==1&&o.fps==30,"Invalid settings repaired");
        o.totalSeconds=99999;o.Normalize();Check(o.totalSeconds==3600,"Long duration bounded");
        o=new NarcissusRecordingOptions{totalSeconds=100,revealSeconds=3};
        Check(o.FrameCount==3000&&o.ReverseSeconds==97,"Requested 100-second film");
        o.totalSeconds=103;Check(o.ReverseSeconds==100,"100 seconds plus reveal");
        foreach(NarcissusRecordingOptions.Quality q in Enum.GetValues(typeof(NarcissusRecordingOptions.Quality))){
            o.quality=q;Check(o.EncoderArguments("/tmp/a folder","/tmp/a film.mp4").Contains("-crf "+o.Crf),"Selected MP4 quality");
        }
        o.format=NarcissusRecordingOptions.Format.MOV_ProRes422HQ;
        Check(o.Extension==".mov"&&o.EncoderArguments("/tmp/frames","/tmp/test.mov").Contains("-profile:v 3 -pix_fmt yuv422p10le"),"ProRes HQ encoding");
        o.format=NarcissusRecordingOptions.Format.PNG_Sequence;
        bool rejected=false;try{o.EncoderArguments("/tmp/frames","unused");}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"PNG has no video encoding stage");
        if(args.Length>0){
            // Emit the actual production command for independent ffmpeg/ffprobe tests.
            var examples=new System.Collections.Generic.List<object>();
            foreach(var format in new[]{NarcissusRecordingOptions.Format.MP4_H264,NarcissusRecordingOptions.Format.MOV_ProRes422HQ}){
                o=new NarcissusRecordingOptions{totalSeconds=2,revealSeconds=1,fps=format==NarcissusRecordingOptions.Format.MP4_H264?25:30,format=format};
                string video=Path.Combine(args[0],"test"+o.Extension);
                examples.Add(new{format=format.ToString(),video,arguments=o.EncoderArguments(args[0],video),fps=o.fps,frames=o.FrameCount});
            }
            File.WriteAllText(Path.Combine(args[0],"encoder-tests.json"),JsonSerializer.Serialize(examples));
        }
        Console.WriteLine("PASS: "+checks+" recording timeline/settings checks; no Unity GPU rendering performed.");
    }
}
