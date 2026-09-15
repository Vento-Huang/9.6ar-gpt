// Copyright (c) 2023 homuler
//
// Use of this source code is governed by an MIT-style
// license that can be found in the LICENSE file or at
// https://opensource.org/licenses/MIT.

using System.Collections;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using UnityEngine;
using UnityEngine.Rendering;

namespace Mediapipe.Unity.Sample.FaceLandmarkDetection
{
  public class FaceLandmarkerRunner : VisionTaskApiRunner<FaceLandmarker>
  {
    [SerializeField] private FaceLandmarkerResultAnnotationController _faceLandmarkerResultAnnotationController;

    [Tooltip("Display each camera frame together with its detection result. Takes effect when the camera restarts.")]
    public bool synchronizePreviewWithDetection = true;

    public bool PreviewIsSynchronized => _synchronizeThisRun && _previewBuffers != null;
    public bool PreviewHasFace { get; private set; }
    public float LastMatchedFrameTime { get; private set; } = -100f;

    private Experimental.TextureFramePool _textureFramePool;
    private RenderTexture[] _previewBuffers;
    private Texture _originalPreviewTexture;
    private int _presentedBuffer;
    private bool _synchronizeThisRun, _restoreRunningMode;
    private Tasks.Vision.Core.RunningMode _originalRunningMode;

    public readonly FaceLandmarkDetectionConfig config = new FaceLandmarkDetectionConfig();

    public override void Stop()
    {
      ReleasePairedPreview();
      base.Stop();
      _textureFramePool?.Dispose();
      _textureFramePool = null;
      if (_restoreRunningMode && config.RunningMode == Tasks.Vision.Core.RunningMode.VIDEO)
        config.RunningMode = _originalRunningMode;
      _restoreRunningMode = false;
      _synchronizeThisRun = false;
    }

