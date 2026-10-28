using System.Collections.Generic;
using UnityEngine;

namespace CodeHappiness.Avatar
{
    /// <summary>What the avatar does at a spot. Mostly tells the LLM how to talk about it.</summary>
    public enum SittingPointKind
    {
        Sitting,
        Laying,
        Standing,
        Any
    }

    /// <summary>
    /// One animation the avatar is allowed to play at a specific spot, plus why it works there.
    /// The name must match an entry in <see cref="AvatarAnimationLibrary"/> - the placement window
    /// only ever writes names it took from that library, so the two can never drift apart.
    /// </summary>
    [System.Serializable]
    public class AvatarSittingPointAnimation
    {
        /// <summary>Registered animation/pose name, e.g. "Female Sitting Pose".</summary>
        public string AnimationName;

        /// <summary>
        /// Optional extra note shown to the LLM next to this animation *at this spot*, e.g.
        /// "her left hand rests on the arm rest". Leave empty to just use the animation's own description.
        /// </summary>
        [TextArea(1, 2)]
        public string Note;

        public AvatarSittingPointAnimation()
        {
        }

        public AvatarSittingPointAnimation(string animationName, string note = null)
        {
            AnimationName = animationName;
            Note = note;
        }
    }

    /// <summary>
    /// A named spot in the apartment the avatar can be moved to and animate at - the left end of the
    /// couch, the far end of the same couch for laying down, a chair, a windowsill.
    ///
    /// Put this on an empty GameObject and place the empty where the avatar's feet should go. The
    /// avatar is teleported to the empty's position/rotation when it plays an animation here, because
    /// root motion is switched off (see <see cref="AvatarAnimationLibrary"/>) and clips animate in place.
    ///
    /// Two empties can sit on the same piece of furniture: one for sitting, one further along for
    /// laying. Each carries only the animations that actually look right at *its* position, which is
    /// what makes "hand on the arm of the couch" work - that clip is listed on the end closest to
    /// the arm rest and nowhere else.
    ///
    /// Use Tools > Code Happiness > Place Sitting Point to place one and tick the animations that fit.
    /// </summary>
    [DisallowMultipleComponent]
    public class AvatarSittingPoint : MonoBehaviour
    {
        [SerializeField] private string pointName;
        [TextArea(2, 4)]
        [SerializeField] private string description;
        [SerializeField] private SittingPointKind kind = SittingPointKind.Sitting;

        [Header("Animations that work at this spot")]
        [SerializeField] private List<AvatarSittingPointAnimation> animations = new List<AvatarSittingPointAnimation>();

        [Header("Behaviour")]
        [SerializeField] private bool includeInPrompt = true;
        [SerializeField] private bool usePointRotation = true;
        [Tooltip("Idle mode to switch to when the avatar arrives here. Empty keeps whatever is playing.")]
        [SerializeField] private string idleMode;
        [Tooltip("The clip that actually puts the avatar into this spot, e.g. \"Female Sitting Pose\". " +
                 "Played automatically when the model moves here without asking for an animation of its own.")]
        [SerializeField] private string defaultAnimationName;

        /// <summary>Name the LLM uses inside [sit:"Name"]. Falls back to the GameObject name.</summary>
        public string PointName => string.IsNullOrWhiteSpace(pointName) ? gameObject.name : pointName.Trim();

        public string Description => description;
        public SittingPointKind Kind => kind;
        public bool IncludeInPrompt => includeInPrompt;
        public bool UsePointRotation => usePointRotation;
        public string IdleMode => idleMode;
        public string DefaultAnimationName => defaultAnimationName;
        public IReadOnlyList<AvatarSittingPointAnimation> Animations => animations;

        /// <summary>Where the avatar's feet go. The empty itself is the anchor.</summary>
        public Vector3 Position => transform.position;
        public Quaternion Rotation => usePointRotation ? transform.rotation : Quaternion.identity;

        public bool HasUsableAnimations => CountUsableAnimations() > 0;

        public int CountUsableAnimations()
        {
            var count = 0;
            for (var i = 0; i < animations.Count; i++)
            {
                if (animations[i] != null && !string.IsNullOrWhiteSpace(animations[i].AnimationName))
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Case-insensitive lookup so the LLM's spelling differences never break a move.</summary>
        public AvatarSittingPointAnimation FindAnimation(string animationName)
        {
            if (string.IsNullOrWhiteSpace(animationName))
            {
                return null;
            }

            for (var i = 0; i < animations.Count; i++)
            {
                var entry = animations[i];
                if (entry != null && string.Equals(entry.AnimationName, animationName.Trim(), System.StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }

        public bool ContainsAnimation(string animationName) => FindAnimation(animationName) != null;

        /// <summary>Adds an animation to this spot. Returns false when it was already listed.</summary>
        public bool AddAnimation(string animationName, string note = null)
        {
            if (string.IsNullOrWhiteSpace(animationName) || ContainsAnimation(animationName))
            {
                return false;
            }

            animations.Add(new AvatarSittingPointAnimation(animationName.Trim(), note));
            return true;
        }

        public bool RemoveAnimation(string animationName)
        {
            var entry = FindAnimation(animationName);
            if (entry == null)
            {
                return false;
            }

            animations.Remove(entry);
            return true;
        }

        public void SetDescription(string value) => description = value;
        public void SetKind(SittingPointKind value) => kind = value;
        public void SetIdleMode(string value) => idleMode = value;
        public void SetIncludeInPrompt(bool value) => includeInPrompt = value;
        public void SetDefaultAnimation(string value) => defaultAnimationName = value;
        public void SetUsePointRotation(bool value) => usePointRotation = value;

#if UNITY_EDITOR
        /// <summary>Editor-only: renames the spot and keeps the GameObject name in sync.</summary>
        public void EditorSetName(string value)
        {
            pointName = value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                gameObject.name = value.Trim();
            }
        }
#endif

        /// <summary>
        /// Human readable problems with this spot, shown by the placement window.
        /// <paramref name="animationLibrary"/> may be null, in which case names are not checked.
        /// </summary>
        public IEnumerable<string> Validate(AvatarAnimationLibrary animationLibrary)
        {
            if (string.IsNullOrWhiteSpace(pointName) && string.IsNullOrWhiteSpace(gameObject.name))
            {
                yield return "This spot has no name.";
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                yield return $"\"{PointName}\" has no description, so the model has no idea when to use it.";
            }

            if (CountUsableAnimations() == 0)
            {
                yield return $"\"{PointName}\" has no animations, so the avatar can only stand there.";
            }

            if (animationLibrary == null)
            {
                yield break;
            }

            foreach (var entry in animations)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.AnimationName))
                {
                    continue;
                }

                if (!animationLibrary.IsAnimationKnown(entry.AnimationName))
                {
                    yield return $"\"{PointName}\": animation \"{entry.AnimationName}\" is not in the animation library. Run Tools > Code Happiness > Scan Avatar Animations, or remove it.";
                }
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = kind == SittingPointKind.Laying ? Color.cyan : Color.yellow;
            Gizmos.DrawWireSphere(transform.position, 0.12f);
            Gizmos.DrawRay(transform.position, transform.up * 0.35f);
        }
    }
}
