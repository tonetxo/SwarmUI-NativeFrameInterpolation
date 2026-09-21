using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using FreneticUtilities.FreneticExtensions;
using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Backends;
using SwarmUI.Builtin_ComfyUIBackend;
using SwarmUI.Core;
using SwarmUI.Media;
using SwarmUI.Text2Image;
using SwarmUI.Utils;

namespace NativeFrameInterpolation;

public class NativeFrameInterpolationExtension : Extension
{
    public static T2IParamGroup NativeVFIGroup;

    public static T2IRegisteredParam<string> NativeVFIModel;
    public static T2IRegisteredParam<int> NativeVFIMultiplier;
    public static T2IRegisteredParam<bool> NativeVFIAdjustFps;
    public static T2IRegisteredParam<string> NativeVFIVideoFile;

    public override void OnInit()
    {
        Logs.Init("Native Frame Interpolation Extension initializing...");

        // Map ComfyUI's built-in frame interpolation nodes to our feature flag
        ComfyUIBackendExtension.NodeToFeatureMap["FrameInterpolationModelLoader"] = "native_frame_interp";
        ComfyUIBackendExtension.NodeToFeatureMap["FrameInterpolate"] = "native_frame_interp";

        // Register the JS file for the history / media button
        ScriptFiles.Add("assets/native_vfi.js");

        RegisterParameters();

        // Priority -1: Video file interpolation from history (creates standalone workflow)
        WorkflowGenerator.AddStep(GenerateNativeVFIVideoFileWorkflow, -1);

        // Priority 80: Runs during video generation after decode & cleanups (priority 50),
        // and before RTX Video Upscale (priority 90) and True Final Save (priority 100).
        WorkflowGenerator.AddStep(GenerateNativeVFIWorkflow, 80);

        Logs.Init("Native Frame Interpolation Extension loaded successfully.");
    }