    protected override IEnumerator Run()
    {
      _synchronizeThisRun = synchronizePreviewWithDetection;
      if (_synchronizeThisRun)
      {
        // LIVE_STREAM callbacks can arrive after the webcam has already moved
        // to another frame. VIDEO still processes a continuous camera stream,
        // but lets us publish the pixels and landmarks as one matched pair.
        _originalRunningMode = config.RunningMode;
        _restoreRunningMode = true;
        config.RunningMode = Tasks.Vision.Core.RunningMode.VIDEO;
      }
      Debug.Log($"Delegate = {config.Delegate}");
      Debug.Log($"Image Read Mode = {config.ImageReadMode}");
      Debug.Log($"Running Mode = {config.RunningMode}");
      Debug.Log($"NumFaces = {config.NumFaces}");
      Debug.Log($"MinFaceDetectionConfidence = {config.MinFaceDetectionConfidence}");
      Debug.Log($"MinFacePresenceConfidence = {config.MinFacePresenceConfidence}");
      Debug.Log($"MinTrackingConfidence = {config.MinTrackingConfidence}");
      Debug.Log($"OutputFaceBlendshapes = {config.OutputFaceBlendshapes}");
      Debug.Log($"OutputFacialTransformationMatrixes = {config.OutputFacialTransformationMatrixes}");

      yield return AssetLoader.PrepareAssetAsync(config.ModelPath);

      var options = config.GetFaceLandmarkerOptions(config.RunningMode == Tasks.Vision.Core.RunningMode.LIVE_STREAM ? OnFaceLandmarkDetectionOutput : null);
      taskApi = FaceLandmarker.CreateFromOptions(options, GpuManager.GpuResources);
      var imageSource = ImageSourceProvider.ImageSource;

      yield return imageSource.Play();

      if (!imageSource.isPrepared)
      {
        Debug.LogError("Failed to start ImageSource, exiting...");
        yield break;
      }

      // Use RGBA32 as the input format.
      // TODO: When using GpuBuffer, MediaPipe assumes that the input format is BGRA, so maybe the following code needs to be fixed.
      _textureFramePool = new Experimental.TextureFramePool(imageSource.textureWidth, imageSource.textureHeight, TextureFormat.RGBA32, 10);

      // NOTE: The screen will be resized later, keeping the aspect ratio.
      screen.Initialize(imageSource);
      if (_synchronizeThisRun)
      {
        InitializePairedPreview(imageSource.textureWidth, imageSource.textureHeight);
      }

      SetupAnnotationController(_faceLandmarkerResultAnnotationController, imageSource);

      var transformationOptions = imageSource.GetTransformationOptions();
      var flipHorizontally = transformationOptions.flipHorizontally;
      var flipVertically = transformationOptions.flipVertically;
      var imageProcessingOptions = new Tasks.Vision.Core.ImageProcessingOptions(rotationDegrees: (int)transformationOptions.rotationAngle);

      AsyncGPUReadbackRequest req = default;
      var waitUntilReqDone = new WaitUntil(() => req.done);
      var waitForEndOfFrame = new WaitForEndOfFrame();
      var result = FaceLandmarkerResult.Alloc(options.numFaces);

      // NOTE: we can share the GL context of the render thread with MediaPipe (for now, only on Android)
      var canUseGpuImage = SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3 && GpuManager.GpuResources != null;
      using var glContext = canUseGpuImage ? GpuManager.GetGlContext() : null;

      while (true)
      {
        if (isPaused)
        {
          yield return new WaitWhile(() => isPaused);
        }

        if (!_textureFramePool.TryGetTextureFrame(out var textureFrame))
        {
          yield return null;
          continue;
        }

        // CPU readback used to wait here, after obtaining a live webcam
        // reference. Capture only after that wait so the owned pixels are fresh.
        if (config.ImageReadMode == ImageReadMode.CPU)
          yield return waitForEndOfFrame;

        Texture inputTexture = imageSource.GetCurrentTexture();
        int captureIndex = -1;
        if (PreviewIsSynchronized)
        {
          captureIndex = 1 - _presentedBuffer;
          // Preserve RAW webcam orientation. Both the detector's existing flip
          // options and the preview's existing uvRect are left intact.
          Graphics.Blit(inputTexture, _previewBuffers[captureIndex]);
          inputTexture = _previewBuffers[captureIndex];
        }
        long captureTimestamp = GetCurrentTimestampMillisec();

        // Build the input Image from the owned capture, never from the texture
        // currently on screen. Readback may yield while the last pair is shown.
        Image image;
        switch (config.ImageReadMode)
        {
          case ImageReadMode.GPU:
            if (!canUseGpuImage)
            {
              throw new System.Exception("ImageReadMode.GPU is not supported");
            }
            textureFrame.ReadTextureOnGPU(inputTexture, flipHorizontally, flipVertically);
            image = textureFrame.BuildGPUImage(glContext);
            // TODO: Currently we wait here for one frame to make sure the texture is fully copied to the TextureFrame before sending it to MediaPipe.
            // This usually works but is not guaranteed. Find a proper way to do this. See: https://github.com/homuler/MediaPipeUnityPlugin/pull/1311
            yield return waitForEndOfFrame;
            break;
          case ImageReadMode.CPU:
            textureFrame.ReadTextureOnCPU(inputTexture, flipHorizontally, flipVertically);
            image = textureFrame.BuildCPUImage();
            textureFrame.Release();
            break;
          case ImageReadMode.CPUAsync:
          default:
            req = textureFrame.ReadTextureAsync(inputTexture, flipHorizontally, flipVertically);
            yield return waitUntilReqDone;

            if (req.hasError)
            {
              // The failed request still owns a pooled frame. Without releasing
              // it, repeated failures exhaust all ten frames and stop tracking.
              textureFrame.Release();
              if (PreviewIsSynchronized) PreviewHasFace = false;
              config.ImageReadMode = ImageReadMode.CPU;
              Debug.LogWarning("FaceLandmarker: asynchronous camera readback failed. Switching to synchronous CPU readback.");
              yield return null;
              continue;
            }
            image = textureFrame.BuildCPUImage();
            textureFrame.Release();
            break;
        }

        bool disposeMatchedImage = PreviewIsSynchronized
          && taskApi.runningMode != Tasks.Vision.Core.RunningMode.LIVE_STREAM;
        try
        {
          switch (taskApi.runningMode)
          {
            case Tasks.Vision.Core.RunningMode.IMAGE:
              PublishDetection(taskApi.TryDetect(image, imageProcessingOptions, ref result), result, captureIndex);
              break;
            case Tasks.Vision.Core.RunningMode.VIDEO:
              PublishDetection(taskApi.TryDetectForVideo(image, captureTimestamp, imageProcessingOptions, ref result), result, captureIndex);
              break;
            case Tasks.Vision.Core.RunningMode.LIVE_STREAM:
              taskApi.DetectAsync(image, captureTimestamp, imageProcessingOptions);
              break;
          }
        }
        finally
        {
          // Synchronous inference has finished reading this image. Releasing
          // its native owner also returns GPU TextureFrames via their existing
          // callback; waiting for GC can otherwise exhaust the ten-frame pool.
          // Keep the opt-out LIVE_STREAM ownership behavior unchanged.
          if (disposeMatchedImage) image.Dispose();
        }
      }
    }

