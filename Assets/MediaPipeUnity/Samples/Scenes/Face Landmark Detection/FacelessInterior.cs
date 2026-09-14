using System;
using UnityEngine;

/// <summary>
/// Fills only enclosed gaps between the existing opaque feature-region cores.
/// The original analytic regions still supply every outside feather edge.
/// This landmark-only atlas never reads back camera pixels or grows a face oval.
/// </summary>
public sealed class FacelessInterior : IDisposable
{
    public const int Size = 256;
    const int CellCount = Size * Size;
    const byte Empty = 0, Solid = 1, Outside = 2, Hole = 3, InnerRing = 4, OuterRing = 5;
    readonly byte[] _cells = new byte[CellCount];
    readonly int[] _queue = new int[CellCount];
    readonly Color32[] _pixels = new Color32[CellCount];
    readonly Vector4[] _cachedCenters = new Vector4[FacelessRegions.Count];
    readonly Vector4[] _cachedAxes = new Vector4[FacelessRegions.Count];
    Vector2 _cachedOrigin, _cachedU, _cachedV;
    Texture2D _texture;
    bool _hasCachedFrame;

    public bool HasHoles { get; private set; }
    public Texture Texture { get { return HasHoles ? _texture : Texture2D.blackTexture; } }

    /// <summary>
    /// Call after FacelessRegions.Build and before reconstruction/compositing.
    /// Atlas UVs are identical to FacelessSkinCommon.PixelToAtlas. The returned
    /// texture must be shared by seed exclusion and the final coverage pass.
    /// Identical matched frames reuse the previous result without CPU work.
    /// </summary>
    public void Build(FacelessRegions regions)
    {
        if (regions == null || !Finite(regions.Origin) ||
            !Finite(regions.AxisU) || !Finite(regions.AxisV) ||
            regions.AxisU.sqrMagnitude < 0.0001f || regions.AxisV.sqrMagnitude < 0.0001f)
        {
            HasHoles = false;
            _hasCachedFrame = false;
            return;
        }
        if (Unchanged(regions)) return;

        Array.Clear(_cells, 0, CellCount);
        for (int region = 0; region < FacelessRegions.Count; region++)
            RasterizeCore(regions, region);
        MarkOutside();

        int head = 0, tail = 0;
        for (int i = 0; i < CellCount; i++)
        {
            if (_cells[i] != Empty) continue;
            _cells[i] = Hole;
            _queue[tail++] = i;
        }
        HasHoles = tail > 0;
        if (HasHoles)
        {
            // The two transition rings can enter only cells proven entirely
            // opaque. Keep a further zero-valued solid cell next to Outside:
            // bilinear interpolation must never reach the original feather.
            for (int ring = 0; ring < 2; ring++)
            {
                int frontierEnd = tail;
                byte nextState = ring == 0 ? InnerRing : OuterRing;
                while (head < frontierEnd)
                {
                    int cell = _queue[head++];
                    int x = cell % Size, y = cell / Size;
                    int minX = Math.Max(0, x - 1), maxX = Math.Min(Size - 1, x + 1);
                    int minY = Math.Max(0, y - 1), maxY = Math.Min(Size - 1, y + 1);
                    for (int ny = minY; ny <= maxY; ny++)
                    for (int nx = minX; nx <= maxX; nx++)
                    {
                        int neighbor = ny * Size + nx;
                        if (_cells[neighbor] != Solid || !SafeBilinearSupport(nx, ny)) continue;
                        _cells[neighbor] = nextState;
                        _queue[tail++] = neighbor;
                    }
                }
            }
            UploadIfChanged();
        }
        Cache(regions);
    }

