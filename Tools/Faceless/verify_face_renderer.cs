// Compile this file WITH the actual FacelessFaceRenderer.cs, without Unity.
// Narrow managed stubs validate six independent entry clocks, state ownership,
// resource cleanup and failure handling. They do NOT validate Unity, shaders,
// MediaPipe, real camera matching, GPU rendering or the accepted geometry.
using System;
using System.Collections.Generic;
using UnityEngine;

public static class FaceRendererChecks
{
    static int _checks;
    static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new Exception("FAIL: " + message);
    }
    static void Near(float actual, float expected, string message)
    { Check(Math.Abs(actual - expected) < 0.0001f, message + ": " + actual + " != " + expected); }
    static Vector2[] Points(float offset = 0f)
    {
        var points = new Vector2[468];
        for (int i = 0; i < points.Length; i++) points[i] = new Vector2(100 + offset + i % 20, 100 + i / 20);
        return points;
    }
    static bool HasTemporalPass()
    {
        foreach (var call in Graphics.Calls) if (call.Pass == 6) return true;
        return false;
    }

    public static int Main()
    {
        var settings = new MidFaceEraseMask();
        var raw = new Texture { width = 1280, height = 720 };
        var faces = new FacelessFaceRenderer[6];
        var points = new Vector2[6][];
        var composites = new HashSet<Material>();
        var skins = new HashSet<Texture>();
        var donorTextures = new HashSet<Texture>();
        var sentinel = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        RenderTexture.active = sentinel;
        GL.sRGBWrite = true;

        for (int i = 0; i < faces.Length; i++)
        {
            faces[i] = new FacelessFaceRenderer(settings);
            faces[i].Reset(100 + i);
            points[i] = Points(i * 140);
            float start = i * 0.4f;
            Graphics.Calls.Clear();
            Check(faces[i].Update(raw, points[i], Matrix4x4.identity, true, start, 99f, true), "initial face " + i);
            Near(faces[i].PresentationSeconds, 0f, "first usable frame must ignore elapsed " + i);
            Check(!HasTemporalPass(), "new track must not sample prior skin " + i);
            Check(faces[i].Ready && faces[i].IsTracking, "successful face ready " + i);
            Check(faces[i].TrackId == 100 + i, "track ID " + i);
            foreach (var call in Graphics.Calls)
                if (call.Pass == 0) donorTextures.Add(call.Destination);
            if (start < 2f)
                Check(faces[i].Update(raw, points[i], Matrix4x4.identity, true, 2f, 2f - start, true), "second face " + i);
            faces[i].ApplyComposite(faces[i].Composite);
            composites.Add(faces[i].Composite);
            skins.Add(faces[i].Composite.GetTexture("_SkinTex"));
            Near(faces[i].Composite.GetFloat("_OverlayOnly"), 1f, "overlay flag " + i);
            Near(faces[i].PresentationSeconds, 2f - start, "different start time " + i);
            float t = (2f - start) / 2f;
            Near(faces[i].EntryProgress, t * t * (3f - 2f * t), "independent entry weight " + i);
            Check(RenderTexture.active == sentinel && GL.sRGBWrite, "restore render globals " + i);
        }
        Check(composites.Count == 6, "six separate composite materials");
        Check(skins.Count == 6 && !skins.Contains(null), "six separate skin histories");
        Check(donorTextures.Count == 6, "six separate cheek-sample buffers");

        var paused = faces[1];
        float pausedClock = paused.PresentationSeconds;
        paused.ApplyComposite(paused.Composite);
        var heldSkin = paused.Composite.GetTexture("_SkinTex");
        float heldAmount = paused.Composite.GetFloat("_Amount");
        paused.MarkMissing();
        paused.ApplyComposite(paused.Composite);
        Check(paused.Ready && !paused.IsTracking, "missing face retains held output only");
        Near(paused.PresentationSeconds, pausedClock, "missing freezes entry clock");
        Near(paused.Composite.GetFloat("_Amount"), heldAmount, "missing must not unmask held frame");
        Check(paused.Composite.GetTexture("_SkinTex") == heldSkin, "missing retains held skin texture");
        Graphics.Calls.Clear();
        Check(paused.Update(raw, points[1], Matrix4x4.identity, true, 9f, 7f, true), "reacquire");
        Near(paused.PresentationSeconds, pausedClock, "reacquire does not count absence even if caller supplies gap");
        Check(!HasTemporalPass(), "reacquire refreshes sampling instead of blending stale skin");
        Check(paused.Update(raw, points[1], Matrix4x4.identity, true, 9.2f, 0.2f, true), "resumed face");
        Near(paused.PresentationSeconds, pausedClock + 0.2f, "valid elapsed resumes after reacquisition");
        Near(faces[5].PresentationSeconds, 0f, "updating one person does not advance another");

        var replayed = faces[2];
        replayed.MarkMissing();
        replayed.ApplyComposite(replayed.Composite);
        float replayHeldAmount = replayed.Composite.GetFloat("_Amount");
        replayed.Replay();
        replayed.ApplyComposite(replayed.Composite);
        Near(replayed.Composite.GetFloat("_Amount"), replayHeldAmount, "queued replay must not reveal held face");
        Check(replayed.Update(raw, points[2], Matrix4x4.identity, true, 10f, 8f, true), "replay on fresh pair");
        Near(replayed.EntryProgress, 0f, "replay first fresh frame starts untouched");

        var replaced = faces[3];
        Graphics.Calls.Clear();
        Check(replaced.Update(raw, points[3], Matrix4x4.identity, true, 2.2f, 0.2f, true), "old track valid");
        Check(HasTemporalPass(), "control: continued track uses its own prior history");
        replaced.Reset(901);
        replaced.ApplyComposite(replaced.Composite);
        Check(replaced.TrackId == 901 && !replaced.Ready && !replaced.IsTracking, "replacement resets state");
        Near(replaced.PresentationSeconds, 0f, "replacement clock reset");
        Near(replaced.Composite.GetFloat("_Amount"), 0f, "replacement cannot display prior face");
        Graphics.Calls.Clear();
        Check(replaced.Update(raw, Points(500), Matrix4x4.identity, false, 11f, 100f, true), "new track valid");
        Check(!HasTemporalPass(), "replacement must not blend old person's colors");
        Near(replaced.EntryProgress, 0f, "replacement starts its own entry");

        Check(faces[4].Update(raw, points[4], Matrix4x4.identity, true, 5f, 3f, true), "growth frame");
        Check(faces[4].GrowthProgress > 0f, "completed entry enables subsequent independent growth clock");
        Near(faces[5].GrowthProgress, 0f, "newcomer has no other person's growth progress");
        settings.playEntryAnimation = false;
        Near(faces[5].EntryProgress, 1f, "shared final skin override");
        settings.playEntryAnimation = true;

        var failing = new FacelessFaceRenderer(settings);
        failing.Reset(777);
        RenderTexture.AllocationFailuresRemaining = 1;
        Check(!failing.Update(raw, Points(), Matrix4x4.identity, false, 12f, 1f, true), "allocation failure returns false");
        Check(!failing.Ready && !failing.IsTracking, "allocation failure cannot expose a ready output");
        Check(Debug.Errors.Count == 1, "allocation failure is reported once");
        Check(failing.Update(raw, Points(), Matrix4x4.identity, false, 12.2f, 0.2f, true), "allocation retry recovers");
        Near(failing.EntryProgress, 0f, "failed frame did not advance entry");
        var invalid = Points();
        invalid[15].x = float.NaN;
        Check(!failing.Update(raw, invalid, Matrix4x4.identity, false, 12.3f, 0.1f, true), "invalid landmark rejected");
        Check(!failing.Ready && !failing.IsTracking, "invalid landmark disables output");

        failing.Dispose();
        foreach (var face in faces) face.Dispose();
        int destroyed = UnityEngine.Object.DestroyCount;
        failing.Dispose();
        foreach (var face in faces) face.Dispose();
        Check(UnityEngine.Object.DestroyCount == destroyed, "Dispose is idempotent");
        Check(RenderTexture.AliveCount == 0, "all allocated render textures released");
        Check(Material.AliveCount == 0, "all materials destroyed");
        Check(!faces[0].Update(raw, points[0], Matrix4x4.identity, true, 15f, 1f, true), "disposed worker cannot update");
        Console.WriteLine("PASS: " + _checks + " checks on actual FacelessFaceRenderer.cs with managed stubs (not Unity/GPU validation).");
        return 0;
    }
}

