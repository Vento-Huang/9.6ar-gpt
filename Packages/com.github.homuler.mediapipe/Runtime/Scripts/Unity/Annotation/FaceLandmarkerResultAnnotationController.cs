// Copyright (c) 2023 homuler
//
// Use of this source code is governed by an MIT-style
// license that can be found in the LICENSE file or at
// https://opensource.org/licenses/MIT.

using UnityEngine;

using Mediapipe.Tasks.Vision.FaceLandmarker;

namespace Mediapipe.Unity
{
  public class FaceLandmarkerResultAnnotationController : AnnotationController<MultiFaceLandmarkListAnnotation>
  {
    [SerializeField] private bool _visualizeZ = false;

    private readonly object _currentTargetLock = new object();
    private FaceLandmarkerResult _currentTarget;

    // Main-thread snapshot for effects. It advances only after annotation transforms
    // have been drawn; consumers must not read mutable native callback results.
    public int ResultVersion { get; private set; }
    public float LastResultTime { get; private set; } = -100f;
    public bool HasFace { get; private set; }
    public bool HasFacePose { get; private set; }
    public Matrix4x4 FacePose { get; private set; } = Matrix4x4.identity;
    public int FaceCount { get; private set; }
    private const int MaxEffectFaces = 6;
    private readonly bool[] _validFaces = new bool[MaxEffectFaces];
    private readonly bool[] _validPoses = new bool[MaxEffectFaces];
    private readonly Matrix4x4[] _facePoses = new Matrix4x4[MaxEffectFaces];
    private readonly PointListAnnotation[] _pointLists = new PointListAnnotation[MaxEffectFaces];

    // Index belongs to this detection snapshot, NOT to a persistent person.
    // Use ListAnnotation's indexer rather than global object search order.
    public bool TryGetFace(int index, out PointListAnnotation points, out Matrix4x4 pose, out bool hasPose)
    {
      points = null; pose = Matrix4x4.identity; hasPose = false;
      if (index < 0 || index >= FaceCount || !_validFaces[index] || index >= annotation.count)
        return false;
      var group = annotation[index];
      if (group == null || !group.gameObject.activeInHierarchy) return false;
      if (_pointLists[index] == null || !_pointLists[index].transform.IsChildOf(group.transform))
      {
        var face = group.GetComponentInChildren<FaceLandmarkListAnnotation>(true);
        _pointLists[index] = face != null ? face.GetComponentInChildren<PointListAnnotation>(true) : null;
      }
      points = _pointLists[index];
      hasPose = _validPoses[index]; pose = _facePoses[index];
      return points != null && points.count >= 468 && points.gameObject.activeInHierarchy;
    }

    public void DrawNow(FaceLandmarkerResult target)
    {
      target.CloneTo(ref _currentTarget);
      SyncNow();
    }

    public void DrawLater(FaceLandmarkerResult target) => UpdateCurrentTarget(target);

    protected void UpdateCurrentTarget(FaceLandmarkerResult newTarget)
    {
      lock (_currentTargetLock)
      {
        newTarget.CloneTo(ref _currentTarget);
        isStale = true;
      }
    }

    protected override void SyncNow()
    {
      lock (_currentTargetLock)
      {
        isStale = false;
        annotation.Draw(_currentTarget.faceLandmarks, _visualizeZ);
        FaceCount = _currentTarget.faceLandmarks == null ? 0 : Mathf.Min(MaxEffectFaces, _currentTarget.faceLandmarks.Count);
        HasFace = HasFacePose = false;
        FacePose = Matrix4x4.identity;
        for (int i = 0; i < MaxEffectFaces; i++)
        {
          _validFaces[i] = i < FaceCount && _currentTarget.faceLandmarks[i].landmarks != null
            && _currentTarget.faceLandmarks[i].landmarks.Count >= 468;
          _validPoses[i] = _validFaces[i] && _currentTarget.facialTransformationMatrixes != null
            && i < _currentTarget.facialTransformationMatrixes.Count;
          _facePoses[i] = _validPoses[i] ? _currentTarget.facialTransformationMatrixes[i] : Matrix4x4.identity;
          if (!HasFace && _validFaces[i])
          {
            HasFace = true; HasFacePose = _validPoses[i]; FacePose = _facePoses[i];
          }
        }
        LastResultTime = Time.unscaledTime;
        unchecked { ResultVersion++; }
      }
    }
  }
}
