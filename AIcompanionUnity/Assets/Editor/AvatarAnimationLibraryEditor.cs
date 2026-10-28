using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChatdollKit.Model;
using CodeHappiness.Avatar;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Scans Assets/Animations/Avatar (or a folder you pick), builds the animation catalogue that
/// AvatarAnimationLibrary hands to the LLM, and makes sure the avatar's AnimatorController has a
/// matching state for every clip.
///
/// Folder layout:
///   Assets/Animations/Avatar/            clips here (or in Base/) play on the base layer
///   Assets/Animations/Avatar/Idle/       clips here play on the base layer and are used while idling
///   Assets/Animations/Avatar/Idle/Sitting/   the subfolder name is the idle mode ([idle:sitting])
///   Assets/Animations/Avatar/Pose/       clips here hold in place instead of playing out
///   Assets/Animations/Avatar/Arms/       any other subfolder becomes its own additive layer
///   Assets/Animations/Avatar/Arms/Wave.txt   optional sidecar describing what the clip does
///
/// A sidecar .txt next to a clip (same file name, .txt extension) becomes the description the model
/// sees, so it knows *when* to use the animation and not just that it exists. Lines in it that start
/// with '#' are metadata rather than description: #pose, #hold=5, #loops=3, #next=Wave.
/// </summary>
public static class AvatarAnimationLibraryEditor
{
    private const string IdleFolderName = "Idle";
    private const string BaseLayerName = "Base Layer";
    private static readonly string[] PoseFolderNames = { "Pose", "Poses" };

    [MenuItem("Tools/Code Happiness/Scan Avatar Animations")]
    public static void Scan()
    {
        ScanAndBuild(AvatarAnimationDefaults.Folder);
    }

    [MenuItem("Tools/Code Happiness/Scan Avatar Animations (Choose Folder...)")]
    public static void ScanWithFolder()
    {
        var selected = EditorUtility.OpenFolderPanel("Select avatar animation folder", Application.dataPath, string.Empty);
        if (string.IsNullOrEmpty(selected))
        {
            return;
        }

        var assetPath = ToAssetPath(selected);
        if (!string.IsNullOrEmpty(assetPath))
        {
            ScanAndBuild(assetPath);
        }
    }

    [MenuItem("Tools/Code Happiness/Create Missing Animation Description Files")]
    public static void CreateMissingDescriptionFiles()
    {
        if (!AssetDatabase.IsValidFolder(AvatarAnimationDefaults.Folder))
        {
            Debug.LogError($"Avatar animation folder does not exist: {AvatarAnimationDefaults.Folder}");
            return;
        }

        var clips = CollectClips(AvatarAnimationDefaults.Folder, out _);
        var created = 0;
        foreach (var clip in clips)
        {
            var assetPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(assetPath))
            {
                continue;
            }

            var displayName = ClipDisplayName(clip, assetPath);
            var sidecar = Path.Combine(GetParentFolder(assetPath), displayName + ".txt").Replace('\\', '/');
            if (File.Exists(sidecar))
            {
                continue;
            }

            File.WriteAllText(sidecar, $"Describe in one sentence when the avatar should play {displayName}.");
            created++;
        }

