using UnityEngine;

/// <summary>
/// Fifteen local regions in camera-texture pixels. The face contour is ONLY a
/// safety boundary: it never supplies the positive coverage of the effect.
/// </summary>
public sealed class FacelessRegions
{
    public const int Count = 15;
    // Upper, middle and lower cheek on each side. The old 117/346 patches
    // overlapped the eye-region core, so they could not provide clean skin.
    static readonly int[] DonorIndices = {123,187,192,352,411,416};
    public static readonly int[] BoundaryIndices =
    {
        10,338,297,332,284,251,389,356,454,323,361,288,397,365,379,378,400,377,
        152,148,176,149,150,136,172,58,132,93,234,127,162,21,54,103,67,109
    };
    static readonly int[][] FeatureIndices =
    {
        new[] {33,7,163,144,145,153,154,155,133,246,161,160,159,158,157,173,
            46,53,52,65,55,70,63,105,66,107,111,118,119,120,121,128},
        new[] {263,249,390,373,374,380,381,382,362,466,388,387,386,385,384,398,
            276,283,282,295,285,300,293,334,296,336,340,347,348,349,350,357},
        new[] {8,9,55,285,168,6,197,195,5},
        new[] {1,2,4,5,19,94,98,327,97,326,203,423},
        new[] {2,164,0,37,267},
        new[] {61,146,91,181,84,17,314,405,321,375,291,185,40,39,37,0,267,
            269,270,409,78,95,88,178,87,14,317,402,318,324,308,191,80,81,82,
            13,312,311,310,415},
        new[] {18,200,199,175},
        // Nasolabial folds: nose wing through the cheek-side crease to lip corner.
        // These include the dark crease itself, so reconstruction cannot copy it.
        new[] {98,203,206,216,165,92,186,57,61},
        new[] {327,423,426,436,391,322,410,287,291},
        // Marionette folds: lip corner down the central lower cheek, stopping
        // above the chin perimeter. Jaw angles and outer cheek are not anchors.
        new[] {61,57,43,202,106,204,211,194},
        new[] {291,287,273,422,335,424,431,418},
        // Inner cheek highlights between the lower eye and nasolabial regions.
        // Keep the clean outer cheek donors and the face perimeter available.
        new[] {118,119,100,101,50,205,36},
        new[] {347,348,329,330,280,425,266},
        // The two lower chin highlights, inside the jaw's visible edge. These
        // bridge the central chin to its side transitions, not the jaw angles.
        new[] {32,208,140},
        new[] {262,428,369}
    };
    public readonly Vector4[] Centers = new Vector4[Count]; // xy center, zw core half-size
    public readonly Vector4[] Axes = new Vector4[Count]; // xy local X, z feather (pixels)
    public readonly Vector4[] Boundary = new Vector4[36];
    public readonly Vector4[] Donors = new Vector4[6];
    public Vector2 Origin, AxisU, AxisV;
    public float Width, Height, TopLimit;
    public Rect Bounds;

    public bool Build(Vector2[] p, float scale, float featherFraction)
    {
        Height = Vector2.Distance(p[10], p[152]);
        if (Height < 24) return false;
        Vector2 up = (p[10] - p[152]).normalized;
        Vector2 right = new Vector2(up.y, -up.x);
        if (Vector2.Dot(right, p[454] - p[234]) < 0) right = -right;
        // At profile the two anatomical cheeks can project onto each other.
        // Fit the reconstruction atlas to ALL projected vertices, including
        // the nose/lips. This resizes only a texture workspace, never a mask.
        Vector2 frameMin = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 frameMax = new Vector2(float.MinValue, float.MinValue);
        for (int i = 0; i < 468; i++)
        {
            Vector2 q = new Vector2(Vector2.Dot(p[i], right), Vector2.Dot(p[i], up));
            frameMin = Vector2.Min(frameMin, q); frameMax = Vector2.Max(frameMax, q);
        }
        Width = Mathf.Max(frameMax.x - frameMin.x, Height * 0.18f);
        Vector2 frameCenter = (frameMin + frameMax) * 0.5f;
        Origin = right * frameCenter.x + up * frameCenter.y;
        AxisU = right * (Width * 1.25f);
        AxisV = up * (Mathf.Max(Height, frameMax.y - frameMin.y) * 1.16f);
        for (int i = 0; i < 36; i++)
        {
            Vector2 b = p[BoundaryIndices[i]];
            Boundary[i] = new Vector4(b.x, b.y, 0, 0);
        }
        Vector2 min = p[0], max = p[0];
        for (int i = 0; i < 468; i++) { min = Vector2.Min(min, p[i]); max = Vector2.Max(max, p[i]); }
        Bounds = Rect.MinMaxRect(min.x, min.y, max.x, max.y);

        Fit(0, p, p[133] - p[33], scale, featherFraction);
        Fit(1, p, p[263] - p[362], scale, featherFraction);
        Fit(2, p, right, scale, featherFraction);
        Fit(3, p, p[327] - p[98], scale, featherFraction);
        Fit(4, p, p[291] - p[61], scale, featherFraction);
        Fit(5, p, p[291] - p[61], scale, featherFraction);
        Fit(6, p, right, scale, featherFraction);
        FitFold(7, p, p[57] - p[203], scale, featherFraction);
        FitFold(8, p, p[287] - p[423], scale, featherFraction);
        FitFold(9, p, p[211] - p[61], scale, featherFraction);
        FitFold(10, p, p[431] - p[291], scale, featherFraction);
        Fit(11, p, p[100] - p[50], scale, featherFraction);
        Fit(12, p, p[329] - p[280], scale, featherFraction);
        Fit(13, p, p[208] - p[140], scale, featherFraction);
        Fit(14, p, p[428] - p[369], scale, featherFraction);

        // Symmetric cheek patches. Avoid nose folds, lips and the forehead/hair.
        for (int i = 0; i < 6; i++)
            Donors[i] = new Vector4(p[DonorIndices[i]].x, p[DonorIndices[i]].y, Width * 0.016f, 0);
        // Keep a real-skin source band above the brow's soft edge; the old
        // brow+3.5% limit removed those neighbors and left a flat upper seam.
        TopLimit = Mathf.Min(Vector2.Dot(p[10] - Origin, up) - Height * 0.05f,
            Mathf.Max(Vector2.Dot(p[105] - Origin, up),
                Vector2.Dot(p[334] - Origin, up)) + Height * 0.12f);
        return true;
    }

