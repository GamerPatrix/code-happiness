using System.Collections.Generic;
using System.Linq;
using System.Text;
using ChatdollKit.Model;
using ChatdollKit.LLM;
using UnityEngine;
using Animation = ChatdollKit.Model.Animation;

namespace CodeHappiness.Avatar
{
    /// <summary>
    /// One animation the avatar can play. Produced by the editor scanner in
    /// Assets/Editor/AvatarAnimationLibraryEditor.cs and consumed at runtime.
    /// </summary>
    [System.Serializable]
    public class AvatarAnimationEntry
    {
        /// <summary>Name the LLM uses inside [anim:Name]. Equals the clip name.</summary>
        public string Name;

        /// <summary>Tells the LLM when this animation is appropriate. Comes from the sidecar .txt file.</summary>
        [TextArea(1, 3)]
        public string Description;

        public string ParameterKey = "BaseParam";
        public int ParameterValue;
        public float Duration = 1f;

        /// <summary>Set for clips that live on an additive layer.</summary>
        public string LayeredAnimationName;
        public string LayeredAnimationLayerName;

        /// <summary>Name of the AnimatorController state the scanner made for this clip.</summary>
        public string AnimatorStateName;

        /// <summary>Animator layer the state lives on ("Base Layer" for base layer clips).</summary>
        public string AnimatorLayerName;

        public bool IsIdle;
        public int IdleWeight = 1;
        public string IdleMode = "normal";

        /// <summary>Pose = the avatar stays in it. Animation = it plays out and ends.</summary>
        public bool IsPose;

        /// <summary>How many times in a row the LLM may want this played. 1 = once.</summary>
        public int Loops = 1;

        /// <summary>
        /// Extra seconds on top of natural playback. For a pose this is the whole hold and 0 means
        /// "hold until something else replaces it".
        /// </summary>
        public float HoldSeconds;

        /// <summary>Animation/pose to move to when this one finishes. Optional.</summary>
        public string NextAnimationName;

        /// <summary>Asset that contains the clip (.anim file or FBX model).</summary>
        public string SourceAssetPath;
        public string SourceFolder;

        public bool IsLayered => !string.IsNullOrEmpty(LayeredAnimationName) && !string.IsNullOrEmpty(LayeredAnimationLayerName);

        /// <summary>Builds the runtime animation object for this entry.</summary>
        public Animation ToAnimation()
        {
            var duration = Duration > 0f ? Duration : 1f;
            return new Animation(ParameterKey, ParameterValue, duration, LayeredAnimationName, LayeredAnimationLayerName)
            {
                Name = Name,
                AnimatorStateName = AnimatorStateName,
                AnimatorLayerName = AnimatorLayerName,
                Kind = IsPose ? AnimationKind.Pose : AnimationKind.Animation,
                Loops = Loops > 0 ? Loops : 1,
                HoldSeconds = HoldSeconds,
                NextAnimationName = NextAnimationName
            };
        }
    }

    /// <summary>
    /// Reads every animation clip placed under <c>Assets/Animations/Avatar</c>, registers them with
    /// ChatdollKit's ModelController and tells the LLM which ones exist, so it can request them with
    /// [anim:Name] tags in its reply.
    ///
    /// Clips are scanned in the editor (see AvatarAnimationLibraryEditor) and stored in <see cref="entries"/>,
    /// which keeps the component working in player builds where AssetDatabase is unavailable.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [DisallowMultipleComponent]
    public class AvatarAnimationLibrary : MonoBehaviour
    {
        // A pose filed into an Idle/ folder still has to let the next random idle take over.
        private const float IdlePoseDefaultHoldSeconds = 8f;

