using System;
using UnityEngine;

/// <summary>
/// Associates up to six camera-space detections with temporary geometric tracks.
/// Detection order may change on every frame. This is not identity recognition:
/// indistinguishable crossings or complete occlusion can still exchange tracks.
/// Reuses all working arrays after construction.
/// </summary>
public sealed class FacelessTrackAssigner
{
    public const int Capacity = 6;
    const int AssignmentStates = 1 << Capacity;
    const float NewDetectionCost = 1.65f;

    // Read-only to callers. Update mutates the array contents in place.
    public readonly int[] DetectionToSlot = new int[Capacity];
    public readonly int[] TrackIds = new int[Capacity];
    public readonly bool[] SlotIsNew = new bool[Capacity];
    public readonly bool[] SlotIsVisible = new bool[Capacity];
    public readonly bool[] SlotIsActive = new bool[Capacity];
    public readonly Rect[] LastBounds = new Rect[Capacity];
    public readonly float[] LastSeen = new float[Capacity];

    readonly Vector2[] _velocity = new Vector2[Capacity];
    readonly int[] _validDetections = new int[Capacity];
    readonly float[,] _edgeCosts = new float[Capacity, Capacity];
    readonly float[,] _costs = new float[Capacity + 1, AssignmentStates];
    readonly int[,] _previousMask = new int[Capacity + 1, AssignmentStates];
    readonly int[,] _previousSlot = new int[Capacity + 1, AssignmentStates];
    int _nextTrackId = 1;
    float _lastUpdate;
    bool _hasUpdate;

    public FacelessTrackAssigner()
    {
        Reset();
    }

    /// <summary>
    /// Bounds must all use the same camera-pixel coordinate system. Only the
    /// first six detections are accepted; invalid/empty rectangles map to -1.
    /// SlotIsNew is true for one update when a slot is allocated or replaced:
    /// the caller must reset that slot's skin sampling, smoothing and entrance.
    /// Unseen tracks retain their IDs until forgetSeconds elapses, unless a
    /// newcomer needs their slot while every slot is occupied. Render visible
    /// slots only; an active but invisible slot holds state, not an old image.
    /// </summary>
    public void Update(Rect[] detectedBounds, int count, float now, float forgetSeconds)
    {
        if (!Finite(now)) throw new ArgumentOutOfRangeException("now");
        if (!Finite(forgetSeconds)) throw new ArgumentOutOfRangeException("forgetSeconds");
        forgetSeconds = Mathf.Max(0f, forgetSeconds);
        if (_hasUpdate && now < _lastUpdate) Reset();
        _lastUpdate = now;
        _hasUpdate = true;
        ClearFrameOutputs();

        for (int slot = 0; slot < Capacity; slot++)
            if (SlotIsActive[slot] && now - LastSeen[slot] > forgetSeconds)
                Retire(slot);

        count = detectedBounds == null ? 0 : Math.Min(Capacity, Math.Min(Math.Max(0, count), detectedBounds.Length));
        int validCount = 0;
        for (int detection = 0; detection < count; detection++)
        {
            if (!Valid(detectedBounds[detection])) continue;
            _validDetections[validCount++] = detection;
            for (int slot = 0; slot < Capacity; slot++)
                _edgeCosts[detection, slot] = SlotIsActive[slot]
                    ? MatchCost(slot, detectedBounds[detection], now)
                    : float.PositiveInfinity;
        }
        if (validCount == 0) return;

        // Small exact assignment: each bit represents an existing slot already
        // claimed by a detection. The unmatched option creates a new track.
        // Keeping this global avoids a greedy early detection stealing the only
        // viable match of a later detection when faces approach each other.
        for (int row = 0; row <= validCount; row++)
        for (int mask = 0; mask < AssignmentStates; mask++)
            _costs[row, mask] = float.PositiveInfinity;
        _costs[0, 0] = 0f;

        for (int row = 0; row < validCount; row++)
        {
            int detection = _validDetections[row];
            for (int mask = 0; mask < AssignmentStates; mask++)
            {
                float cost = _costs[row, mask];
                if (float.IsPositiveInfinity(cost)) continue;
                Consider(row + 1, mask, mask, -1, cost + NewDetectionCost);
                for (int slot = 0; slot < Capacity; slot++)
                {
                    int bit = 1 << slot;
                    if ((mask & bit) != 0) continue;
                    float edge = _edgeCosts[detection, slot];
                    if (float.IsPositiveInfinity(edge)) continue;
                    Consider(row + 1, mask | bit, mask, slot, cost + edge);
                }
            }
        }

        int bestMask = 0;
        for (int mask = 1; mask < AssignmentStates; mask++)
            if (_costs[validCount, mask] < _costs[validCount, bestMask]) bestMask = mask;

        for (int row = validCount; row > 0; row--)
        {
            int detection = _validDetections[row - 1];
            int slot = _previousSlot[row, bestMask];
            DetectionToSlot[detection] = slot;
            if (slot >= 0) SlotIsVisible[slot] = true;
            bestMask = _previousMask[row, bestMask];
        }

        // Reserve every existing match before allocating newcomers. Otherwise a
        // newcomer early in the detection array could replace a later match.
        for (int row = 0; row < validCount; row++)
        {
            int detection = _validDetections[row];
            int slot = DetectionToSlot[detection];
            if (slot >= 0) Observe(slot, detectedBounds[detection], now, false);
        }
        for (int row = 0; row < validCount; row++)
        {
            int detection = _validDetections[row];
            if (DetectionToSlot[detection] >= 0) continue;
            int slot = AvailableSlot();
            // At most six valid inputs: an unmatched input always has either
            // a free slot or an unseen slot available for replacement.
            DetectionToSlot[detection] = slot;
            Observe(slot, detectedBounds[detection], now, true);
        }
    }

