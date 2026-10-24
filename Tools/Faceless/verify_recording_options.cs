using System;
using System.IO;
using System.Text.Json;

// Compile with UNITY_EDITOR and NarcissusRecordingOptions.cs, without Unity stubs.
static class VerifyRecordingOptions
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static bool Near(double a,double b,double tolerance=.0002)=>Math.Abs(a-b)<tolerance;
    static double OrientationDistance(double a,double b)=>Math.Abs((a-b+540)%360-180);
    static void Main(string[] args)
    {
        var o=new NarcissusRecordingOptions();
        foreach(int duration in new[]{2,13,60,83,100,103,3600})foreach(int fps in new[]{24,25,30,60}){
            o.totalSeconds=duration;o.revealSeconds=3;o.fps=fps;o.Normalize();
            Check(o.FrameCount==duration*fps,"Exact encoded duration");
            Check(o.ReverseSeconds==duration-Math.Min(3,duration-1),"Face reveal included in total");
            Check(Near(o.FrameTime(0),0)&&Near(o.FrameTime(o.FrameCount-1),duration),"Both endpoints included");
            Check(Near(o.GrowthTime(0),21)&&Near(o.GrowthTime(o.ReverseSeconds),0),"Full growth to zero");
            Check(Near(o.RotationDegrees(0),0)&&Near(o.RotationDegrees(o.ReverseSeconds),o.ReverseSeconds*36.0),"Default constant 36 degrees per second");
            Check(Near(o.DisplayRotationDegrees(duration),0),"Final face points front");
            Check(Near(o.GrowthTime(duration),0),"No flowers during final reveal");
            float lastGrowth=21,lastTime=-1;double lastRotation=0;
            for(int i=0;i<=100;i++){
                float t=duration*i/100f,g=o.GrowthTime(t);double r=o.RotationDegrees(t);
                Check(g<=lastGrowth&&g>=0&&g<=21,"Growth monotonic and bounded");
                Check(r>=lastRotation&&r>=0&&r<=o.ReverseSeconds*36.0,"Main rotation monotonic and bounded");
                lastGrowth=g;lastRotation=r;
            }
            for(int frame=0;frame<o.FrameCount;frame++){
                float t=o.FrameTime(frame);Check(t>lastTime,"Frame time strictly increases");
                if(frame<o.FrameCount-1)Check(Near(t,frame/(double)fps),"True encoded frame cadence, independent of duration");
                lastTime=t;
            }
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
        var baseline=new NarcissusRecordingOptions{totalSeconds=3600,revealSeconds=3};
        foreach(float period in new[]{.1f,5f,10f,20f,37.5f,3600f})foreach(int duration in new[]{13,60,83,100,103}){
            o=new NarcissusRecordingOptions{totalSeconds=duration,revealSeconds=3,secondsPerTurn=period};o.Normalize();
            baseline.secondsPerTurn=period;
            Check(Near(o.DegreesPerSecond,360.0/period),"Configured angular speed");
            Check(Near(o.TotalRotationTurns,o.ReverseSeconds/(double)period),"Fractional turns allowed");
            foreach(double t in new[]{0.0,.5,1.0,5.0,9.0}){
                Check(Near(o.RotationDegrees(t),baseline.RotationDegrees(t)),"Different interview lengths have identical orientation at equal times");
                Check(Near(o.RotationDegrees(t+.01)-o.RotationDegrees(t),o.DegreesPerSecond*.01),"Constant main angular velocity");
            }
            for(int frame=0;frame<o.ReverseSeconds*o.fps;frame++){
                float t=o.FrameTime(frame);
                Check(OrientationDistance(o.DisplayRotationDegrees(t),o.RotationDegrees(t)%360)<.0001,"Displayed main orientation follows configured speed");
                Check(Near(o.RotationDegrees(t),baseline.RotationDegrees(o.FrameTime(frame))),"Encoded clips share main rotation speed");
                Check(o.GrowthTime(t)==new NarcissusRecordingOptions{totalSeconds=duration,revealSeconds=3}.GrowthTime(t),"Speed does not alter flower timing");
            }
            double previous=OrientationDistance(o.DisplayRotationDegrees(o.ReverseSeconds),0);
            for(int i=1;i<=100;i++){
                float t=o.ReverseSeconds+i/100f;
                double distance=OrientationDistance(o.DisplayRotationDegrees(t),0);
                Check(distance<=previous+.0001,"Reveal alignment follows shortest arc monotonically");previous=distance;
            }
            Check(Near(o.DisplayRotationDegrees(o.ReverseSeconds+1),0)&&Near(o.DisplayRotationDegrees(duration),0),"Front reached after one reveal second and held until end");
            float boundary=o.ReverseSeconds;
            Check(OrientationDistance(o.DisplayRotationDegrees(boundary-.0001f),o.DisplayRotationDegrees(boundary+.0001f))<.5,"No orientation jump at reveal boundary");
            Check(o.FrameCount==duration*30,"Speed does not change frame count");
            Check(o.Description(1).Contains("constant rotation"),"Export metadata records speed mode");
        }
        o=new NarcissusRecordingOptions{totalSeconds=100,revealSeconds=3,secondsPerTurn=10};
        Check(Near(o.TotalRotationTurns,9.7)&&Near(o.RotationDegrees(97),3492),"100-second clip rotates 9.7 turns before reveal");
        Check(OrientationDistance(o.DisplayRotationDegrees(97),252)<.0001,"Fractional last turn is not snapped to front");
        o.secondsPerTurn=20;Check(Near(o.DegreesPerSecond,18),"20 seconds per turn halves speed");
        o.secondsPerTurn=5;Check(Near(o.DegreesPerSecond,72),"5 seconds per turn doubles speed");
        foreach(float invalid in new[]{0f,-1f,float.NaN,float.PositiveInfinity,float.NegativeInfinity}){
            o.secondsPerTurn=invalid;o.Normalize();Check(o.secondsPerTurn==10,"Invalid rotation period repaired");
        }
        o.secondsPerTurn=.001f;o.Normalize();Check(o.secondsPerTurn==.1f,"Minimum period bounded");
        o.secondsPerTurn=99999;o.Normalize();Check(o.secondsPerTurn==3600,"Maximum period bounded");
        o=new NarcissusRecordingOptions{totalSeconds=8,revealSeconds=1,secondsPerTurn=10};
        Check(OrientationDistance(o.DisplayRotationDegrees(7),252)<.0001&&Near(o.DisplayRotationDegrees(8),0),"One-second reveal finishes alignment at exact endpoint");
        o.totalSeconds=103;o.revealSeconds=3;Check(o.ReverseSeconds==100,"100 seconds plus reveal");
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