// Stub only the API surface used by the production worker.
public class MidFaceEraseMask : UnityEngine.Object
{
    public Shader reconstructionShader = new Shader(), compositeShader = new Shader(), surfaceShader = new Shader();
    public bool playEntryAnimation = true, showMask;
    public float entryDurationSeconds = 2f, maskScale = 1f, featherFraction = .09f, landmarkCutoff = 4f;
    public float localColorStrength = 1f, highlightSuppression = .85f, colorSmoothingSeconds = .065f;
    public float effectAmount = 1f, volume, fineGrain = .35f;
    public int reconstructionResolution = 256;
}
public sealed class FacelessRegions
{
    public const int Count = 15;
    public float Width = 120f;
    public bool Build(Vector2[] points, float scale, float feather) { return true; }
    public void SetMaterial(Material material) { }
}
public sealed class FacelessSurface : IDisposable
{
    public Texture Texture { get; private set; }
    public bool Render(Vector2[] points, int width, int height, Shader shader)
    { if (Texture == null) Texture = new Texture(); return true; }
    public void Dispose() { UnityEngine.Object.Destroy(Texture); Texture = null; }
}
public sealed class FacelessInterior : IDisposable
{
    public Texture Texture { get; private set; }
    public void Build(FacelessRegions regions) { if (Texture == null) Texture = new Texture(); }
    public void Dispose() { UnityEngine.Object.Destroy(Texture); Texture = null; }
}