    public void Reset()
    {
        for (int slot = 0; slot < Capacity; slot++) Retire(slot);
        ClearFrameOutputs();
        _nextTrackId = 1;
        _lastUpdate = 0f;
        _hasUpdate = false;
    }

    float MatchCost(int slot, Rect bounds, float now)
    {
        Rect previous = LastBounds[slot];
        float widthRatio = bounds.width / previous.width;
        float heightRatio = bounds.height / previous.height;
        // Permit moderate yaw/depth changes but reject unrelated large/small
        // faces. The limits are relative; they do not depend on camera size.
        if (widthRatio < 0.34f || widthRatio > 2.94f ||
            heightRatio < 0.43f || heightRatio > 2.33f)
            return float.PositiveInfinity;

        float elapsed = Mathf.Max(0f, now - LastSeen[slot]);
        float diagonal = Mathf.Sqrt(previous.width * previous.width + previous.height * previous.height);
        float newDiagonal = Mathf.Sqrt(bounds.width * bounds.width + bounds.height * bounds.height);
        float scale = Mathf.Max(1f, (diagonal + newDiagonal) * 0.5f);
        // Predict only a short distance into missing frames: indefinite linear
        // extrapolation would carry a lost face onto unrelated spectators.
        Vector2 predicted = previous.center + _velocity[slot] * Mathf.Min(elapsed, 0.25f);
        float distance = (bounds.center - predicted).magnitude / scale;
        float distanceGate = 0.8f + Mathf.Min(elapsed, 0.5f) * 0.7f;
        if (distance > distanceGate) return float.PositiveInfinity;

        float sizeCost = Mathf.Abs(Mathf.Log(widthRatio)) + Mathf.Abs(Mathf.Log(heightRatio));
        return 2f * distance * distance + 0.35f * sizeCost + 0.12f * Mathf.Min(elapsed, 3f);
    }

    void Consider(int row, int mask, int oldMask, int slot, float cost)
    {
        if (cost >= _costs[row, mask]) return;
        _costs[row, mask] = cost;
        _previousMask[row, mask] = oldMask;
        _previousSlot[row, mask] = slot;
    }

    int AvailableSlot()
    {
        for (int slot = 0; slot < Capacity; slot++)
            if (!SlotIsActive[slot]) return slot;

        int oldest = -1;
        for (int slot = 0; slot < Capacity; slot++)
            if (!SlotIsVisible[slot] && (oldest < 0 || LastSeen[slot] < LastSeen[oldest]))
                oldest = slot;
        return oldest;
    }

    void Observe(int slot, Rect bounds, float now, bool isNew)
    {
        if (isNew)
        {
            _velocity[slot] = Vector2.zero;
            TrackIds[slot] = NextId();
            SlotIsNew[slot] = true;
        }
        else
        {
            float elapsed = now - LastSeen[slot];
            if (elapsed > 0.4f) _velocity[slot] = Vector2.zero;
            else if (elapsed > 0.0001f)
            {
                Vector2 measured = (bounds.center - LastBounds[slot].center) / elapsed;
                float diagonal = Mathf.Sqrt(bounds.width * bounds.width + bounds.height * bounds.height);
                float maxSpeed = Mathf.Max(1f, diagonal * 6f);
                if (measured.sqrMagnitude > maxSpeed * maxSpeed)
                    measured = measured.normalized * maxSpeed;
                _velocity[slot] = Vector2.Lerp(_velocity[slot], measured, 0.5f);
            }
        }
        SlotIsActive[slot] = true;
        SlotIsVisible[slot] = true;
        LastBounds[slot] = bounds;
        LastSeen[slot] = now;
    }

    int NextId()
    {
        int candidate;
        bool used;
        do
        {
            candidate = _nextTrackId;
            _nextTrackId = _nextTrackId == int.MaxValue ? 1 : _nextTrackId + 1;
            used = false;
            for (int slot = 0; slot < Capacity; slot++)
                if (SlotIsActive[slot] && TrackIds[slot] == candidate) used = true;
        }
        while (used);
        return candidate;
    }

    void Retire(int slot)
    {
        TrackIds[slot] = 0;
        SlotIsActive[slot] = false;
        SlotIsVisible[slot] = false;
        SlotIsNew[slot] = false;
        LastBounds[slot] = default(Rect);
        LastSeen[slot] = 0f;
        _velocity[slot] = Vector2.zero;
    }

    void ClearFrameOutputs()
    {
        for (int slot = 0; slot < Capacity; slot++)
        {
            DetectionToSlot[slot] = -1;
            SlotIsVisible[slot] = false;
            SlotIsNew[slot] = false;
        }
    }

    static bool Valid(Rect value)
    {
        return Finite(value.x) && Finite(value.y) &&
            Finite(value.width) && Finite(value.height) &&
            value.width > 0.001f && value.height > 0.001f;
    }

    static bool Finite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