        AssetDatabase.Refresh();
        Debug.Log($"Created {created} description file(s). Edit them, then run Tools > Code Happiness > Scan Avatar Animations again.");
    }

    public static void ScanAndBuild(string folderAssetPath)
    {
        if (string.IsNullOrEmpty(folderAssetPath) || !folderAssetPath.StartsWith("Assets/"))
        {
            Debug.LogError($"Avatar animation folder must be inside Assets/: {folderAssetPath}");
            return;
        }

        if (!AssetDatabase.IsValidFolder(folderAssetPath))
        {
            Debug.LogError($"Avatar animation folder does not exist: {folderAssetPath}");
            return;
        }

        var library = EnsureLibraryComponent(out var controller, out var avatar);
        if (library == null || avatar == null)
        {
            Debug.LogError("Could not find an avatar to animate. Set AvatarModel on the scene's ModelController first.");
            return;
        }

        var clips = CollectClips(folderAssetPath, out var clipFolders);
        if (clips.Count == 0)
        {
            Debug.LogWarning($"No animation clips found in {folderAssetPath}. Drop .anim or FBX clips there and scan again.");
            return;
        }

        var animator = EnsureAnimator(avatar);
        if (animator == null)
        {
            Debug.LogError($"{avatar.name} has no Animator component.");
            return;
        }

        var animatorController = EnsureAnimatorController(animator, avatar, folderAssetPath);
        if (animatorController == null)
        {
            return;
        }

        // Drop the states this scanner made last time so re-scanning never leaves stale states behind
        // holding onto parameter values. States nobody here created are left alone.
        RemoveGeneratedStates(animatorController, library, folderAssetPath);

        var entries = new List<AvatarAnimationEntry>();

        // Idle clips must own the lowest BaseParam values: the parameter starts at 0 when the Animator
        // is created, so whatever owns 0 is what the avatar does the moment the scene starts.
        var planned = clips
            .Select(clip =>
            {
                var clipFolder = clipFolders[clip];
                var displayName = ClipDisplayName(clip);
                var isBaseLayer = IsBaseLayerFolder(clipFolder, folderAssetPath);
                var meta = ReadMetadata(clip, displayName, clipFolder, folderAssetPath);
                var isIdle = isBaseLayer && IsIdleClip(displayName, clipFolder, folderAssetPath);
                return new
                {
                    clip,
                    displayName,
                    clipFolder,
                    isBaseLayer,
                    isIdle,
                    isPose = meta.isPose,
                    meta,
                    idleMode = IdleModeFor(clipFolder, folderAssetPath)
                };
            })
            // Idles own the lowest values because BaseParam starts at 0, which is what the avatar does
            // the moment the scene starts. Poses come next so they never end up owning 0 either.
            .OrderByDescending(c => c.isIdle)
            .ThenBy(c => c.isPose)
            .ThenByDescending(c => c.isBaseLayer)
            .ThenBy(c => c.displayName, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        var baseCounter = NextParameterValue(animatorController, AvatarAnimationDefaults.BaseLayerParameter);
        // Value 0 on the additive parameter means "no gesture", so the additive layer stays on its empty
        // Default state when the scene starts instead of auto-playing the first gesture.
        var additiveCounter = Mathf.Max(1, NextParameterValue(animatorController, AvatarAnimationDefaults.AdditiveLayerParameter));

        foreach (var clip in planned)
        {
            var clipFolder = clip.clipFolder;
            var displayName = clip.displayName;
            var isBaseLayer = clip.isBaseLayer;
            var layerName = isBaseLayer ? null : clipFolder.Split('/').Last();
            var isIdle = clip.isIdle;

            var parameterKey = isBaseLayer
                ? AvatarAnimationDefaults.BaseLayerParameter
                : AvatarAnimationDefaults.AdditiveLayerParameter;
            var parameterValue = isBaseLayer ? baseCounter++ : additiveCounter++;

            var stateName = BuildUniqueStateName(animatorController, isBaseLayer, layerName, displayName);
            AddState(animatorController, isBaseLayer, layerName, stateName, clip.clip, parameterKey, parameterValue, controller);

            entries.Add(new AvatarAnimationEntry
            {
                Name = displayName,
                Description = clip.meta.description,
                ParameterKey = parameterKey,
                ParameterValue = parameterValue,
                Duration = ClipDuration(clip.clip),
                LayeredAnimationName = isBaseLayer ? null : stateName,
                LayeredAnimationLayerName = isBaseLayer ? null : layerName,
                AnimatorStateName = stateName,
                AnimatorLayerName = isBaseLayer ? BaseLayerName : layerName,
                IsIdle = isIdle,
                IdleWeight = 1,
                IdleMode = isIdle ? clip.idleMode : "normal",
                IsPose = clip.isPose,
                Loops = clip.meta.loops,
                HoldSeconds = clip.meta.hasHold ? clip.meta.holdSeconds : 0f,
                NextAnimationName = clip.meta.next,
                SourceAssetPath = AssetDatabase.GetAssetPath(clip.clip),
                SourceFolder = clipFolder
            });
        }

        library.SetAnimationsFolder(folderAssetPath);
        library.ReplaceEntries(entries);
        library.RegisterAll();

        EditorUtility.SetDirty(library);
        EditorUtility.SetDirty(animatorController);
        MarkActiveSceneDirty();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        var baseCount = entries.Count(e => !e.IsLayered);
        var withDescription = entries.Count(e => !string.IsNullOrWhiteSpace(e.Description));
        var idleModes = entries.Where(e => e.IsIdle).Select(e => e.IdleMode).Distinct().Count();
        Debug.Log(
$@"Avatar animations scanned from {folderAssetPath}:
- {baseCount} base layer clip(s), {entries.Count - baseCount} additive clip(s)
- {entries.Count(e => e.IsIdle)} idle clip(s) in {idleModes} idle mode(s), {entries.Count(e => e.IsPose)} pose(s)
- {withDescription}/{entries.Count} have a description for the model
- AnimatorController: {AssetDatabase.GetAssetPath(animatorController)}
- The model can now use [anim:Name], [pose:Name] and [idle:mode] tags. Press Play to try it.");
    }

    #region Scanning

    private static List<AnimationClip> CollectClips(string folderAssetPath, out Dictionary<AnimationClip, string> clipFolders)
    {
        clipFolders = new Dictionary<AnimationClip, string>();
        var clips = new List<AnimationClip>();

        var assetPaths = new List<string>();
        foreach (var guidFile in Directory.GetFiles(folderAssetPath, "*.meta", SearchOption.AllDirectories))
        {
            var assetPath = guidFile.Substring(0, guidFile.Length - 5).Replace('\\', '/');
            if (File.Exists(assetPath) && IsAnimationAsset(assetPath))
            {
                assetPaths.Add(assetPath);
            }
        }

        foreach (var assetPath in assetPaths.OrderBy(p => p, System.StringComparer.OrdinalIgnoreCase))
        {
            var usedNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (!(obj is AnimationClip clip) || clip.name.StartsWith("__preview__"))
                {
                    continue;
                }

                // FBX clips imported without named takes all come in as "Take 001", which is useless as a
                // name the model has to pick, so fall back to the file name and keep only the first one.
                var displayName = ClipDisplayName(clip, assetPath);
                if (!usedNames.Add(displayName))
                {
                    continue;
                }

                clipFolders[clip] = GetParentFolder(assetPath);
                clips.Add(clip);
            }
        }

        return clips;
    }

    /// <summary>The name the model uses in [anim:Name] tags.</summary>
    private static string ClipDisplayName(AnimationClip clip, string assetPath = null)
    {
        if (!string.IsNullOrEmpty(clip.name) && !clip.name.StartsWith("Take 001"))
        {
            return clip.name;
        }

        var path = string.IsNullOrEmpty(assetPath) ? AssetDatabase.GetAssetPath(clip) : assetPath;
        if (string.IsNullOrEmpty(path))
        {
            return clip.name;
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// How long the model should hold the clip before going back to idle.
    /// Poses get their natural length here - the time they actually hold comes from
    /// <see cref="AvatarAnimationEntry.HoldSeconds"/>, which is decided by <see cref="ReadMetadata"/>.
    /// </summary>
    private static float ClipDuration(AnimationClip clip)
    {
        return Mathf.Max(0.1f, clip.length);
    }

    private static bool IsAnimationAsset(string assetPath)
    {
        var extension = Path.GetExtension(assetPath).ToLowerInvariant();
        return extension == ".anim" || extension == ".fbx" || extension == ".bvh";
    }

    private static string GetParentFolder(string assetPath)
    {
        var index = assetPath.LastIndexOf('/');
        return index > 0 ? assetPath.Substring(0, index) : assetPath;
    }

    private static bool IsBaseLayerFolder(string clipFolder, string rootFolder)
    {
        if (string.Equals(clipFolder, rootFolder, System.StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Anything under Idle/ or a Pose/ folder replaces the whole body, so it belongs on the base layer.
        var segments = RelativeSegments(clipFolder, rootFolder);
        if (segments.Contains(IdleFolderName, System.StringComparer.OrdinalIgnoreCase) || IsPoseFolder(clipFolder, rootFolder))
        {
            return true;
        }

        var folderName = clipFolder.Split('/').Last();
        return AvatarAnimationDefaults.BaseLayerFolderNames.Contains(folderName)
            || string.Equals(folderName, IdleFolderName, System.StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIdleClip(string displayName, string clipFolder, string rootFolder)
    {
        // Anything filed under Idle/ (including its <mode> subfolders) is part of the idle rotation.
        var segments = RelativeSegments(clipFolder, rootFolder);
        if (segments.Contains(IdleFolderName, System.StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // Only loose clips sitting in the root folder are guessed at from the name. Clips deliberately
        // filed in a named subfolder (Base, FullBody, ...) stay out of the idle rotation, otherwise a
        // "Sitting Idle" clip would make the avatar sit down at random while it should be standing.
        if (segments.Length == 0)
        {
            return displayName.ToLowerInvariant().Contains("idle");
        }

        return false;
    }

    /// <summary>Everything the scanner learned from the folder layout and the sidecar .txt file.</summary>
    private class ClipMetadata
    {
        public string description = string.Empty;
        public bool isPose;
        public bool hasHold;
        public float holdSeconds;
        public int loops = 1;
        public string next;
    }

    /// <summary>
    /// Decides what a clip *is* rather than guessing from its length.
    ///
    /// Folder layout is the primary source: anything under a Pose/ (or Poses/) folder is a pose, and
    /// Idle/&lt;mode&gt;/ subfolders name the idle mode the model can switch to with [idle:mode].
    /// The sidecar .txt can override that with keyword lines:
    ///   #pose            - treat as a pose (holds until replaced)
    ///   #hold=5          - hold for 5 seconds
    ///   #loops=3         - play 3 times in a row
    ///   #next=Wave       - move into Wave when this one ends
    /// Keyword lines are stripped from the description the model sees.
    /// </summary>
    private static ClipMetadata ReadMetadata(AnimationClip clip, string displayName, string clipFolder, string rootFolder)
    {
        var meta = new ClipMetadata();
        meta.isPose = IsPoseFolder(clipFolder, rootFolder);
        meta.description = ReadSidecar(clip, displayName);
        return ApplySidecarKeywords(meta, ref meta.description);
    }

    private static ClipMetadata ApplySidecarKeywords(ClipMetadata meta, ref string description)
    {
        var kept = new List<string>();
        foreach (var rawLine in (description ?? string.Empty).Replace("\r", string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("#"))
            {
                kept.Add(rawLine);
                continue;
            }

            var body = line.Substring(1).Trim();
            var eq = body.IndexOf('=');
            var key = (eq < 0 ? body : body.Substring(0, eq)).Trim().ToLowerInvariant();
            var value = eq < 0 ? string.Empty : body.Substring(eq + 1).Trim().Trim('"', '\'');
            switch (key)
            {
                case "pose":
                case "poses":
                    meta.isPose = true;
                    break;
                case "hold":
                    if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hold))
                    {
                        meta.holdSeconds = hold;
                        meta.hasHold = true;
                    }
                    break;
                case "loops":
                case "loop":
                case "repeat":
                    if (int.TryParse(value, out var loops) && loops > 0)
                    {
                        meta.loops = loops;
                    }
                    break;
                case "next":
                case "then":
                    meta.next = string.IsNullOrEmpty(value) ? null : value;
                    break;
                default:
                    Debug.LogWarning($"Unknown keyword '#{line.Substring(1)}' - ignoring.");
                    break;
            }
        }

        description = string.Join("\n", kept).Trim();
        return meta;
    }

    private static string ReadSidecar(AnimationClip clip, string displayName)
    {
        var assetPath = AssetDatabase.GetAssetPath(clip);
        if (string.IsNullOrEmpty(assetPath))
        {
            return string.Empty;
        }

        // .anim sidecar sits next to the file; FBX sidecar sits next to the FBX and is named after the clip.
        var candidates = new List<string>
        {
            Path.Combine(GetParentFolder(assetPath), displayName + ".txt").Replace('\\', '/'),
            Path.ChangeExtension(assetPath, ".txt").Replace('\\', '/')
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate).Trim();
            }
        }

        return string.Empty;
    }

    /// <summary>True when the clip sits under a Pose/ (or Poses/) folder anywhere below the root.</summary>
    private static bool IsPoseFolder(string clipFolder, string rootFolder)
    {
        foreach (var segment in RelativeSegments(clipFolder, rootFolder))
        {
            if (PoseFolderNames.Contains(segment, System.StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Folder names between the root and the clip, e.g. Idle/sitting -> ["Idle","sitting"].</summary>
    private static string[] RelativeSegments(string clipFolder, string rootFolder)
    {
        if (string.IsNullOrEmpty(clipFolder) || string.IsNullOrEmpty(rootFolder))
        {
            return System.Array.Empty<string>();
        }

        var normalized = clipFolder.Replace('\\', '/');
        var root = rootFolder.Replace('\\', '/').TrimEnd('/');
        if (normalized.StartsWith(root + "/", System.StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring(root.Length + 1);
        }
        else if (string.Equals(normalized, root, System.StringComparison.OrdinalIgnoreCase))
        {
            return System.Array.Empty<string>();
        }

        return normalized.Split('/').Where(s => !string.IsNullOrEmpty(s)).ToArray();
    }

    /// <summary>Idle mode for a clip under Idle/&lt;mode&gt;/, or "normal" when it is loose in Idle/.</summary>
    private static string IdleModeFor(string clipFolder, string rootFolder)
    {
        var segments = RelativeSegments(clipFolder, rootFolder);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], IdleFolderName, System.StringComparison.OrdinalIgnoreCase))
            {
                var mode = segments[i + 1];
                return PoseFolderNames.Contains(mode, System.StringComparer.OrdinalIgnoreCase) ? "normal" : mode;
            }
        }

        return "normal";
    }

    #endregion

    #region AnimatorController

    private static Animator EnsureAnimator(GameObject avatar)
    {
        var animator = avatar.GetComponent<Animator>();
        if (animator == null)
        {
            animator = avatar.GetComponentInChildren<Animator>();
        }
        return animator;
    }

    /// <summary>
    /// Reuses the controller that is already driving this avatar when it is either the generated one
    /// inside the animations folder or a controller somebody else set up with real content. Otherwise
    /// creates (or reopens) Assets/Animations/Avatar/&lt;avatar&gt;.controller.
    /// </summary>
    private static AnimatorController EnsureAnimatorController(Animator animator, GameObject avatar, string folderAssetPath)
    {
        var generatedPath = $"{folderAssetPath}/{avatar.name}.controller";
        var existing = animator.runtimeAnimatorController as AnimatorController;
        var existingPath = existing != null ? AssetDatabase.GetAssetPath(existing) : null;

        if (existing != null && existingPath == generatedPath)
        {
            return existing;
        }

        if (existing != null && !string.IsNullOrEmpty(existingPath) && HasContent(existing))
        {
            Debug.Log($"Keeping the avatar's existing AnimatorController ({existingPath}). States are added to it.");
            return existing;
        }

        var reusable = AssetDatabase.LoadAssetAtPath<AnimatorController>(generatedPath);
        if (reusable != null)
        {
            animator.runtimeAnimatorController = reusable;
            return reusable;
        }

        var created = AnimatorController.CreateAnimatorControllerAtPath(AssetDatabase.GenerateUniqueAssetPath(generatedPath));
        animator.runtimeAnimatorController = created;
        EditorUtility.SetDirty(animator);
        Debug.Log($"Created AnimatorController at {generatedPath} and assigned it to {avatar.name}.");
        return created;
    }

    private static bool HasContent(AnimatorController controller)
    {
        foreach (var layer in controller.layers)
        {
            if (layer.stateMachine == null)
            {
                continue;
            }
            if (layer.stateMachine.states.Any(s => s.state != null && s.state.motion != null))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Removes the states written by the previous scan (and any additive layer left empty by it) so that
    /// re-scanning after deleting or renaming a clip does not leave orphan states behind. The scanner owns
    /// every state whose clip comes from the animations folder, plus anything it recorded last time.
    /// </summary>
    private static void RemoveGeneratedStates(AnimatorController controller, AvatarAnimationLibrary library, string folderAssetPath)
    {
        var targets = new List<(string layerName, string stateName)>();
        if (library != null)
        {
            targets.AddRange(library.Entries
                .Where(e => !string.IsNullOrEmpty(e.AnimatorStateName))
                .Select(e => (string.IsNullOrEmpty(e.AnimatorLayerName) ? BaseLayerName : e.AnimatorLayerName, e.AnimatorStateName)));
        }

        var ownedFolder = folderAssetPath.TrimEnd('/') + "/";

        var layers = controller.layers.ToList();
        for (int i = 0; i < layers.Count; i++)
        {
            var stateMachine = layers[i].stateMachine;
            if (stateMachine == null)
            {
                continue;
            }

            var isBase = i == 0;
            var toRemove = stateMachine.states
                .Select(s => s.state)
                .Where(s => s != null && s.name != AvatarAnimationDefaults.DefaultStateName && IsOwned(s, targets, layers[i].name, isBase, ownedFolder))
                .ToList();

            foreach (var state in toRemove)
            {
                stateMachine.RemoveState(state);
            }

            if (toRemove.Count > 0 && stateMachine.defaultState == null)
            {
                stateMachine.defaultState = stateMachine.states.Select(s => s.state).FirstOrDefault(s => s != null && s.name == AvatarAnimationDefaults.DefaultStateName);
            }
        }

        // Drop additive layers the scan created that no longer hold anything but the empty Default state.
        layers = controller.layers.ToList();
        for (int i = layers.Count - 1; i >= 1; i--)
        {
            var stateMachine = layers[i].stateMachine;
            if (stateMachine == null || layers[i].blendingMode != AnimatorLayerBlendingMode.Additive)
            {
                continue;
            }

            var wasGenerated = targets.Any(t => string.Equals(t.layerName, layers[i].name, System.StringComparison.OrdinalIgnoreCase))
                || stateMachine.states.Any(s => s.state != null && IsOwnedByFolder(s.state.motion, ownedFolder));
            var isNowEmpty = stateMachine.states.All(s => s.state == null || s.state.motion == null);
            if (wasGenerated && isNowEmpty)
            {
                controller.RemoveLayer(i);
            }
        }
    }

    private static bool IsOwned(AnimatorState state, List<(string layerName, string stateName)> targets, string layerName, bool isBaseLayer, string ownedFolder)
    {
        if (IsOwnedByFolder(state.motion, ownedFolder))
        {
            return true;
        }

        return targets.Any(t =>
            string.Equals(t.stateName, state.name, System.StringComparison.Ordinal)
            && (string.Equals(t.layerName, layerName, System.StringComparison.OrdinalIgnoreCase)
                || (isBaseLayer && string.Equals(t.layerName, BaseLayerName, System.StringComparison.OrdinalIgnoreCase))));
    }

    private static bool IsOwnedByFolder(Motion motion, string ownedFolder)
    {
        if (motion == null)
        {
            return false;
        }

        var assetPath = AssetDatabase.GetAssetPath(motion);
        return !string.IsNullOrEmpty(assetPath)
            && assetPath.Replace('\\', '/').StartsWith(ownedFolder, System.StringComparison.OrdinalIgnoreCase);
    }

    private static int NextParameterValue(AnimatorController controller, string parameterName)
    {
        EnsureParameter(controller, parameterName);

        var used = new HashSet<int>();
        foreach (var layer in controller.layers)
        {
            if (layer.stateMachine != null)
            {
                CollectConditionValues(layer.stateMachine, parameterName, used);
            }
        }

        var value = 0;
        while (used.Contains(value))
        {
            value++;
        }
        return value;
    }

    private static void CollectConditionValues(AnimatorStateMachine stateMachine, string parameterName, HashSet<int> used)
    {
        if (stateMachine == null)
        {
            return;
        }

        foreach (var entry in stateMachine.states)
        {
            if (entry.state == null)
            {
                continue;
            }
            CollectTransitionConditionValues(entry.state.transitions, parameterName, used);
        }

        CollectTransitionConditionValues(stateMachine.anyStateTransitions, parameterName, used);

        foreach (var child in stateMachine.stateMachines)
        {
            CollectConditionValues(child.stateMachine, parameterName, used);
        }
    }

    private static void CollectTransitionConditionValues(AnimatorStateTransition[] transitions, string parameterName, HashSet<int> used)
    {
        if (transitions == null)
        {
            return;
        }

        foreach (var transition in transitions)
        {
            if (transition == null || transition.conditions == null)
            {
                continue;
            }

            foreach (var condition in transition.conditions)
            {
                if (condition.parameter == parameterName)
                {
                    used.Add((int)condition.threshold);
                }
            }
        }
    }

    private static void EnsureParameter(AnimatorController controller, string parameterName)
    {
        if (!controller.parameters.Any(p => p.name == parameterName))
        {
            controller.AddParameter(parameterName, AnimatorControllerParameterType.Int);
        }
    }

    private static string BuildUniqueStateName(AnimatorController controller, bool isBaseLayer, string additiveLayerName, string clipName)
    {
        var layers = controller.layers;
        var layerIndex = 0;
        if (!isBaseLayer)
        {
            layerIndex = System.Array.FindIndex(layers, 1, layers.Length - 1,
                l => string.Equals(l.name, additiveLayerName, System.StringComparison.OrdinalIgnoreCase));
        }
        if (layerIndex < 0 || layers[layerIndex].stateMachine == null)
        {
            return clipName;
        }

        var existing = new HashSet<string>(layers[layerIndex].stateMachine.states.Select(s => s.state != null ? s.state.name : string.Empty));
        if (!existing.Contains(clipName))
        {
            return clipName;
        }

        var suffix = isBaseLayer ? "Base" : "Additive";
        var candidate = $"{clipName}_{suffix}";
        var index = 2;
        while (existing.Contains(candidate))
        {
            candidate = $"{clipName}_{suffix}{index++}";
        }
        return candidate;
    }

    private static void AddState(
        AnimatorController controller,
        bool isBaseLayer,
        string additiveLayerName,
        string stateName,
        AnimationClip clip,
        string parameterKey,
        int parameterValue,
        ModelController modelController)
    {
        EnsureParameter(controller, parameterKey);

        var layerIndex = isBaseLayer ? 0 : GetOrAddAdditiveLayer(controller, additiveLayerName);
        var layers = controller.layers;
        var layer = layers[layerIndex];
        var stateMachine = layer.stateMachine;
        var fade = modelController != null ? modelController.AnimationFadeLength : 0.5f;

        // Empty "Default" state every clip returns to, and which ModelController resets layers to.
        var defaultState = GetOrCreateState(stateMachine, AvatarAnimationDefaults.DefaultStateName);
        if (stateMachine.defaultState == null)
        {
            stateMachine.defaultState = defaultState;
        }

        var state = GetOrCreateState(stateMachine, stateName);
        state.motion = clip;
        state.speed = 1f;
        state.writeDefaultValues = false;

        if (isBaseLayer)
        {
            // Trigger: any state -> clip when BaseParam equals this clip's value.
            var anyTransition = stateMachine.anyStateTransitions.FirstOrDefault(t => t.destinationState == state);
            if (anyTransition == null)
            {
                anyTransition = stateMachine.AddAnyStateTransition(state);
                anyTransition.duration = fade;
                anyTransition.exitTime = 0f;
                anyTransition.canTransitionToSelf = false;
                anyTransition.AddCondition(AnimatorConditionMode.Equals, parameterValue, parameterKey);
            }
            else
            {
                SetCondition(anyTransition, AnimatorConditionMode.Equals, parameterValue, parameterKey);
            }
        }

        // Release: clip -> Default once the parameter moves on. Additive clips are driven by
        // ModelController's CrossFadeInFixedTime, so they only need the way back.
        var backToDefault = state.transitions.FirstOrDefault(t => t.destinationState == defaultState);
        if (backToDefault == null)
        {
            backToDefault = state.AddTransition(defaultState);
            backToDefault.duration = fade;
            backToDefault.exitTime = 0f;
            backToDefault.AddCondition(AnimatorConditionMode.NotEqual, parameterValue, parameterKey);
        }
        else
        {
            SetCondition(backToDefault, AnimatorConditionMode.NotEqual, parameterValue, parameterKey);
        }
    }

    /// <summary>Returns the index of the additive layer with this name, creating it when needed.</summary>
    private static int GetOrAddAdditiveLayer(AnimatorController controller, string layerName)
    {
        var layers = controller.layers;
        for (var i = 1; i < layers.Length; i++)
        {
            if (layers[i].name == layerName)
            {
                if (layers[i].blendingMode != AnimatorLayerBlendingMode.Additive || layers[i].defaultWeight < 1f)
                {
                    var layer = layers[i];
                    layer.blendingMode = AnimatorLayerBlendingMode.Additive;
                    layer.defaultWeight = 1f;
                    layers[i] = layer;
                    controller.layers = layers;
                }
                return i;
            }
        }

        controller.AddLayer(layerName);
        layers = controller.layers;
        var index = System.Array.FindIndex(layers, l => l.name == layerName);
        var created = layers[index];
        created.blendingMode = AnimatorLayerBlendingMode.Additive;
        created.defaultWeight = 1f;
        layers[index] = created;
        controller.layers = layers;
        return index;
    }

    private static AnimatorState GetOrCreateState(AnimatorStateMachine stateMachine, string name)
    {
        foreach (var entry in stateMachine.states)
        {
            if (entry.state != null && entry.state.name == name)
            {
                return entry.state;
            }
        }
        return stateMachine.AddState(name);
    }

    private static void SetCondition(AnimatorStateTransition transition, AnimatorConditionMode mode, int value, string parameter)
    {
        var conditions = transition.conditions;
        for (var i = 0; i < conditions.Length; i++)
        {
            if (conditions[i].parameter == parameter)
            {
                conditions[i].mode = mode;
                conditions[i].threshold = value;
                transition.conditions = conditions;
                return;
            }
        }

        transition.AddCondition(mode, value, parameter);
    }

    #endregion

    #region Scene wiring

    private static AvatarAnimationLibrary EnsureLibraryComponent(out ModelController controller, out GameObject avatar)
    {
        controller = null;
        avatar = null;

        controller = UnityEngine.Object.FindFirstObjectByType<ModelController>();
        if (controller == null)
        {
            return null;
        }

        avatar = controller.AvatarModel;
        if (avatar == null)
        {
            return null;
        }

        var library = controller.gameObject.GetComponent<AvatarAnimationLibrary>();
        if (library == null)
        {
            library = controller.gameObject.AddComponent<AvatarAnimationLibrary>();
            Debug.Log($"Added AvatarAnimationLibrary to {controller.gameObject.name}.");
        }
        return library;
    }

    private static string ToAssetPath(string absolutePath)
    {
        var normalized = absolutePath.Replace('\\', '/');
        var dataPath = Application.dataPath.Replace('\\', '/');
        if (!normalized.StartsWith(dataPath))
        {
            Debug.LogError($"Selected folder must be inside {dataPath}: {normalized}");
            return null;
        }
        return "Assets" + normalized.Substring(dataPath.Length);
    }

    private static void MarkActiveSceneDirty()
    {
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (!scene.isDirty)
        {
            EditorSceneManager.MarkSceneDirty(scene);
        }
    }

    #endregion
}