    void InitializePairedPreview(int width, int height)
    {
      _originalPreviewTexture = screen.texture;
      _previewBuffers = new RenderTexture[2];
      var previous = RenderTexture.active;
      try
      {
        for (int i = 0; i < _previewBuffers.Length; i++)
        {
          var buffer = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
          {
            name = "Face camera pair " + i,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            useMipMap = false,
            autoGenerateMips = false,
            hideFlags = HideFlags.HideAndDontSave
          };
          _previewBuffers[i] = buffer;
          if (!buffer.Create())
            throw new System.InvalidOperationException("Could not allocate the paired camera preview.");
          RenderTexture.active = buffer;
          GL.Clear(false, true, UnityEngine.Color.black);
        }
      }
      catch
      {
        ReleasePairedPreview();
        throw;
      }
      finally { RenderTexture.active = previous; }
      _presentedBuffer = 0;
      PreviewHasFace = false;
      LastMatchedFrameTime = -100f;
      screen.texture = _previewBuffers[_presentedBuffer];
    }

    void PublishDetection(bool succeeded, FaceLandmarkerResult result, int captureIndex)
    {
      if (!PreviewIsSynchronized)
      {
        _faceLandmarkerResultAnnotationController.DrawNow(succeeded ? result : default);
        return;
      }

      PreviewHasFace = false;
      if (succeeded && result.faceLandmarks != null)
      {
        for (int i = 0; i < Mathf.Min(config.NumFaces, result.faceLandmarks.Count); i++)
          if (result.faceLandmarks[i].landmarks != null && result.faceLandmarks[i].landmarks.Count >= 468)
          { PreviewHasFace = true; break; }
      }
      if (!PreviewHasFace)
      {
        // Keep the last matching pixels AND geometry. The effect controller
        // can hold briefly then fade to black without revealing raw features.
        return;
      }
      _faceLandmarkerResultAnnotationController.DrawNow(result);
      _presentedBuffer = captureIndex;
      screen.texture = _previewBuffers[_presentedBuffer];
      LastMatchedFrameTime = Time.unscaledTime;
    }

    void ReleasePairedPreview()
    {
      if (_previewBuffers != null)
      {
        if (screen != null && (screen.texture == _previewBuffers[0] || screen.texture == _previewBuffers[1]))
          screen.texture = _originalPreviewTexture;
        foreach (var buffer in _previewBuffers)
        {
          if (buffer == null) continue;
          buffer.Release();
          Destroy(buffer);
        }
      }
      _previewBuffers = null;
      _originalPreviewTexture = null;
      PreviewHasFace = false;
      LastMatchedFrameTime = -100f;
    }

    private void OnFaceLandmarkDetectionOutput(FaceLandmarkerResult result, Image image, long timestamp)
    {
      _faceLandmarkerResultAnnotationController.DrawLater(result);
    }
  }
}