    void FitFold(int index, Vector2[] p, Vector2 alongCrease, float scale, float feather)
    {
        // Use this crease's own projected long axis. The short axis controls
        // feather width, so the far-side fold contracts on head turns too.
        Fit(index, p, new Vector2(alongCrease.y, -alongCrease.x), scale, feather);
    }

    void Fit(int index, Vector2[] p, Vector2 horizontal, float scale, float feather)
    {
        Vector2 x = horizontal.sqrMagnitude > 0.01f ? horizontal.normalized : AxisU.normalized;
        Vector2 y = new Vector2(-x.y, x.x);
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);
        foreach (int id in FeatureIndices[index])
        {
            Vector2 q = new Vector2(Vector2.Dot(p[id], x), Vector2.Dot(p[id], y));
            min = Vector2.Min(min, q); max = Vector2.Max(max, q);
        }
        Vector2 c = (min + max) * 0.5f;
        Vector2 r = (max - min) * 0.5f;
        // The core must contain the whole brow and OUTER lip, even with closed
        // eyes/mouth. Core width does not depend on eyelid or lip opening alone.
        // Lower chin skin sits close to the real silhouette. Give those two
        // patches their own padding instead of growing the existing face mask.
        r += Vector2.one * (Width * (index >= 13 ? 0.020f : 0.022f));
        r.x = Mathf.Max(r.x, Width * (index == 2 ? 0.060f : index == 6 ? 0.095f : 0.018f));
        r.y = Mathf.Max(r.y, Height * (index == 4 ? 0.052f : 0.015f));
        // Enclose every source landmark in a rounded rectangle (L4 norm).
        float enclosure = 1f;
        foreach (int id in FeatureIndices[index])
        {
            float qx = Mathf.Abs(Vector2.Dot(p[id], x) - c.x) / r.x;
            float qy = Mathf.Abs(Vector2.Dot(p[id], y) - c.y) / r.y;
            enclosure = Mathf.Max(enclosure, Mathf.Pow(qx*qx*qx*qx + qy*qy*qy*qy, 0.25f) * 1.08f);
        }
        r *= enclosure * scale;
        if (index == 5) r.x *= 1.10f; // include the crease immediately beside lip corners
        Vector2 center = x * c.x + y * c.y;
        Centers[index] = new Vector4(center.x, center.y, r.x, r.y);
        // Far-side regions contract with their OWN projected width on head turns.
        float localEdge = index >= 13 ? Mathf.Min(r.x, r.y) * 0.45f : r.x * 0.75f;
        float edge = Mathf.Min(Width * feather, Mathf.Max(Width * 0.013f, localEdge));
        Axes[index] = new Vector4(x.x, x.y, edge, 0);
    }

    public void SetMaterial(Material m)
    {
        m.SetVectorArray("_Regions", Centers);
        m.SetVectorArray("_RegionAxes", Axes);
        m.SetVectorArray("_Boundary", Boundary);
        m.SetVectorArray("_Donors", Donors);
        m.SetVector("_FrameOrigin", new Vector4(Origin.x, Origin.y, Width, Height));
        m.SetVector("_FrameU", new Vector4(AxisU.x, AxisU.y, 0, 0));
        m.SetVector("_FrameV", new Vector4(AxisV.x, AxisV.y, TopLimit, 0));
        m.SetFloat("_ContourInset", Width * 0.018f);
        m.SetVector("_FaceBounds", new Vector4(Bounds.xMin, Bounds.yMin, Bounds.xMax, Bounds.yMax));
    }
}
