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

        /// <summary>Asset that contains the clip (.anim file or FBX model).</summary>
        public string SourceAssetPath;
        public string SourceFolder;

        public bool IsLayered => !string.IsNullOrEmpty(LayeredAnimationName) && !string.IsNullOrEmpty(LayeredAnimationLayerName);
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
        private const string DefaultSystemPromptTemplate =
@"You have a body and can move it. To play one of your animations, write [anim:Name] immediately before the words that the movement should accompany.
Rules:
- Only use animation names from the list below, spelled exactly as written. Never invent a name.
- Use at most one [anim:Name] per sentence, and only when the movement genuinely fits what you are saying.
- Saying something without any animation is perfectly fine.
- The tag is not spoken, so do not read it out or explain it.

Your animations:
{0}";

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

                var duration = entry.Duration > 0f ? entry.Duration : 1f;
                controller.RegisterAnimation(
                    entry.Name,
                    new Animation(entry.ParameterKey, entry.ParameterValue, duration, entry.LayeredAnimationName, entry.LayeredAnimationLayerName));
                count++;
            }

            if (registerIdleAnimations && !idleAnimationsRegistered)
            {
                foreach (var entry in entries.Where(e => e.IsIdle && !string.IsNullOrEmpty(e.Name)))
                {
                    var duration = entry.Duration > 0f ? entry.Duration : 1f;
                    controller.AddIdleAnimation(
                        new Animation(entry.ParameterKey, entry.ParameterValue, duration, entry.LayeredAnimationName, entry.LayeredAnimationLayerName),
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
            var sb = new StringBuilder();
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry.Name))
                {
                    continue;
                }

                sb.Append("- ").Append(entry.Name);
                if (!string.IsNullOrWhiteSpace(entry.Description))
                {
                    sb.Append(": ").Append(entry.Description.Trim().Replace("\r", " ").Replace("\n", " "));
                }
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>The full animation instructions that get appended to the LLM system message.</summary>
        public string BuildSystemPrompt()
        {
            var list = BuildAnimationList();
            if (string.IsNullOrEmpty(list))
            {
                return string.Empty;
            }

            var template = string.IsNullOrEmpty(systemPromptTemplate) ? DefaultSystemPromptTemplate : systemPromptTemplate;
            return string.Format(template, list);
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
        public static readonly string[] BaseLayerFolderNames = { "Base", "Base Layer", "BaseLayer", "FullBody" };
    }
}
