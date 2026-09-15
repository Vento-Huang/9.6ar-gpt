// Standalone behavioral checks for the production tracker (no Unity runtime).
// Example with Mono:
// mcs -out:/tmp/faceless-track-tests.exe Tools/Faceless/verify_tracks.cs \
//   'Assets/MediaPipeUnity/Samples/Scenes/Face Landmark Detection/FacelessTrackAssigner.cs'
// mono /tmp/faceless-track-tests.exe
// The minimal Unity math substitutes below cover only the helper's public API.
using System;
using UnityEngine;

static class FacelessTrackTests
{
    static int _checks;

    static Rect Face(float x, float y = 100f, float width = 50f, float height = 70f)
    {
        return new Rect(x - width * 0.5f, y - height * 0.5f, width, height);
    }

    static void Check(bool value, string message)
    {
        _checks++;
        if (!value) throw new Exception(message);
    }

    static void Main()
    {
        SixFacesKeepSlotsWhenDetectionOrderChanges();
        MissingTrackResumesThenExpires();
        NewcomerReplacesOnlyUnmatchedSlotAtCapacity();
        AssignmentUsesGlobalCost();
        VelocityPreservesTracksThroughAResolvableCrossing();
        RelativeSizeAndInvalidBoundsAreRejected();
        ClockRestartAndResetCreateFreshState();
        Console.WriteLine("PASS: " + _checks + " geometric tracking checks (standalone C#, not Unity).");
    }

    static void SixFacesKeepSlotsWhenDetectionOrderChanges()
    {
        var tracker = new FacelessTrackAssigner();
        var bounds = new Rect[6];
        for (int i = 0; i < 6; i++) bounds[i] = Face(i * 150f);
        tracker.Update(bounds, 6, 0f, 3f);
        int[] slots = (int[])tracker.DetectionToSlot.Clone();
        int[] ids = (int[])tracker.TrackIds.Clone();
        for (int i = 0; i < 6; i++)
        {
            Check(slots[i] == i, "First detections use separate slots.");
            Check(ids[i] > 0 && tracker.SlotIsNew[i], "A new slot needs a fresh ID and reset signal.");
        }
        int[] order = { 5, 2, 0, 4, 1, 3 };
        for (int i = 0; i < 6; i++) bounds[i] = Face(order[i] * 150f + 2f);
        tracker.Update(bounds, 6, 0.033f, 3f);
        for (int i = 0; i < 6; i++)
        {
            int slot = tracker.DetectionToSlot[i];
            Check(slot == slots[order[i]], "Detection reordering must not exchange effects.");
            Check(tracker.TrackIds[slot] == ids[slot], "Reordered detection retains ID.");
            Check(!tracker.SlotIsNew[slot], "Continuous detection must not restart entrance.");
        }
    }

    static void MissingTrackResumesThenExpires()
    {
        var tracker = new FacelessTrackAssigner();
        var bounds = new[] { Face(100f) };
        tracker.Update(bounds, 1, 0f, 3f);
        int slot = tracker.DetectionToSlot[0];
        int id = tracker.TrackIds[slot];
        tracker.Update(null, 0, 1f, 3f);
        Check(tracker.SlotIsActive[slot] && !tracker.SlotIsVisible[slot], "Brief absence holds state without rendering.");
        tracker.Update(bounds, 1, 2f, 3f);
        Check(tracker.DetectionToSlot[0] == slot && tracker.TrackIds[slot] == id, "Brief absence resumes the same track.");
        Check(!tracker.SlotIsNew[slot], "Brief absence preserves entrance progress.");
        tracker.Update(null, 0, 5.01f, 3f);
        Check(!tracker.SlotIsActive[slot] && tracker.TrackIds[slot] == 0, "Long absence retires the slot.");
        tracker.Update(bounds, 1, 5.1f, 3f);
        Check(tracker.TrackIds[tracker.DetectionToSlot[0]] != id, "Return after timeout starts a new geometric track.");
        Check(tracker.SlotIsNew[tracker.DetectionToSlot[0]], "Return after timeout requests fresh skin/timeline state.");
    }

    static void NewcomerReplacesOnlyUnmatchedSlotAtCapacity()
    {
        var tracker = new FacelessTrackAssigner();
        var bounds = new Rect[6];
        for (int i = 0; i < 6; i++) bounds[i] = Face(i * 150f);
        tracker.Update(bounds, 6, 0f, 3f);
        int[] ids = (int[])tracker.TrackIds.Clone();
        // New face comes first in the detector output. Slot 5 is missing;
        // slots 0..4 must be reserved before the new face chooses a slot.
        bounds[0] = Face(2000f);
        for (int i = 1; i < 6; i++) bounds[i] = Face((i - 1) * 150f);
        tracker.Update(bounds, 6, 0.05f, 3f);
        Check(tracker.DetectionToSlot[0] == 5, "A seventh arrival must replace the unseen sixth slot.");
        Check(tracker.TrackIds[5] != ids[5] && tracker.SlotIsNew[5], "Replacement cannot inherit the previous skin or animation.");
        for (int i = 1; i < 6; i++)
        {
            Check(tracker.DetectionToSlot[i] == i - 1, "Newcomer must not steal a later visible match.");
            Check(tracker.TrackIds[i - 1] == ids[i - 1], "Existing visible track must keep ID at capacity.");
        }
    }