namespace UnityEngine
{
    public class Object
    {
        public static int DestroyCount;
        internal bool Destroyed;
        public string name;
        public HideFlags hideFlags;
        public static void Destroy(Object value)
        {
            if (value == null || value.Destroyed) return;
            value.Destroyed = true; DestroyCount++;
            if (value is Material) Material.AliveCount--;
        }
        public static void DestroyImmediate(Object value) { Destroy(value); }
    }
    public class Shader : Object { }
    public class Texture : Object { public int width, height; }
    public class Material : Object
    {
        public static int AliveCount;
        readonly Dictionary<string, float> _floats = new Dictionary<string, float>();
        readonly Dictionary<string, Texture> _textures = new Dictionary<string, Texture>();
        public Material(Shader shader) { AliveCount++; }
        public void SetFloat(string name, float value) { _floats[name] = value; }
        public float GetFloat(string name) { return _floats.TryGetValue(name, out var value) ? value : 0f; }
        public void SetTexture(string name, Texture value) { _textures[name] = value; }
        public Texture GetTexture(string name) { return _textures.TryGetValue(name, out var value) ? value : null; }
        public void SetVector(string name, Vector4 value) { }
        public void SetFloatArray(string name, float[] value) { }
    }
    public class RenderTexture : Texture
    {
        public static int AliveCount, AllocationFailuresRemaining;
        public static RenderTexture active;
        public FilterMode filterMode;
        public TextureWrapMode wrapMode;
        public bool useMipMap, autoGenerateMips;
        public int antiAliasing;
        bool _created;
        public RenderTexture(int w, int h, int depth, RenderTextureFormat format, RenderTextureReadWrite read)
        { width = w; height = h; }
        public bool Create()
        {
            if (AllocationFailuresRemaining > 0) { AllocationFailuresRemaining--; return false; }
            if (!_created) { _created = true; AliveCount++; }
            return true;
        }
        public void Release() { if (_created) { _created = false; AliveCount--; } }
    }
    public enum RenderTextureFormat { ARGBHalf, ARGBFloat }
    public enum RenderTextureReadWrite { Linear }
    public enum FilterMode { Bilinear }
    public enum TextureWrapMode { Clamp }
    public enum HideFlags { HideAndDontSave }
    public static class SystemInfo
    { public static bool SupportsRenderTextureFormat(RenderTextureFormat format) { return true; } }
    public static class Application { public static bool isPlaying = true; }
    public static class GL { public static bool sRGBWrite; }
    public static class Graphics
    {
        public struct BlitCall { public int Pass; public Texture Source; public RenderTexture Destination; }
        public static readonly List<BlitCall> Calls = new List<BlitCall>();
        public static void Blit(Texture source, RenderTexture destination, Material material, int pass)
        { Calls.Add(new BlitCall { Pass = pass, Source = source, Destination = destination }); }
        public static void Blit(Texture source, RenderTexture destination) { Blit(source, destination, null, -1); }
    }
    public static class Debug
    {
        public static readonly List<string> Errors = new List<string>();
        public static void LogError(string error, Object context) { Errors.Add(error); }
    }
    public struct Matrix4x4 { public static Matrix4x4 identity => new Matrix4x4(); }
    public struct Vector4
    { public float x, y, z, w; public Vector4(float a, float b, float c, float d) { x = a; y = b; z = c; w = d; } }
    public struct Vector2
    {
        public float x, y;
        public Vector2(float a, float b) { x = a; y = b; }
        public static Vector2 zero => new Vector2();
        public float sqrMagnitude => x * x + y * y;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x + b.x, a.y + b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x - b.x, a.y - b.y);
        public static Vector2 operator *(Vector2 a, float b) => new Vector2(a.x * b, a.y * b);
        public static Vector2 operator /(Vector2 a, float b) => new Vector2(a.x / b, a.y / b);
        public static float Distance(Vector2 a, Vector2 b) => (a - b).magnitude;
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => a + (b - a) * Mathf.Clamp(t, 0f, 1f);
        public static Vector2 ClampMagnitude(Vector2 a, float length) => a.magnitude <= length ? a : a * (length / a.magnitude);
    }
    public static class Mathf
    {
        public const float PI = (float)Math.PI;
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Exp(float a) => (float)Math.Exp(a);
        public static float Clamp(float a, float lo, float hi) => Math.Max(lo, Math.Min(hi, a));
        public static int Clamp(int a, int lo, int hi) => Math.Max(lo, Math.Min(hi, a));
        public static int ClosestPowerOfTwo(int a)
        { int p = 1; while (p < a) p *= 2; return a - p / 2 < p - a ? p / 2 : p; }
        public static float InverseLerp(float a, float b, float v) => a == b ? 0f : Clamp((v - a) / (b - a), 0f, 1f);
        public static float SmoothStep(float a, float b, float t)
        { t = Clamp(t, 0f, 1f); return a + (b - a) * t * t * (3f - 2f * t); }
    }
}