        private const string DefaultSystemPromptTemplate =
@"You have a body and can move it. You can play animations, freeze in a pose, and change how you idle.

Tags (write them inline, immediately before the words the movement should accompany):
- [anim:""Name""] plays a clip once. Optional options separated by spaces:
    [anim:""Name"" loops=3] plays it 3 times in a row.
    [anim:""Name"" hold=2] stays in the last frame 2 extra seconds.
    [anim:""Name"" next=""OtherName""] moves into another animation/pose when this one ends.
- [pose:""Name""] freezes you in a position and you stay there until you do something else.
    [pose:""Name"" hold=5] holds for 5 seconds then returns to idle.
    [pose:""Name"" next=""OtherName""] holds, then moves into that animation/pose.
- [idle:mode] changes which idle animation plays while you are not moving, e.g. [idle:Sitting].
    It is not a pose and should not be used to request the initial sit-down posture. For a held
    posture, use a matching [pose:""Name""] from the Poses list; use [idle:Sitting] only when you
    want the seated idle animation to cycle after you are already sitting.

Rules:
- Only use names from the lists below, spelled exactly as written and kept inside the quotes.
    Never invent a name.
- Durations are in seconds. Use loops/hold/next to time a movement to what you are saying.
- Use at most one tag per sentence, and only when the movement genuinely fits what you are saying.
- Saying something without any tag is perfectly fine.
- Tags are not spoken, so do not read them out or explain them.

Animations (they play out and end):
{0}

Poses (you stay in them):
{1}

Idle modes (for [idle:mode]):
{2}";

        [Header("References (auto-detected when empty)")]
        [SerializeField] private ModelController modelController;
        [SerializeField] private LLMServiceBase llmService;

        [Header("Animations")]
        [SerializeField] private string animationsFolder = AvatarAnimationDefaults.Folder;
        [SerializeField] private List<AvatarAnimationEntry> entries = new List<AvatarAnimationEntry>();
        [SerializeField] private bool registerOnAwake = true;
        [SerializeField] private bool registerIdleAnimations = true;

        [Header("LLM prompt")]
        [SerializeField] private bool injectIntoSystemMessage = true;
        [SerializeField] private string systemPromptTemplate = DefaultSystemPromptTemplate;

        // System prompt as it was before this component touched it, so re-applying never duplicates text.
        private string originalSystemMessage;
        private bool idleAnimationsRegistered;

        public IReadOnlyList<AvatarAnimationEntry> Entries => entries;
        public string AnimationsFolder => animationsFolder;
        public ModelController ModelController => modelController != null ? modelController : (modelController = ResolveModelController());
        public LLMServiceBase LLMService => llmService != null ? llmService : (llmService = ResolveLLMService());

        public IEnumerable<string> AnimationNames => entries.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => e.Name);

        /// <summary>
        /// True when name matches a registered entry (case-insensitive). Used by AvatarSittingPoint to
        /// tell the author that a spot lists a clip that does not exist in the library.
        /// </summary>
        public bool IsAnimationKnown(string name) => FindEntry(name) != null;

        /// <summary>Returns the entry with this name, or null. Case-insensitive.</summary>
        public AvatarAnimationEntry FindEntry(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var trimmed = name.Trim();
            for (var i = 0; i < entries.Count; i++)
            {
                if (string.Equals(entries[i].Name, trimmed, System.StringComparison.OrdinalIgnoreCase))
                {
                    return entries[i];
                }
            }

            return null;
        }

        private void Awake()
        {
            if (registerOnAwake)
            {
                RegisterAll();
            }
        }

        /// <summary>
        /// Registers every entry with the ModelController and refreshes the LLM system message.
        /// Safe to call at any time; calling it twice does not duplicate idle animations or prompt text.
        /// </summary>
        public void RegisterAll()
        {
            var controller = ModelController;
            if (controller == null)
            {
                Debug.LogWarning($"{name}: no ModelController found, animations were not registered.", this);
                return;
            }

            var count = 0;
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                controller.RegisterAnimation(entry.Name, entry.ToAnimation());
                count++;
            }

            if (registerIdleAnimations && !idleAnimationsRegistered)
            {
                foreach (var entry in entries.Where(e => e.IsIdle && !string.IsNullOrEmpty(e.Name)))
                {
                    var idle = entry.ToAnimation();
                    // An idle that holds forever would stop the random rotation dead, so give a pose
                    // used as an idle a default hold time.
                    if (idle.IsPose && idle.HoldSeconds <= 0f)
                    {
                        idle.HoldSeconds = IdlePoseDefaultHoldSeconds;
                    }

                    controller.AddIdleAnimation(
                        idle,
                        entry.IdleWeight > 0 ? entry.IdleWeight : 1,
                        string.IsNullOrEmpty(entry.IdleMode) ? "normal" : entry.IdleMode);
                }
                idleAnimationsRegistered = true;
            }

