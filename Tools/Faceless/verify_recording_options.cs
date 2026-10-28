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

        o=Options(100,10);
        double originalRevealStart=o.RevealStartSeconds,originalRevealEnd=o.RevealEndSeconds;
        o.rotationStopSeconds=105;o.useCustomRotationStop=true;o.Normalize();
        Check(Near(o.RevealStartSeconds,100)&&Near(o.RevealEndSeconds,103),"Custom stop preserves original face-reveal interval");
        Check(o.TotalRotationTurns==11&&Near(o.RotationStopSeconds,110)&&Near(o.ExtraRotationSeconds,5),"Stop at 105 completes current turn at 110");
        Check(Near(o.ActualSeconds,110)&&o.FrameCount==3300,"Export includes rotation after reveal finishes");
        Check(OrientationDistance(o.DisplayRotationDegrees(101),36)<.0001&&OrientationDistance(o.DisplayRotationDegrees(102),72)<.0001,"Face keeps rotating throughout reveal");
        Check(OrientationDistance(o.DisplayRotationDegrees(103),108)<.0001&&OrientationDistance(o.DisplayRotationDegrees(109),324)<.0001,"Fully revealed face continues rotating until last turn ends");
        Check(Near(o.DisplayRotationDegrees(110),0)&&Near(o.DisplayRotationDegrees(115),0),"Stop and hold front only after completing selected turn");
        Check(Near(o.GrowthTime(98),0),"Later stop does not regrow flowers");
        foreach(float stop in new[]{0f,90f,100f,102f,105f,112f,300f,7200f}){
            o.rotationStopSeconds=stop;o.Normalize();
            Check(Near(o.RevealStartSeconds,originalRevealStart)&&Near(o.RevealEndSeconds,originalRevealEnd),"Changing stop never shifts or stretches face reveal");
            Check(o.RotationStopSeconds>=stop-1e-7&&o.RotationStopSeconds-stop<o.RotationPeriodSeconds+1e-7,"New stop finishes only the current turn");
            Check(Near(o.RotationDegrees(o.RotationStopSeconds),o.TotalRotationTurns*360),"Custom stop ends on a full revolution");
            Check(Near(o.ActualSeconds,Math.Max(originalRevealEnd,o.RotationStopSeconds)),"Export covers both reveal and stop");
            Check(o.Description(1).Contains("reveal the face independently of rotation"),"Metadata describes independent timelines");
        }
        o.rotationStopSeconds=100;Check(Near(o.RotationStopSeconds,100)&&o.FrameCount==3090,"Stop already at front adds no extra turn");
        o.rotationStopSeconds=90;Check(Near(o.DisplayRotationDegrees(95),0)&&Near(o.ActualSeconds,103),"Early stop still preserves full later reveal");
        o.rotationStopSeconds=0;Check(o.TotalRotationTurns==0&&Near(o.DisplayRotationDegrees(5),0),"Zero stop leaves head still from start");
        o.rotationStopSeconds=105;o.totalSeconds=200;
        Check(Near(o.RotationStopTargetSeconds,105)&&Near(o.RotationStopSeconds,110)&&Near(o.RevealEndSeconds,203),"Edited stop remains independent when interview timing changes");
        o=Options(100,10);o.rotationStopSeconds=999;
        Check(Near(o.RotationStopSeconds,100)&&Near(o.ActualSeconds,103),"Unset custom-stop flag preserves old serialized behaviour");
        foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity}){
            o.rotationStopSeconds=invalid;o.useCustomRotationStop=true;o.Normalize();
            Check(!o.useCustomRotationStop&&Near(o.RotationStopSeconds,100),"Invalid stop falls back to original stop time");
        }
        o.rotationStopSeconds=99999;o.useCustomRotationStop=true;o.Normalize();Check(o.rotationStopSeconds==7200,"Custom stop upper bound");

        foreach(float period in new[]{.1f,7.3f,10f,37.5f})foreach(float stop in new[]{0f,10f,102.5f,105f,112f})foreach(int fps in new[]{24,25,30,60}){
            o=Options(100,period,3,fps);
            double start=o.RevealStartSeconds,end=o.RevealEndSeconds;
            o.rotationStopSeconds=stop;o.useCustomRotationStop=true;o.Normalize();
            Check(Near(o.RevealStartSeconds,start)&&Near(o.RevealEndSeconds,end),"Custom stop preserves reveal for all periods and frame rates");
            Check(o.RotationStopSeconds>=stop-1e-7&&o.RotationStopSeconds-stop<o.RotationPeriodSeconds+1e-7,"Fractional stop completes current forward turn");
            Check(Near(o.DisplayRotationDegrees((float)o.RotationStopSeconds),0),"Custom stop reaches front for decimal periods too");
            Check(o.EncodedDurationSeconds>=Math.Max(end,o.RotationStopSeconds)-1e-7,"Encoding retains both independent timelines");
            if(stop>0){
                double t=o.RotationStopSeconds*.8;
                Check(Near(o.RotationDegrees(t),t*o.DegreesPerSecond,.001),"Custom stop never changes configured angular speed");
            }
        }

        // User's fixed interview timeline: 146 s film, reveal at 71 s,
        // fade for 3 s, complete the last forward turn at 120 s, then hold.
        o=Options(146,10);
        o.faceRevealStartSeconds=71;o.useExplicitRevealStart=true;
        o.rotationStopSeconds=120;o.useCustomRotationStop=true;o.Normalize();
        Check(Near(o.RevealStartSeconds,71)&&Near(o.RevealEndSeconds,74),"Explicit reveal starts at requested second, not the next front-facing turn");
        Check(Near(o.ReverseSeconds,71)&&Near(o.GrowthTime(71),0),"Flowers disappear before the explicit face reveal");
        Check(Near(o.RotationStopSeconds,120)&&Near(o.FinalHoldSeconds,26),"Stop at 120 and hold until 146");
        Check(Near(o.ActualSeconds,146)&&o.FrameCount==4380&&o.TimingError==null,"Exact 146-second 30 fps export");
        Check(OrientationDistance(o.DisplayRotationDegrees(72),72)<.0001&&OrientationDistance(o.DisplayRotationDegrees(119),324)<.0001,"Rotation continues during and after explicit face fade");
        Check(Near(o.DisplayRotationDegrees(120),0)&&Near(o.DisplayRotationDegrees(130),0)&&Near(o.DisplayRotationDegrees(146),0),"Final 26 seconds remain facing front");
        var preview=new NarcissusRecordingPreviewClock();
        preview.Play(1000,146);Check(preview.IsPlaying&&Near(preview.Seconds,0),"Preview starts at beginning");
        Check(preview.Advance(1071,146)&&Near(preview.Seconds,71),"Playback reaches exact face-reveal start");
        preview.Pause();Check(!preview.Advance(2000,146)&&Near(preview.Seconds,71),"Paused preview does not advance");
        preview.Play(2000,146);preview.Advance(2003,146);
        Check(Near(preview.Seconds,74),"Resuming continues at same point without counting paused wall time");
        preview.Seek(119,146);Check(!preview.IsPlaying&&Near(preview.Seconds,119),"Scrubbing pauses playback");
        preview.Play(3000,146);preview.Advance(3001,146);
        Check(Near(preview.Seconds,120)&&Near(o.DisplayRotationDegrees((float)preview.Seconds),0),"Playback reaches natural rotation stop");
        preview.Advance(3027,146);Check(!preview.IsPlaying&&Near(preview.Seconds,146),"Playback includes final hold and pauses at film end");
        preview.Play(4000,146);Check(preview.IsPlaying&&Near(preview.Seconds,0),"Play at film end restarts");
        preview.Advance(4005,146);preview.Play(5000,146,true);Check(Near(preview.Seconds,0),"Replay explicitly returns to zero");
        preview.Advance(5200,146);Check(!preview.IsPlaying&&Near(preview.Seconds,146),"Slow preview cannot advance beyond film end");
        Check(o.FrameCount==4380&&Near(o.FrameTime(120*30),120),"Preview transport does not alter offline export frame count or clock");
        foreach(int fps in new[]{24,25,30,60}){
            o.fps=fps;
            Check(o.FrameCount==146*fps&&Near(o.EncodedDurationSeconds,146),"Fixed total duration at every output frame rate");
            Check(Near(o.FrameTime(o.FrameCount-1),146)&&Near(o.FrameTime(120*fps),120),"Hold and final endpoint are inside encoded sequence");
        }
        foreach(float stop in new[]{80f,100f,105f,120f,140f}){
            o.rotationStopSeconds=stop;o.Normalize();
            Check(Near(o.RevealStartSeconds,71)&&Near(o.RevealEndSeconds,74)&&Near(o.ActualSeconds,146),"Editing stop preserves both explicit reveal and fixed end");
            Check(o.TimingError==null&&Near(o.FinalHoldSeconds,146-o.RotationStopSeconds),"Hold fills remaining film after complete turn");
        }
        o.rotationStopSeconds=145;o.Normalize();
        Check(Near(o.RotationStopSeconds,150)&&o.TimingError!=null&&o.TimingError.Contains("150"),"Impossible last turn beyond fixed film end is reported");
        Check(Near(o.ActualSeconds,146),"Invalid stop does not silently extend the fixed film");
        o.rotationStopSeconds=120;o.totalSeconds=200;o.Normalize();
        Check(Near(o.RevealStartSeconds,71)&&Near(o.RevealEndSeconds,74)&&Near(o.RotationStopSeconds,120)&&Near(o.FinalHoldSeconds,80),"Film end independent of reveal and stop");
        o.secondsPerTurn=20;o.Normalize();
        Check(Near(o.RevealStartSeconds,71)&&Near(o.RevealEndSeconds,74)&&Near(o.RotationStopSeconds,120),"Changing rotation speed does not move explicit reveal time");
        o.faceRevealStartSeconds=71.25f;o.Normalize();
        Check(Near(o.RevealStartSeconds,71.25)&&Near(o.RevealEndSeconds,74.25),"Fractional explicit reveal second supported");
        o.faceRevealStartSeconds=0;o.Normalize();
        Check(Near(o.GrowthTime(0),0)&&!float.IsNaN(o.GrowthTime(0))&&Near(o.RevealStartSeconds,0),"Immediate reveal avoids zero-length growth division");
        o.faceRevealStartSeconds=999;o.Normalize();
        Check(Near(o.RevealEndSeconds,200),"Full face fade fits within total film length");
        foreach(float invalid in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity}){
            o.faceRevealStartSeconds=invalid;o.useExplicitRevealStart=true;o.Normalize();
            Check(!o.useExplicitRevealStart,"Invalid explicit reveal start repaired");
        }

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
            Check(Near(o.GrowthTime(0),21)&&Near(o.GrowthTime((float)o.ReverseSeconds),0),"Original reverse growth endpoint unchanged");
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