    static void AssignmentUsesGlobalCost()
    {
        var tracker = new FacelessTrackAssigner();
        var bounds = new[] { Face(0f, 100f, 100f, 100f), Face(80f, 100f, 100f, 100f) };
        tracker.Update(bounds, 2, 0f, 3f);
        int first = tracker.DetectionToSlot[0], second = tracker.DetectionToSlot[1];
        // The first new detection is closer to slot 0, but assigning it there
        // makes the second detection a much worse match. Greedy matching fails.
        bounds[0] = Face(30f, 100f, 100f, 100f);
        bounds[1] = Face(-30f, 100f, 100f, 100f);
        tracker.Update(bounds, 2, 0.033f, 3f);
        Check(tracker.DetectionToSlot[0] == second && tracker.DetectionToSlot[1] == first,
            "Assignment must minimize joint cost rather than consume the nearest slot greedily.");
    }

    static void VelocityPreservesTracksThroughAResolvableCrossing()
    {
        var tracker = new FacelessTrackAssigner();
        var bounds = new[] { Face(0f), Face(200f) };
        tracker.Update(bounds, 2, 0f, 3f);
        int first = tracker.DetectionToSlot[0], second = tracker.DetectionToSlot[1];
        for (int frame = 1; frame <= 5; frame++)
        {
            bool reordered = frame >= 4;
            bounds[reordered ? 1 : 0] = Face(frame * 30f);
            bounds[reordered ? 0 : 1] = Face(200f - frame * 30f);
            tracker.Update(bounds, 2, frame * 0.1f, 3f);
            Check(tracker.DetectionToSlot[reordered ? 1 : 0] == first, "Predicted right-moving track must retain its slot.");
            Check(tracker.DetectionToSlot[reordered ? 0 : 1] == second, "Predicted left-moving track must retain its slot.");
        }
    }

    static void RelativeSizeAndInvalidBoundsAreRejected()
    {
        var tracker = new FacelessTrackAssigner();
        var bounds = new[] { Face(100f), default(Rect), new Rect(float.NaN, 0f, 50f, 70f) };
        tracker.Update(bounds, 3, 0f, 3f);
        int smallSlot = tracker.DetectionToSlot[0];
        Check(tracker.DetectionToSlot[1] == -1 && tracker.DetectionToSlot[2] == -1, "Invalid rectangles must not create tracks.");
        bounds[0] = Face(100f, 100f, 300f, 500f);
        tracker.Update(bounds, 3, 0.033f, 3f);
        int largeSlot = tracker.DetectionToSlot[0];
        Check(largeSlot != smallSlot && tracker.SlotIsNew[largeSlot], "Unrelated scale at the same center must not inherit a track.");
        tracker.Update(bounds, 99, 0.066f, 3f);
        Check(tracker.DetectionToSlot[0] >= 0, "Oversized count must be bounded to the supplied array.");
        tracker.Update(bounds, -1, 0.1f, 3f);
        Check(tracker.DetectionToSlot[0] == -1, "Negative count is an empty update.");
    }

    static void ClockRestartAndResetCreateFreshState()
    {
        var tracker = new FacelessTrackAssigner();
        var bounds = new[] { Face(100f) };
        tracker.Update(bounds, 1, 10f, 3f);
        tracker.Update(bounds, 1, 0f, 3f);
        Check(tracker.SlotIsNew[tracker.DetectionToSlot[0]], "Clock restart must reset tracking state.");
        tracker.Reset();
        for (int i = 0; i < 6; i++)
            Check(tracker.TrackIds[i] == 0 && !tracker.SlotIsActive[i] && tracker.DetectionToSlot[i] == -1,
                "Explicit reset must clear every track and frame output.");
    }
}

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero { get { return new Vector2(0f, 0f); } }
        public float sqrMagnitude { get { return x * x + y * y; } }
        public float magnitude { get { return (float)Math.Sqrt(sqrMagnitude); } }
        public Vector2 normalized { get { return magnitude > 0f ? this / magnitude : zero; } }
        public static Vector2 operator +(Vector2 a, Vector2 b) { return new Vector2(a.x + b.x, a.y + b.y); }
        public static Vector2 operator -(Vector2 a, Vector2 b) { return new Vector2(a.x - b.x, a.y - b.y); }
        public static Vector2 operator *(Vector2 a, float value) { return new Vector2(a.x * value, a.y * value); }
        public static Vector2 operator /(Vector2 a, float value) { return new Vector2(a.x / value, a.y / value); }
        public static Vector2 Lerp(Vector2 a, Vector2 b, float t) { return a + (b - a) * t; }
    }

    public struct Rect
    {
        public float x, y, width, height;
        public Rect(float x, float y, float width, float height)
        { this.x = x; this.y = y; this.width = width; this.height = height; }
        public Vector2 center { get { return new Vector2(x + width * 0.5f, y + height * 0.5f); } }
    }

    public static class Mathf
    {
        public static float Max(float a, float b) { return Math.Max(a, b); }
        public static float Min(float a, float b) { return Math.Min(a, b); }
        public static float Sqrt(float value) { return (float)Math.Sqrt(value); }
        public static float Abs(float value) { return Math.Abs(value); }
        public static float Log(float value) { return (float)Math.Log(value); }
    }
}
