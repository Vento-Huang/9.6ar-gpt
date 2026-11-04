"""Run the production preview publish/release methods with narrow C# doubles.

Requires .NET 8 SDK: python Tools/Faceless/verify_preview.py --dotnet dotnet
Exercises buffer ownership and tracking-loss transitions, not Unity/camera/GPU.
"""
import argparse
from pathlib import Path
import subprocess
import tempfile


def method(source, name):
    start = source.index("    void " + name + "(")
    opening = source.index("{", start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[2]
    source = (root / "Assets/MediaPipeUnity/Samples/Scenes/Face Landmark Detection/FaceLandmarkerRunner.cs").read_text()
    # Use the actual publish/release bodies and public state declarations.
    declarations = source[source.index("    public bool PreviewIsSynchronized"):source.index("    private Experimental.TextureFramePool")]
    harness = r'''
using System;
using System.Collections.Generic;
class Texture { }
class RenderTexture : Texture { public void Release() { } }
class Screen { public Texture texture; }
class Face { public List<int> landmarks; }
struct FaceLandmarkerResult { public List<Face> faceLandmarks; }
class Controller
{
    public int Calls; public FaceLandmarkerResult Last;
    public void DrawNow(FaceLandmarkerResult value) { Calls++; Last = value; }
}
static class Time { public static float unscaledTime; }
static class Mathf
{
    public static int Min(int a, int b) => Math.Min(a, b);
    public static float Clamp(float v, float a, float b) => Math.Max(a, Math.Min(v, b));
}
class Config { public int NumFaces = 12; }
class PreviewChecks
{
    public float trackingLossHoldSeconds = 1f;
    bool _synchronizeThisRun = true;
    RenderTexture[] _previewBuffers = { new RenderTexture(), new RenderTexture() };
    Texture _originalPreviewTexture = new Texture();
    int _presentedBuffer;
    Screen screen = new Screen();
    Controller _faceLandmarkerResultAnnotationController = new Controller();
    Config config = new Config();
    static void Destroy(object value) { }
    static int checks;
    static void Check(bool value, string message)
    { checks++; if (!value) throw new Exception(message); }
    void Publish(float now, bool success, FaceLandmarkerResult result)
    { Time.unscaledTime = now; PublishDetection(success, result, 1 - _presentedBuffer); }
    static FaceLandmarkerResult FaceResult(int count = 468)
    { return new FaceLandmarkerResult { faceLandmarks = new List<Face> { new Face { landmarks = new List<int>(new int[count]) } } }; }
    static void Main()
    {
        var p = new PreviewChecks();
        p.Publish(0f, true, default);
        Check(p.screen.texture == p._previewBuffers[1], "Empty startup publishes camera instead of waiting for a face.");
        Check(!p.PreviewIsHoldingFace && !p.PreviewHasFace, "Empty startup has no hold.");
        p.Publish(1f, true, FaceResult());
        var held = p.screen.texture;
        int draws = p._faceLandmarkerResultAnnotationController.Calls;
        p.Publish(1.2f, true, default);
        p.Publish(1.9f, false, FaceResult());
        Check(p.PreviewIsHoldingFace && !p.PreviewHasFace, "Brief missing/failed inference holds.");
        Check(p.screen.texture == held, "Capture must not replace presented buffer during hold.");
        Check(p._faceLandmarkerResultAnnotationController.Calls == draws, "Hold retains matched geometry.");
        Check(p.LastMatchedFrameTime == 1f, "Missing frames do not extend hold clock.");
        p.Publish(2.01f, true, default);
        Check(!p.PreviewIsHoldingFace && p.screen.texture != held, "Expired hold returns to fresh camera.");
        Check(p._faceLandmarkerResultAnnotationController.Last.faceLandmarks == null, "Fresh raw camera has no stale landmarks.");
        p.Publish(2.1f, true, FaceResult());
        Check(p.PreviewHasFace && !p.PreviewIsHoldingFace, "Reacquisition publishes new matched pair immediately.");
        p.Publish(2.2f, true, FaceResult(467));
        Check(p.PreviewIsHoldingFace, "Incomplete landmarks count as missing.");
        var twelve = new FaceLandmarkerResult { faceLandmarks = new List<Face>() };
        for (int i = 0; i < 11; i++) twelve.faceLandmarks.Add(new Face());
        twelve.faceLandmarks.Add(FaceResult().faceLandmarks[0]);
        p.Publish(2.3f, true, twelve);
        Check(p.PreviewHasFace, "Twelfth valid detection is accepted.");
        p.trackingLossHoldSeconds = 0f;
        p.Publish(2.4f, true, default);
        Check(!p.PreviewIsHoldingFace, "Zero hold publishes camera immediately.");
        p.trackingLossHoldSeconds = 1f;
        p.Publish(3f, true, FaceResult());
        p.Publish(3.1f, true, default);
        p.ReleasePairedPreview();
        Check(!p.PreviewIsHoldingFace && !p.PreviewHasFace && p.LastMatchedFrameTime < 0f, "Camera restart clears held state.");
        Check(p.screen.texture == p._originalForTest, "Camera stop restores original texture.");
        p._synchronizeThisRun = false;
        p.Publish(4f, true, FaceResult());
        Check(p._faceLandmarkerResultAnnotationController.Last.faceLandmarks.Count == 1, "Unpaired mode still publishes detection.");
        Console.WriteLine("PASS: " + checks + " preview transition checks (standalone C#, not Unity).");
    }
    Texture _originalForTest;
    PreviewChecks() { _originalForTest = _originalPreviewTexture; }
'''
    harness += declarations + method(source, "PublishDetection") + method(source, "ReleasePairedPreview") + "\n}\n"
    with tempfile.TemporaryDirectory(prefix="faceless-preview-") as work:
        work = Path(work)
        (work / "Program.cs").write_text(harness)
        (work / "NuGet.Config").write_text("<configuration><packageSources><clear /></packageSources></configuration>")
        (work / "check.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>')
        subprocess.run([args.dotnet, "run", "--project", str(work / "check.csproj"), "--configuration", "Release"], check=True)


if __name__ == "__main__":
    main()