            EnsureAdditiveLayerWeights(controller);
            EnsureRootMotionDisabled(controller);

            if (injectIntoSystemMessage)
            {
                RefreshSystemMessage();
            }

            Debug.Log($"{name}: registered {count} animation(s) from {animationsFolder}.");
        }

        /// <summary>
        /// Additive layers only show up when their weight is 1. AnimatorControllerLayer.defaultWeight is
        /// not reliably applied to the live Animator, so set it explicitly.
        /// </summary>
        private void EnsureAdditiveLayerWeights(ModelController controller)
        {
            if (controller.AvatarModel == null)
            {
                return;
            }

            var animator = controller.AvatarModel.GetComponent<Animator>();
            if (animator == null || animator.layerCount < 2)
            {
                return;
            }

            var layeredLayerNames = entries.Where(e => e.IsLayered).Select(e => e.LayeredAnimationLayerName).Distinct();
            foreach (var layerName in layeredLayerNames)
            {
                var index = animator.GetLayerIndex(layerName);
                if (index > 0)
                {
                    animator.SetLayerWeight(index, 1f);
                }
            }
        }

        /// <summary>
        /// Humanoid clips carry RootT/RootQ curves. With applyRootMotion on, those curves move the avatar's
        /// transform and it slowly walks out of frame, so the companion always animates in place.
        /// </summary>
        private void EnsureRootMotionDisabled(ModelController controller)
        {
            if (controller.AvatarModel == null)
            {
                return;
            }

            foreach (var animator in controller.AvatarModel.GetComponentsInChildren<Animator>(true))
            {
                if (animator.applyRootMotion)
                {
                    animator.applyRootMotion = false;
                }
            }
        }

        /// <summary>Bulleted list of "name: description" lines for every registered animation.</summary>
        public string BuildAnimationList()
        {
            return BuildEntryList(false);
        }

        /// <summary>Bulleted list of the poses the avatar can hold.</summary>
        public string BuildPoseList()
        {
            return BuildEntryList(true);
        }

        /// <summary>Bulleted list of the idle modes the model can switch between.</summary>
        public string BuildIdleModeList()
        {
            var sb = new StringBuilder();
            foreach (var group in entries.Where(e => e.IsIdle && !string.IsNullOrEmpty(e.Name))
                         .GroupBy(e => string.IsNullOrEmpty(e.IdleMode) ? "normal" : e.IdleMode))
            {
                sb.Append("- ").Append(group.Key).Append(": ").Append(group.Count()).Append(" clip(s)");
                var first = group.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Description));
                if (first != null)
                {
                    sb.Append(" - e.g. ").Append(Clean(first.Description));
                }
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private string BuildEntryList(bool poses)
        {
            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Name) || entry.IsIdle || entry.IsPose != poses)
                {
                    continue;
                }

                sb.Append("- \"").Append(entry.Name).Append('"');
                if (!string.IsNullOrWhiteSpace(entry.Description))
                {
                    sb.Append(": ").Append(Clean(entry.Description));
                }

                sb.Append(" (").Append(poses ? "pose" : "animation").Append(", ~").Append(FormatDuration(entry)).Append(")");
                if (!string.IsNullOrEmpty(entry.NextAnimationName))
                {
                    sb.Append(" ends in \"").Append(entry.NextAnimationName).Append('"');
                }
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        private static string Clean(string text)
        {
            return text.Trim().Replace("\r", " ").Replace("\n", " ");
        }

        private static string FormatDuration(AvatarAnimationEntry entry)
        {
            if (entry.IsPose && entry.HoldSeconds <= 0f)
            {
                return "holds until replaced";
            }

            var loops = entry.Loops > 0 ? entry.Loops : 1;
            var seconds = (entry.Duration > 0f ? entry.Duration : 1f) * loops + Mathf.Max(0f, entry.HoldSeconds);
            return seconds.ToString("0.0") + "s" + (loops > 1 ? ", " + loops + " loops" : string.Empty);
        }

        /// <summary>The full animation instructions that get appended to the LLM system message.</summary>
        public string BuildSystemPrompt()
        {
            var animations = BuildAnimationList();
            var poses = BuildPoseList();
            var idleModes = BuildIdleModeList();
            if (string.IsNullOrEmpty(animations) && string.IsNullOrEmpty(poses) && string.IsNullOrEmpty(idleModes))
            {
                return string.Empty;
            }

            var template = systemPromptTemplate;
            // Scene assets keep serialized values even after this code's defaults change. The
            // existing scene still has the old animation-only template, which hides poses and idle
            // modes from the model. Keep custom templates that opt into the complete tag format;
            // otherwise use the current template so the model can actually discover those entries.
            if (string.IsNullOrEmpty(template)
                || !template.Contains("[pose:")
                || !template.Contains("{1}")
                || !template.Contains("{2}"))
            {
                template = DefaultSystemPromptTemplate;
            }

            return string.Format(template, animations, poses, idleModes);
        }

        /// <summary>
        /// Writes <see cref="BuildSystemPrompt"/> into the LLM service's system message, keeping whatever
        /// was already there.
        /// </summary>
        public void RefreshSystemMessage()
        {
            var service = LLMService;
            if (service == null)
            {
                Debug.LogWarning($"{name}: no LLMServiceBase found, the animation list was not sent to the model.", this);
                return;
            }

            if (originalSystemMessage == null)
            {
                originalSystemMessage = service.SystemMessageContent ?? string.Empty;
            }

            var prompt = BuildSystemPrompt();
            service.SystemMessageContent = string.IsNullOrEmpty(originalSystemMessage)
                ? prompt
                : originalSystemMessage + "\n\n" + prompt;

            // This overwrote everything that was appended after the original message, including the
            // sitting-point list, so let the point library put its block back.
            var pointLibrary = FindFirstObjectByType<AvatarPointLibrary>();
            if (pointLibrary != null)
            {
                pointLibrary.RefreshSystemMessage();
            }
        }

        /// <summary>Restores the system message this component replaced.</summary>
        public void ResetSystemMessage()
        {
            var service = LLMService;
            if (service != null && originalSystemMessage != null)
            {
                service.SystemMessageContent = originalSystemMessage;
            }
        }

        // Used by the editor scanner.
        public void ReplaceEntries(IEnumerable<AvatarAnimationEntry> newEntries)
        {
            entries = newEntries != null ? new List<AvatarAnimationEntry>(newEntries) : new List<AvatarAnimationEntry>();
            idleAnimationsRegistered = false;
        }

        public void SetAnimationsFolder(string folder)
        {
            animationsFolder = folder;
        }

        private ModelController ResolveModelController()
        {
            return GetComponent<ModelController>()
                ?? GetComponentInParent<ModelController>()
                ?? GetComponentInChildren<ModelController>()
                ?? FindFirstObjectByType<ModelController>();
        }

        private LLMServiceBase ResolveLLMService()
        {
            return GetComponent<LLMServiceBase>()
                ?? GetComponentInParent<LLMServiceBase>()
                ?? GetComponentInChildren<LLMServiceBase>()
                ?? FindFirstObjectByType<LLMServiceBase>();
        }
    }

    /// <summary>Shared location for the avatar animation folder.</summary>
    public static class AvatarAnimationDefaults
    {
        public const string Folder = "Assets/Animations/Avatar";
        public const string BaseLayerParameter = "BaseParam";
        public const string AdditiveLayerParameter = "AdditiveParam";
        public const string DefaultStateName = "Default";

        /// <summary>Subfolder names that mean "play these on the base layer" instead of their own additive layer.</summary>
        /// <remarks>
        /// "FullBody" is here because clips like sitting, laying or aiming replace the whole pose - blending
        /// them additively on top of the standing idle looks broken.
        /// </remarks>
        public static readonly string[] BaseLayerFolderNames = { "Base", "Base Layer", "BaseLayer", "FullBody", "Pose", "Poses" };
    }
}