    private static void RegisterParameters()
    {
        NativeVFIGroup = new(
            Name: "Native Frame Interpolation",
            Description: "Interpolate video frames using ComfyUI's native frame interpolation engine (comfy_extras).\n" +
                         "Faster than legacy VFI and supports modern safetensors models (RIFE 4.26, 4.25, etc.).\n" +
                         "Can be enabled for generations or triggered on existing videos via the 'VFI Interpolate' button.",
            Toggles: true,
            Open: false,
            OrderPriority: 8.5,
            IsAdvanced: false
        );

        double priority = 0;

        NativeVFIModel = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Native VFI Model",
            Description: "Select which frame interpolation model to use from 'models/frame_interpolation'.\n" +
                         "e.g. 'rife_v4.26.safetensors' (recommended for speed and motion quality).",
            Default: "rife_v4.26.safetensors",
            Group: NativeVFIGroup,
            OrderPriority: priority++,
            FeatureFlag: "native_frame_interp",
            GetValues: _ => GetModelDropdownValues()
        ));

        NativeVFIMultiplier = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Native VFI Multiplier",
            Description: "Frame count multiplier.\n" +
                         "2 doubles the frame rate (e.g. 16 -> 32 fps), 3 triples it, etc.",
            Default: "2",
            Min: 2,
            Max: 16,
            Step: 1,
            ViewType: ParamViewType.SLIDER,
            Group: NativeVFIGroup,
            OrderPriority: priority++,
            FeatureFlag: "native_frame_interp"
        ));

        NativeVFIAdjustFps = T2IParamTypes.Register<bool>(new T2IParamType(
            Name: "Native VFI Adjust FPS",
            Description: "When enabled, multiplies video FPS to preserve original playback speed with maximum smoothness.\n" +
                         "When disabled, the video plays at the original FPS in slow-motion.",
            Default: "true",
            Group: NativeVFIGroup,
            OrderPriority: priority++,
            FeatureFlag: "native_frame_interp"
        ));

        NativeVFIVideoFile = T2IParamTypes.Register<string>(new(
            "Native VFI Video File",
            "Internal parameter for video file interpolation from history.",
            "",
            FeatureFlag: "native_frame_interp",
            ChangeWeight: 2,
            VisibleNormally: false
        ));
    }

    /// <summary>Scans ComfyUI's models/frame_interpolation directories for available models.</summary>
    public static List<string> GetModelDropdownValues()
    {
        HashSet<string> models = new(StringComparer.OrdinalIgnoreCase);

        List<string> candidateDirs =
        [
            Path.Combine(Program.ServerSettings.Paths.ActualModelRoot, "frame_interpolation"),
            Path.Combine(Environment.CurrentDirectory, "dlbackend/ComfyUI/models/frame_interpolation")
        ];

        foreach (var (_, data) in Program.Backends.AllBackends)
        {
            if (data.AbstractBackend is ComfyUISelfStartBackend backend)
            {
                candidateDirs.Add(Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, backend.ComfyPathBase, "models/frame_interpolation"));
            }
        }

        string[] validExtensions = [".safetensors", ".pth", ".pt", ".bin", ".onnx"];

        foreach (string dir in candidateDirs.Distinct())
        {
            if (Directory.Exists(dir))
            {
                try
                {
                    foreach (string file in Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(file).ToLowerInvariant();
                        if (validExtensions.Contains(ext))
                        {
                            string rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                            models.Add(rel);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logs.Verbose($"Native VFI: Error scanning directory '{dir}': {ex.Message}");
                }
            }
        }

        List<string> result = models
            .OrderByDescending(m => m.Contains("4.26"))
            .ThenByDescending(m => m.Contains("4.25"))
            .ThenBy(m => m)
            .ToList();

        if (result.Count == 0)
        {
            result.Add("rife_v4.26.safetensors");
        }

        return result;
    }

    /// <summary>Generates a complete workflow to interpolate an existing video file from history.</summary>
    public static void GenerateNativeVFIVideoFileWorkflow(WorkflowGenerator g)
    {
        if (!g.UserInput.TryGet(NativeVFIVideoFile, out string videoFile) || string.IsNullOrEmpty(videoFile))
        {
            return;
        }

        Logs.Info($"Native VFI: Processing video file '{videoFile}'");

        if (!g.Features.Contains("native_frame_interp"))
        {
            throw new SwarmUserErrorException("Native Frame Interpolation requires ComfyUI's native frame interpolation nodes.");
        }

        if (!g.Features.Contains("comfy_loadimage_b64") || WorkflowGenerator.RestrictCustomNodes)
        {
            throw new SwarmUserErrorException("Native Frame Interpolation requires SwarmUI's ComfyUI backend nodes (comfy_loadimage_b64) and cannot run with restricted custom nodes.");
        }

        if (videoFile.StartsWith("~/"))
        {
            videoFile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + videoFile[1..];
        }

        User user = g.UserInput.SourceSession?.User;
        if (user is not null)
        {
            string relativePath = null;
            if (videoFile.StartsWith("Output/"))
            {
                relativePath = videoFile["Output/".Length..];
            }
            else if (videoFile.StartsWith("View/"))
            {
                string afterView = videoFile["View/".Length..];
                int slashIndex = afterView.IndexOf('/');
                if (slashIndex > 0)
                {
                    relativePath = afterView[(slashIndex + 1)..];
                }
            }

            if (relativePath is not null)
            {
                string root = Utilities.CombinePathWithAbsolute(Environment.CurrentDirectory, user.OutputDirectory);
                videoFile = UserImageHistoryHelper.GetRealPathFor(user, $"{root}/{relativePath}", root: root);
            }
        }

        if (!File.Exists(videoFile))
        {
            throw new SwarmUserErrorException($"Native VFI: Video file not found: {videoFile}");
        }

        VideoFile sourceVideo;
        try
        {
            byte[] rawVideoData = File.ReadAllBytes(videoFile);
            sourceVideo = new VideoFile(rawVideoData, MediaType.GetByExtension(videoFile.AfterLast('.').ToLowerFast()));
        }
        catch (Exception ex)
        {
            throw new SwarmUserErrorException($"Native VFI: Could not read video: {ex.Message}");
        }

        string modelName = g.UserInput.Get(NativeVFIModel, "rife_v4.26.safetensors");
        modelName = modelName.Before("///").Trim();
        int multiplier = g.UserInput.Get(NativeVFIMultiplier, 2);
        bool adjustFps = g.UserInput.Get(NativeVFIAdjustFps, true);

        Logs.Info($"Native VFI File Mode: model={modelName}, multiplier={multiplier}, adjustFps={adjustFps}");

        // 1. Load video via SwarmUI base64 helper
        WGNodeData videoData = g.LoadVideo(sourceVideo, "${vfi_source_video}", false);
        JArray loadedVideoFrames = videoData.Path;
        JArray videoAudio = videoData.AttachedAudio?.Path;
        JToken videoFps = videoData.FPS;

        // 2. Load frame interpolation model
        string loaderNode = g.CreateNode("FrameInterpolationModelLoader", new JObject()
        {
            ["model_name"] = modelName
        });

        // 3. Interpolate frames
        string interpNode = g.CreateNode("FrameInterpolate", new JObject()
        {
            ["interp_model"] = new JArray() { loaderNode, 0 },
            ["images"] = loadedVideoFrames,
            ["multiplier"] = multiplier
        });
        JArray interpolatedFrames = new JArray() { interpNode, 0 };

        // 4. Calculate final FPS
        JToken finalFps = videoFps;
        if (adjustFps && multiplier > 1)
        {
            string multNode = g.CreateNode("CM_FloatBinaryOperation", new JObject()
            {
                ["op"] = "Mul",
                ["a"] = videoFps,
                ["b"] = (double)multiplier
            });
            finalFps = new JArray() { multNode, 0 };
        }

        // 5. CreateVideo with interpolated frames, original audio and scaled FPS
        string createVideoNode = g.CreateNode("CreateVideo", new JObject()
        {
            ["images"] = interpolatedFrames,
            ["audio"] = videoAudio,
            ["fps"] = finalFps
        });

        // 6. SaveVideo
        string swarmVideoFormat = g.UserInput.Get(T2IParamTypes.VideoFormat, "h264-mp4");
        string container = "mp4";
        string codec = "auto";
        if (swarmVideoFormat.Contains("-"))
        {
            string[] parts = swarmVideoFormat.Split('-');
            codec = parts[0];
            container = parts[1];
        }
        else
        {
            container = swarmVideoFormat;
        }
        if (container != "mp4" && container != "auto")
        {
            container = "mp4";
        }
        if (codec != "h264" && codec != "auto")
        {
            codec = "auto";
        }
        g.CreateNode("SaveVideo", new JObject()
        {
            ["video"] = new JArray() { createVideoNode, 0 },
            ["filename_prefix"] = "video/VFI_interpolated",
            ["format"] = container,
            ["codec"] = codec
        });

        // Skip all further generation steps
        g.CurrentMedia = new WGNodeData(interpolatedFrames, g, WGNodeData.DT_IMAGE, g.CurrentCompat());
        g.SkipFurtherSteps = true;

        Logs.Info($"Native VFI: Complete workflow created - loading '{videoFile}', interpolating {multiplier}x ({modelName}), saving as {codec}-{container}");
    }

    /// <summary>Returns true if the current generation request produces video output.</summary>
    private static bool IsVideoGenerationRequest(WorkflowGenerator g)
    {
        if (g.IsVideoModel())
        {
            return true;
        }
        if (g.UserInput.TryGet(T2IParamTypes.VideoModel, out _))
        {
            return true;
        }
        if (g.UserInput.Get(T2IParamTypes.Prompt, "").Contains("<extend:"))
        {
            return true;
        }
        if (g.CurrentMedia is not null && (g.CurrentMedia.DataType == WGNodeData.DT_VIDEO || g.CurrentMedia.DataType == WGNodeData.DT_LATENT_VIDEO || g.CurrentMedia.DataType == WGNodeData.DT_LATENT_AUDIOVIDEO))
        {
            return true;
        }
        return false;
    }

    /// <summary>Generates the ComfyUI native frame interpolation workflow steps during live generation.</summary>
    public static void GenerateNativeVFIWorkflow(WorkflowGenerator g)
    {
        // Skip if video file mode is active
        if (g.UserInput.TryGet(NativeVFIVideoFile, out string videoFile) && !string.IsNullOrEmpty(videoFile))
        {
            return;
        }

        if (!IsVideoGenerationRequest(g))
        {
            return;
        }

        // Only activate if group is enabled or user specified multiplier
        if (!g.UserInput.TryGet(NativeVFIModel, out _) && !g.UserInput.TryGet(NativeVFIMultiplier, out _))
        {
            return;
        }

        if (g.CurrentMedia is null)
        {
            return;
        }

        int multiplier = g.UserInput.Get(NativeVFIMultiplier, 2);
        if (multiplier < 2)
        {
            return;
        }

        string modelName = g.UserInput.Get(NativeVFIModel, "rife_v4.26.safetensors");
        modelName = modelName.Before("///").Trim();

        Logs.Info($"Native Frame Interpolation: Interpolating {multiplier}x with '{modelName}'");

        // 1. Ensure video latents are decoded to raw IMAGE frames
        g.CurrentMedia = g.CurrentMedia.AsRawImage(g.CurrentVae);
        JArray decodedFrames = g.CurrentMedia.Path;

        // 2. Create native FrameInterpolationModelLoader node
        string loaderNode = g.CreateNode("FrameInterpolationModelLoader", new JObject()
        {
            ["model_name"] = modelName
        });

        // 3. Create native FrameInterpolate node
        string interpNode = g.CreateNode("FrameInterpolate", new JObject()
        {
            ["interp_model"] = new JArray() { loaderNode, 0 },
            ["images"] = decodedFrames,
            ["multiplier"] = multiplier
        });

        // 4. Update CurrentMedia path to point to interpolated frames
        JArray interpolatedOutput = new JArray() { interpNode, 0 };
        g.CurrentMedia = g.CurrentMedia.WithPath(interpolatedOutput);

        // 5. Adjust FPS to maintain video duration if requested
        if (g.UserInput.Get(NativeVFIAdjustFps, true))
        {
            int currentFps = g.CurrentMedia.GetRawFPS() ?? g.Text2VideoFPS();
            int newFps = currentFps * multiplier;
            g.CurrentMedia.FPS = newFps;
            g.T2VFPSOverride = newFps;
            Logs.Info($"Native Frame Interpolation: Scaled video FPS from {currentFps} to {newFps}");
        }
    }
}
