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
    static NarcissusRecordingOptions Options(int target,float period,int reveal=3,int fps=30)
    {
        var o=new NarcissusRecordingOptions{totalSeconds=target,secondsPerTurn=period,revealSeconds=reveal,fps=fps};
        o.Normalize();return o;
    }
    static void Main(string[] args)
    {
        var o=Options(100,10);
        Check(o.ReverseSeconds==97,"Flower disappearance stays at target minus reveal time");
        Check(o.TotalRotationTurns==10&&Near(o.RevealStartSeconds,100),"Finish tenth turn before revealing");
        Check(Near(o.ActualSeconds,103)&&o.FrameCount==3090,"100-second target naturally exports 103 seconds");
        Check(OrientationDistance(o.DisplayRotationDegrees(97),252)<.0001,"No forced alignment at original reveal time");
        Check(OrientationDistance(o.DisplayRotationDegrees(98),288)<.0001,"Continue forward at original speed");
        Check(OrientationDistance(o.DisplayRotationDegrees(99),324)<.0001,"Still completing the same turn");
        Check(Near(o.GrowthTime(98),0),"Blurred face, without flowers, while finishing turn");
        Check(Near(o.DisplayRotationDegrees(100),0)&&Near(o.DisplayRotationDegrees(103),0),"Only stop after naturally reaching front");
        o=Options(83,10);Check(Near(o.RevealStartSeconds,80)&&Near(o.ActualSeconds,83)&&Near(o.ExtraRotationSeconds,0),"Already complete turn adds no time or extra circle");
        o=Options(60,10);Check(Near(o.ActualSeconds,63),"60-second target extends to the next front-facing turn");
        o=Options(5,10);Check(Near(o.RevealStartSeconds,10)&&Near(o.ActualSeconds,13),"Short clip still completes one full turn");
        o=Options(100,7.3f);Check(o.TotalRotationTurns==14&&Near(o.ActualSeconds,105.2)&&o.FrameCount==3156,"Decimal period avoids an unwanted extra frame");
        o=Options(100,7.37f);Check(Near(o.ActualSeconds,106.18)&&o.FrameCount==3186&&Near(o.EncodedDurationSeconds,106.2),"Non-frame-aligned endpoint rounds up to a full encoded frame");
        o=Options(100,37.5f);Check(o.TotalRotationTurns==3&&Near(o.ActualSeconds,115.5),"Fractional period finishes the current full turn");
        o=Options(8,10,1);Check(Near(o.RevealStartSeconds,10)&&Near(o.ActualSeconds,11),"One-second reveal waits for completion too");

        var baseline=Options(3600,10);
        foreach(int target in new[]{2,13,60,83,100,103,3600})foreach(float period in new[]{.1f,5f,7.3f,10f,20f,37.5f,3600f})foreach(int fps in new[]{24,25,30,60}){
            o=Options(target,period,3,fps);baseline.secondsPerTurn=period;
            Check(o.RevealStartSeconds>=o.ReverseSeconds-1e-7,"Face reveal never starts early");
            Check(o.ExtraRotationSeconds<o.RotationPeriodSeconds+1e-7,"Wait less than one additional turn");
            Check(o.RevealStartSeconds-o.RotationPeriodSeconds<o.ReverseSeconds+1e-7,"Finish current turn, not an unnecessary later one");
            Check(Near(o.RotationDegrees(o.RevealStartSeconds),o.TotalRotationTurns*360.0),"Stop angle is a complete revolution");
            Check(Near(o.ActualSeconds-o.RevealStartSeconds,o.revealSeconds),"Preserve full selected reveal duration");
            Check(o.EncodedDurationSeconds>=o.ActualSeconds-1e-7&&o.EncodedDurationSeconds-o.ActualSeconds<1.0/fps+1e-7,"Encoded duration differs by less than one frame");
            Check(Near(o.FrameTime(0),0)&&Near(o.FrameTime(o.FrameCount-1),o.ActualSeconds,.001),"First and last frames retain both endpoints");
            Check(Near(o.GrowthTime(0),21)&&Near(o.GrowthTime(o.ReverseSeconds),0),"Original reverse growth endpoint unchanged");
            foreach(double fraction in new[]{0.0,.25,.5,.9,.99}){
                double t=o.RevealStartSeconds*fraction;
                Check(Near(o.RotationDegrees(t),baseline.RotationDegrees(t),.001),"Different interview lengths have the same rotation speed");
                Check(Near(o.RotationDegrees(t+.00001)-o.RotationDegrees(t),o.DegreesPerSecond*.00001,.001),"No acceleration, slowdown or reversal before completing last turn");
                Check(OrientationDistance(o.DisplayRotationDegrees((float)t),o.RotationDegrees((float)t)%360)<.0001,"Displayed orientation matches forward rotation");
            }
            Check(Near(o.DisplayRotationDegrees((float)o.RevealStartSeconds),0)&&Near(o.DisplayRotationDegrees((float)o.ActualSeconds),0),"Hold front for entire reveal");
            double epsilon=Math.Min(.00001,o.RotationPeriodSeconds/100000);
            Check(OrientationDistance(o.DisplayRotationDegrees((float)(o.RevealStartSeconds-epsilon)),0)<.1,"Natural final front arrival has no alignment jump");
            for(int frame=0;frame<Math.Min(o.FrameCount-1,1000);frame++){
                float t=o.FrameTime(frame);Check(Near(t,frame/(double)fps),"True encoded frame cadence independent of clip length");
            }
            Check(o.Description(1).Contains("No forced alignment or reversal"),"Export metadata records completed-turn rule");
        }
        foreach(NarcissusRecordingOptions.Resolution size in Enum.GetValues(typeof(NarcissusRecordingOptions.Resolution))){
            o.resolution=size;Check(o.Width%2==0&&o.Height%2==0&&o.Width*9==o.Height*16,"Encoder-compatible 16:9 size");
        }
        o.resolution=NarcissusRecordingOptions.Resolution.HD720p;Check(o.Width==1280&&o.Height==720,"720p dimensions");
        o.resolution=NarcissusRecordingOptions.Resolution.FullHD1080p;Check(o.Width==1920&&o.Height==1080,"1080p dimensions");
        o.resolution=NarcissusRecordingOptions.Resolution.UHD4K;Check(o.Width==3840&&o.Height==2160,"4K dimensions");
        o.totalSeconds=-1;o.revealSeconds=999;o.fps=0;o.Normalize();
        Check(o.totalSeconds==2&&o.revealSeconds==1&&o.fps==30,"Invalid settings repaired");
        o.totalSeconds=99999;o.Normalize();Check(o.totalSeconds==3600,"Target duration bounded");
        foreach(float invalid in new[]{0f,-1f,float.NaN,float.PositiveInfinity,float.NegativeInfinity}){
            o.secondsPerTurn=invalid;o.Normalize();Check(o.secondsPerTurn==10,"Invalid rotation period repaired");
        }
        o.secondsPerTurn=.001f;o.Normalize();Check(o.secondsPerTurn==.1f,"Minimum period bounded");
        o.secondsPerTurn=99999;o.Normalize();Check(o.secondsPerTurn==3600,"Maximum period bounded");
        o=Options(100,10);
        foreach(NarcissusRecordingOptions.Quality q in Enum.GetValues(typeof(NarcissusRecordingOptions.Quality))){
            o.quality=q;Check(o.EncoderArguments("/tmp/a folder","/tmp/a film.mp4").Contains("-crf "+o.Crf),"Selected MP4 quality");
            Check(o.EncoderArguments("/tmp/a folder","/tmp/a film.mp4").Contains("-frames:v 3090"),"Encoder includes the complete turn and full reveal");
        }
        o.format=NarcissusRecordingOptions.Format.MOV_ProRes422HQ;
        Check(o.Extension==".mov"&&o.EncoderArguments("/tmp/frames","/tmp/test.mov").Contains("-profile:v 3 -pix_fmt yuv422p10le"),"ProRes HQ encoding");
        o.format=NarcissusRecordingOptions.Format.PNG_Sequence;
        bool rejected=false;try{o.EncoderArguments("/tmp/frames","unused");}catch(InvalidOperationException){rejected=true;}
        Check(rejected,"PNG has no video encoding stage");
        if(args.Length>0){
            var examples=new System.Collections.Generic.List<object>();
            foreach(var format in new[]{NarcissusRecordingOptions.Format.MP4_H264,NarcissusRecordingOptions.Format.MOV_ProRes422HQ}){
                o=Options(2,1,1,format==NarcissusRecordingOptions.Format.MP4_H264?25:30);o.format=format;
                string video=Path.Combine(args[0],"test"+o.Extension);
                examples.Add(new{format=format.ToString(),video,arguments=o.EncoderArguments(args[0],video),fps=o.fps,frames=o.FrameCount});
            }
            File.WriteAllText(Path.Combine(args[0],"encoder-tests.json"),JsonSerializer.Serialize(examples));
        }
        Console.WriteLine("PASS: "+checks+" completed-turn recording checks; no Unity GPU rendering performed.");
    }
}
