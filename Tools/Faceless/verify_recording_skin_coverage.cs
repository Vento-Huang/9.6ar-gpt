using System;
using System.IO;
using System.Linq;
using System.Text.Json;

// Compile with UNITY_EDITOR and the production recording skin coverage helper.
static class VerifyRecordingSkinCoverage
{
    static int checks;
    static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    static void Reject(Action action){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Check(rejected,"Invalid coverage input rejected");}
    static void Main(string[] args)
    {
        var square=new[]{-.6f,-.6f,.6f,-.6f,.6f,.6f,-.6f,.6f};
        Check(NarcissusRecordingSkinCoverage.Build(32,32,null,0,1,.1f).All(x=>x==255),"Old calibration leaves recording unchanged");
        Check(NarcissusRecordingSkinCoverage.Build(32,32,new float[0],0,1,.1f).All(x=>x==255),"Empty optional outline leaves recording unchanged");
        var mask=NarcissusRecordingSkinCoverage.Build(200,200,square,0,1,.1f);
        for(int y=0;y<200;y++)for(int x=0;x<200;x++){
            double wx=(x+.5)/100-1,wy=(y+.5)/100-1;
            double distance=.6-Math.Max(Math.Abs(wx),Math.Abs(wy));
            if(distance<0)Check(mask[y*200+x]==0,"Hair outside boundary never gains coverage");
            else if(distance>.1)Check(mask[y*200+x]==255,"Interior facial features retain full erasure");
            else {double t=distance/.1;Check(Math.Abs(mask[y*200+x]-255*t*t*(3-2*t))<1,"Feather stays inside skin with smooth transition");}
        }
        var reverse=new[]{-.6f,.6f,.6f,.6f,.6f,-.6f,-.6f,-.6f};
        Check(mask.SequenceEqual(NarcissusRecordingSkinCoverage.Build(200,200,reverse,0,1,.1f)),"Outline winding does not change coverage");
        Check(mask.SequenceEqual(NarcissusRecordingSkinCoverage.Build(200,200,square.Concat(new[]{-.6f,-.6f}).ToArray(),0,1,.1f)),"Repeated closing point is safe");
        var shifted=square.Select((v,i)=>i%2==1?v+3:v).ToArray();
        var offset=NarcissusRecordingSkinCoverage.Build(200,200,shifted,3,1,.1f);
        Check(mask.Zip(offset,(a,b)=>Math.Abs(a-b)).Max()<=1,"Reference center offset preserves mask alignment");
        var scaled=square.Select(v=>v*2).ToArray();
        Check(mask.SequenceEqual(NarcissusRecordingSkinCoverage.Build(200,200,scaled,0,2,.2f)),"Model and projection scale preserve coverage");
        var concave=new[]{-.8f,-.8f,.8f,-.8f,.8f,0f,0f,0f,0f,.8f,-.8f,.8f};
        var l=NarcissusRecordingSkinCoverage.Build(100,100,concave,0,1,.02f);
        Check(l[75*100+75]==0&&l[75*100+25]==255&&l[25*100+75]==255,"Concave hair boundary preserves inward notch");
        var outside=new[]{10f,10f,11f,10f,11f,11f,10f,11f};
        Check(NarcissusRecordingSkinCoverage.Build(8,8,outside,0,1,.1f).All(x=>x==0),"Outside-frame boundary does not wrap onto model");
        Reject(()=>NarcissusRecordingSkinCoverage.Build(0,32,square,0,1,.1f));
        Reject(()=>NarcissusRecordingSkinCoverage.Build(32,32,square,0,0,.1f));
        Reject(()=>NarcissusRecordingSkinCoverage.Build(32,32,new[]{0f,0f,1f},0,1,.1f));
        Reject(()=>NarcissusRecordingSkinCoverage.Build(32,32,new[]{0f,0f,1f,1f,2f,2f},0,1,.1f));
        Reject(()=>NarcissusRecordingSkinCoverage.Build(32,32,new[]{0f,0f,float.NaN,1f,1f,0f},0,1,.1f));
        Reject(()=>NarcissusRecordingSkinCoverage.Build(32,32,square,0,1,float.NaN));
        Reject(()=>NarcissusRecordingSkinCoverage.Build(32,32,square,0,1,0));
        if(args.Length>0){
            using(var doc=JsonDocument.Parse(File.ReadAllText(args[0]))){
                var d=doc.RootElement;var xy=d.GetProperty("recordingSkinOutline").EnumerateArray().SelectMany(v=>new[]{v.GetProperty("x").GetSingle(),v.GetProperty("y").GetSingle()}).ToArray();
                var actual=NarcissusRecordingSkinCoverage.Build(1024,1024,xy,d.GetProperty("referenceCenterY").GetSingle(),d.GetProperty("referenceHalfHeight").GetSingle(),d.GetProperty("recordingSkinFeather").GetSingle());
                // Image-space checks against the inspected model reference; no
                // portrait or landmark coordinates are committed to the repo.
                Check(actual.Any(v=>v==255)&&actual.Any(v=>v==0)&&actual.Any(v=>v>0&&v<255),"Actual calibration has interior, protected exterior and feather");
                File.WriteAllBytes(args[1],actual);
            }
        }
        Console.WriteLine("PASS: "+checks+" offline skin coverage checks; Unity GPU verification remains pending.");
    }
}