    void RasterizeCore(FacelessRegions regions, int index)
    {
        Vector4 center = regions.Centers[index], axes = regions.Axes[index];
        if (!Finite(center) || !Finite(axes) || center.z <= 0 || center.w <= 0) return;
        Vector2 axisX = new Vector2(axes.x, axes.y);
        Vector2 axisY = new Vector2(-axes.y, axes.x);
        // Regions.Build creates orthogonal atlas axes, matching PixelToAtlas.
        float inverseU = 1f / regions.AxisU.sqrMagnitude;
        float inverseV = 1f / regions.AxisV.sqrMagnitude;
        Vector2 offset = new Vector2(center.x, center.y) - regions.Origin;
        float centerU = Vector2.Dot(offset, regions.AxisU) * inverseU + 0.5f;
        float centerV = Vector2.Dot(offset, regions.AxisV) * inverseV + 0.5f;
        float extentU = (Mathf.Abs(Vector2.Dot(axisX, regions.AxisU)) * center.z +
            Mathf.Abs(Vector2.Dot(axisY, regions.AxisU)) * center.w) * inverseU;
        float extentV = (Mathf.Abs(Vector2.Dot(axisX, regions.AxisV)) * center.z +
            Mathf.Abs(Vector2.Dot(axisY, regions.AxisV)) * center.w) * inverseV;
        int minX = Mathf.Max(0, Mathf.FloorToInt((centerU - extentU) * Size));
        int maxX = Mathf.Min(Size - 1, Mathf.CeilToInt((centerU + extentU) * Size));
        int minY = Mathf.Max(0, Mathf.FloorToInt((centerV - extentV) * Size));
        int maxY = Mathf.Min(Size - 1, Mathf.CeilToInt((centerV + extentV) * Size));
        if (minX > maxX || minY > maxY) return;

        Vector2 atlasStart = regions.Origin - (regions.AxisU + regions.AxisV) * 0.5f;
        Vector2 toStart = atlasStart - new Vector2(center.x, center.y);
        float qxStart = Vector2.Dot(toStart, axisX) / center.z;
        float qyStart = Vector2.Dot(toStart, axisY) / center.w;
        float qxStepX = Vector2.Dot(regions.AxisU, axisX) / (Size * center.z);
        float qyStepX = Vector2.Dot(regions.AxisU, axisY) / (Size * center.w);
        float qxStepY = Vector2.Dot(regions.AxisV, axisX) / (Size * center.z);
        float qyStepY = Vector2.Dot(regions.AxisV, axisY) / (Size * center.w);
        for (int y = minY; y <= maxY; y++)
        {
            float rowX = qxStart + y * qxStepY;
            float rowY = qyStart + y * qyStepY;
            for (int x = minX; x <= maxX; x++)
            {
                int cell = y * Size + x;
                if (_cells[cell] == Solid) continue;
                float qx = rowX + x * qxStepX, qy = rowY + x * qyStepX;
                // L4 regions are convex: all four corners in ONE region prove
                // the entire cell is opaque. A center-only raster can close a
                // real subpixel outside channel and falsely fill outer skin.
                if (InCore(qx, qy) &&
                    InCore(qx + qxStepX, qy + qyStepX) &&
                    InCore(qx + qxStepY, qy + qyStepY) &&
                    InCore(qx + qxStepX + qxStepY, qy + qyStepX + qyStepY))
                    _cells[cell] = Solid;
            }
        }
    }

    static bool InCore(float x, float y)
    {
        float xx = x * x, yy = y * y;
        return xx * xx + yy * yy <= 0.99999f;
    }

    void MarkOutside()
    {
        int head = 0, tail = 0;
        for (int i = 0; i < Size; i++)
        {
            EnqueueOutside(i, ref tail);
            EnqueueOutside((Size - 1) * Size + i, ref tail);
            EnqueueOutside(i * Size, ref tail);
            EnqueueOutside(i * Size + Size - 1, ref tail);
        }
        while (head < tail)
        {
            int cell = _queue[head++];
            int x = cell % Size, y = cell / Size;
            int minX = Math.Max(0, x - 1), maxX = Math.Min(Size - 1, x + 1);
            int minY = Math.Max(0, y - 1), maxY = Math.Min(Size - 1, y + 1);
            for (int ny = minY; ny <= maxY; ny++)
            for (int nx = minX; nx <= maxX; nx++)
                EnqueueOutside(ny * Size + nx, ref tail);
        }
    }

    void EnqueueOutside(int cell, ref int tail)
    {
        if (_cells[cell] != Empty) return;
        _cells[cell] = Outside;
        _queue[tail++] = cell;
    }

    bool SafeBilinearSupport(int x, int y)
    {
        if (x <= 0 || x >= Size - 1 || y <= 0 || y >= Size - 1) return false;
        for (int ny = y - 1; ny <= y + 1; ny++)
        for (int nx = x - 1; nx <= x + 1; nx++)
            if (_cells[ny * Size + nx] == Outside) return false;
        return true;
    }

    void UploadIfChanged()
    {
        bool changed = _texture == null;
        for (int i = 0; i < CellCount; i++)
        {
            byte state = _cells[i];
            byte value = state == Hole || state == InnerRing ? (byte)255 :
                state == OuterRing ? (byte)128 : (byte)0;
            if (_pixels[i].r != value) changed = true;
            _pixels[i] = new Color32(value, value, value, 255);
        }
        if (!changed) return;
        if (_texture == null)
        {
            _texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false, true)
            {
                name = "Faceless enclosed interior gaps",
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
        }
        _texture.SetPixels32(_pixels);
        _texture.Apply(false, false);
    }

    bool Unchanged(FacelessRegions regions)
    {
        if (!_hasCachedFrame || !_cachedOrigin.Equals(regions.Origin) ||
            !_cachedU.Equals(regions.AxisU) || !_cachedV.Equals(regions.AxisV)) return false;
        for (int i = 0; i < FacelessRegions.Count; i++)
            if (!_cachedCenters[i].Equals(regions.Centers[i]) ||
                !_cachedAxes[i].Equals(regions.Axes[i])) return false;
        return true;
    }

    void Cache(FacelessRegions regions)
    {
        _cachedOrigin = regions.Origin;
        _cachedU = regions.AxisU;
        _cachedV = regions.AxisV;
        Array.Copy(regions.Centers, _cachedCenters, FacelessRegions.Count);
        Array.Copy(regions.Axes, _cachedAxes, FacelessRegions.Count);
        _hasCachedFrame = true;
    }

    static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    static bool Finite(Vector2 value) { return Finite(value.x) && Finite(value.y); }
    static bool Finite(Vector4 value)
    {
        return Finite(value.x) && Finite(value.y) && Finite(value.z) && Finite(value.w);
    }

    public void Dispose()
    {
        HasHoles = false;
        _hasCachedFrame = false;
        if (_texture != null)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(_texture);
            else UnityEngine.Object.DestroyImmediate(_texture);
        }
        _texture = null;
    }
}
