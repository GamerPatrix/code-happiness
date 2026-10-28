using System.Collections.Generic;
using System.Linq;
using CodeHappiness.Avatar;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Placement and authoring window for <see cref="AvatarSittingPoint"/> spots.
///
/// Open it from Tools &gt; Code Happiness &gt; Place Sitting Point. It does three things:
///  - creates/edits the named spot (name, description, kind) on an empty GameObject,
///  - "Attach avatar" temporarily parents the character under the spot so you can drag the empty and
///    immediately see where the body ends up,
///  - lists every animation in the avatar's library as a clickable row. Click a row and the clip is
///    previewed on the attached avatar right there in the Scene view; tick the box on that row to say
///    "yes, this one works at this spot", which writes it into the spot's animation list.
///
/// The list is deliberately a list rather than a dropdown: picking a clip should cost one click and
/// show the result instantly, since judging whether a clip fits a spot is the whole point.
/// </summary>
public sealed class AvatarSittingPointEditor : EditorWindow
{
    private AvatarSittingPoint point;
    private AvatarAnimationLibrary library;
    private ChatdollKit.Model.ModelController controller;

    private Vector2 listScroll;
    private Vector2 spotScroll;
    private bool attachAvatar;
    private bool previewPlaying;
    private float previewTime;
    private string search = string.Empty;

    // Preview state
    private AnimationClip previewClip;
    private string previewClipName;
    private GameObject previewTarget;
    private readonly Dictionary<string, bool> rowFoldouts = new Dictionary<string, bool>();

    // Edit-mode parenting stash so leaving the window always gives the avatar back to the scene.
    private Transform originalParent;
    private Vector3 originalLocalPosition;
    private Quaternion originalLocalRotation;
    private bool isAttached;

    [MenuItem("Tools/Code Happiness/Place Sitting Point")]
    public static void Open()
    {
        var window = GetWindow<AvatarSittingPointEditor>("Sitting Points");
        window.minSize = new Vector2(420, 480);
        window.TryResolveSelection();
    }

    private void OnEnable()
    {
        TryResolveSelection();
        EditorApplication.update += OnEditorUpdate;
        Selection.selectionChanged += OnSelectionChanged;
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        Selection.selectionChanged -= OnSelectionChanged;
        DetachAvatar();
        StopAnimationMode();
    }

    private void OnSelectionChanged()
    {
        var selected = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponentInParent<AvatarSittingPoint>()
            : null;
        if (selected != null && selected != point)
        {
            point = selected;
            previewClip = null;
            previewClipName = null;
            Repaint();
        }
    }

    private void TryResolveSelection()
    {
        if (point != null)
        {
            return;
        }

        point = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponentInParent<AvatarSittingPoint>()
            : FindObjectsByType<AvatarSittingPoint>(FindObjectsSortMode.None).FirstOrDefault();

        library = FindFirstObjectByType<AvatarAnimationLibrary>();
        controller = FindFirstObjectByType<ChatdollKit.Model.ModelController>();
    }

    // ------------------------------------------------------------------ GUI

    private void OnGUI()
    {
        spotScroll = EditorGUILayout.BeginScrollView(spotScroll);

        DrawToolbar();

        if (point == null)
        {
            EditorGUILayout.HelpBox(
                "No sitting point selected. Create one with the button above, or select an empty that has an AvatarSittingPoint component.",
                MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        DrawSpotFields();
        DrawAttachToggle();

        EditorGUILayout.EndScrollView();

        DrawAnimationList();
    }

    private void DrawToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            if (GUILayout.Button("New spot", EditorStyles.toolbarButton, GUILayout.Width(70)))
            {
                CreatePoint();
            }

            if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                library = FindFirstObjectByType<AvatarAnimationLibrary>();
                controller = FindFirstObjectByType<ChatdollKit.Model.ModelController>();
            }

            GUILayout.FlexibleSpace();

            var count = FindObjectsByType<AvatarSittingPoint>(FindObjectsSortMode.None).Length;
            GUILayout.Label($"{count} spot(s) in scene", EditorStyles.miniLabel);
        }

        // Spot picker, so switching between the two empties on one couch is quick.
        var all = FindObjectsByType<AvatarSittingPoint>(FindObjectsSortMode.None);
        if (all.Length > 1)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Spot", GUILayout.Width(38));
                var names = all.Select(p => p.PointName).ToArray();
                var current = Mathf.Max(0, System.Array.IndexOf(names, point.PointName));
                var next = EditorGUILayout.Popup(current, names);
                if (next != current)
                {
                    ChangePoint(all[next]);
                }
            }
        }

        EditorGUILayout.Space(4);
    }

    private void DrawSpotFields()
    {
        var serialized = new SerializedObject(point);
        var nameProp = serialized.FindProperty("pointName");
        var descProp = serialized.FindProperty("description");
        var kindProp = serialized.FindProperty("kind");
        var defaultProp = serialized.FindProperty("defaultAnimationName");
        var rotateProp = serialized.FindProperty("usePointRotation");
        var idleProp = serialized.FindProperty("idleMode");
        var promptProp = serialized.FindProperty("includeInPrompt");

        EditorGUILayout.LabelField("What this spot is", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        var newName = EditorGUILayout.TextField("Name", nameProp.stringValue);
        var newDescription = EditorGUILayout.TextArea(descProp.stringValue, GUILayout.MinHeight(38));
        var newKind = (SittingPointKind)EditorGUILayout.EnumPopup("Kind", (SittingPointKind)kindProp.enumValueIndex);
        var newRotate = EditorGUILayout.ToggleLeft("Face the spot's rotation", rotateProp.boolValue);
        var newPrompt = EditorGUILayout.ToggleLeft("Tell the model about this spot", promptProp.boolValue);
        var newIdle = EditorGUILayout.TextField("Idle mode", idleProp.stringValue);
        if (EditorGUI.EndChangeCheck())
        {
            Undo.RecordObject(point, "Edit sitting point");
            nameProp.stringValue = newName;
            descProp.stringValue = newDescription;
            kindProp.enumValueIndex = (int)newKind;
            rotateProp.boolValue = newRotate;
            promptProp.boolValue = newPrompt;
            idleProp.stringValue = newIdle;
            serialized.ApplyModifiedProperties();

            var trimmed = newName.Trim();
            if (!string.IsNullOrEmpty(trimmed) && point.gameObject.name != trimmed)
            {
                point.gameObject.name = trimmed;
            }
        }

        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("Animations that work here", EditorStyles.boldLabel);

        // Default animation: the clip played when the model just says [sit:"Name"] without naming one.
        var listed = point.Animations
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.AnimationName))
            .Select(a => a.AnimationName)
            .ToList();
        var options = new List<string> { "(none)" };
        options.AddRange(listed);
        var currentIndex = Mathf.Max(0, options.IndexOf(point.DefaultAnimationName));
        EditorGUI.BeginChangeCheck();
        var picked = EditorGUILayout.Popup("Arrive with", currentIndex, options.ToArray());
        if (EditorGUI.EndChangeCheck() && picked != currentIndex)
        {
            Undo.RecordObject(point, "Set arrival animation");
            point.SetDefaultAnimation(picked <= 0 ? null : options[picked]);
            EditorUtility.SetDirty(point);
        }

        foreach (var problem in point.Validate(library))
        {
            EditorGUILayout.HelpBox(problem, MessageType.Warning);
        }

        if (point.HasUsableAnimations)
        {
            EditorGUILayout.LabelField($"Listed: {string.Join(", ", listed)}", EditorStyles.wordWrappedMiniLabel);
        }
    }

    private void DrawAttachToggle()
    {
        EditorGUILayout.Space(6);
        using (new EditorGUILayout.HorizontalScope())
        {
            var toggle = EditorGUILayout.ToggleLeft(
                "Attach avatar to this spot (drag the empty to position the body)", attachAvatar, GUILayout.MinWidth(280));
            if (toggle != attachAvatar)
            {
                if (toggle)
                {
                    AttachAvatar();
                }
                else
                {
                    DetachAvatar();
                }
            }

            if (GUILayout.Button("Focus", GUILayout.Width(52)))
            {
                if (point != null)
                {
                    Selection.activeTransform = point.transform;
                    SceneView.lastActiveSceneView?.FrameSelected();
                }
            }
        }
    }

    private void DrawAnimationList()
    {
        EditorGUILayout.Space(6);
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.LabelField("Click a row to preview it, tick it if it works here", EditorStyles.boldLabel);
        }

        search = EditorGUILayout.TextField("Search", search);

        var entries = library != null ? library.Entries : null;
        if (entries == null || entries.Count == 0)
        {
            EditorGUILayout.HelpBox(
                "The avatar animation library is empty. Run Tools > Code Happiness > Scan Avatar Animations first.",
                MessageType.Info);
            return;
        }

        listScroll = EditorGUILayout.BeginScrollView(listScroll);
        var filter = string.IsNullOrWhiteSpace(search) ? null : search.Trim().ToLowerInvariant();
        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            if (filter != null
                && !entry.Name.ToLowerInvariant().Contains(filter)
                && (entry.Description == null || !entry.Description.ToLowerInvariant().Contains(filter)))
            {
                continue;
            }

            DrawAnimationRow(entry);
        }

        EditorGUILayout.EndScrollView();

        if (previewClip != null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(previewClipName, GUILayout.Width(140));
                previewTime = EditorGUILayout.Slider(previewTime, 0f, previewClip.length);
                previewPlaying = GUILayout.Toggle(previewPlaying, "Play", "Button", GUILayout.Width(46));
            }
        }
    }

    private void DrawAnimationRow(AvatarAnimationEntry entry)
    {
        var isChecked = point.ContainsAnimation(entry.Name);
        var isPreviewing = string.Equals(previewClipName, entry.Name, System.StringComparison.OrdinalIgnoreCase);

        var foldKey = entry.Name;
        if (!rowFoldouts.TryGetValue(foldKey, out var folded))
        {
            folded = false;
        }

        using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
        {
            // The checkmark: "yes, this animation/pose works at this spot".
            var toggled = GUILayout.Toggle(isChecked, GUIContent.none, GUILayout.Width(18));
            if (toggled != isChecked)
            {
                Undo.RecordObject(point, toggled ? "Add animation to spot" : "Remove animation from spot");
                if (toggled)
                {
                    point.AddAnimation(entry.Name);
                }
                else
                {
                    point.RemoveAnimation(entry.Name);
                }

                EditorUtility.SetDirty(point);
                PrefabUtility.RecordPrefabInstancePropertyModifications(point);
            }

            // The whole rest of the row is the preview button - one click, immediate playback.
            var label = new GUIContent(entry.Name + (entry.IsPose ? "  (pose)" : string.Empty), entry.Description);
            var style = isPreviewing ? EditorStyles.boldLabel : EditorStyles.label;
            if (GUILayout.Button(label, style, GUILayout.MinWidth(120)))
            {
                PreviewAnimation(entry);
            }

            if (GUILayout.Button(folded ? "+" : "-", EditorStyles.miniButtonLeft, GUILayout.Width(20)))
            {
                rowFoldouts[foldKey] = !folded;
            }

            var listed = point.FindAnimation(entry.Name);
            if (listed != null && GUILayout.Button(listed.Note == null ? "note" : "note*", EditorStyles.miniButtonRight, GUILayout.Width(42)))
            {
                rowFoldouts[foldKey] = !(listed.Note != null);
            }
        }

        if (!folded)
        {
            return;
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Space(22);
            var existing = point.FindAnimation(entry.Name);
            var note = existing != null ? existing.Note : null;
            var newNote = EditorGUILayout.TextField("Note for the model", note);
            if (newNote != note)
            {
                if (existing == null)
                {
                    return;
                }

                Undo.RecordObject(point, "Edit spot animation note");
                if (string.IsNullOrWhiteSpace(newNote))
                {
                    point.RemoveAnimation(entry.Name);
                    point.AddAnimation(entry.Name, null);
                }
                else
                {
                    point.RemoveAnimation(entry.Name);
                    point.AddAnimation(entry.Name, newNote);
                }

                EditorUtility.SetDirty(point);
            }
        }
    }

    // ------------------------------------------------------------------ actions

    private void CreatePoint()
    {
        var parent = Selection.activeTransform;
        var go = new GameObject("Sitting Point");
        Undo.RegisterCreatedObjectUndo(go, "Create sitting point");
        if (parent != null)
        {
            go.transform.SetParent(parent, false);
        }

        var created = go.AddComponent<AvatarSittingPoint>();
        created.EditorSetName($"Sitting Point {FindObjectsByType<AvatarSittingPoint>(FindObjectsSortMode.None).Length}");
        ChangePoint(created);
        Selection.activeGameObject = go;
        EditorGUIUtility.PingObject(go);
    }

    private void ChangePoint(AvatarSittingPoint next)
    {
        if (next == point)
        {
            return;
        }

        DetachAvatar();
        StopAnimationMode();
        point = next;
        previewClip = null;
        previewClipName = null;
        previewTime = 0f;
        previewPlaying = false;
        Selection.activeGameObject = next != null ? next.gameObject : null;
        Repaint();
    }

    private void PreviewAnimation(AvatarAnimationEntry entry)
    {
        previewClip = ResolveClip(entry);
        previewClipName = entry.Name;
        previewTime = 0f;
        previewPlaying = true;

        if (Application.isPlaying)
        {
            var anim = entry.ToAnimation();
            controller = controller != null ? controller : FindFirstObjectByType<ChatdollKit.Model.ModelController>();
            if (controller != null)
            {
                controller.TeleportTo(point.Position, point.Rotation);
                controller.Animate(new List<ChatdollKit.Model.Animation> { anim });
            }

            return;
        }

        if (previewClip == null)
        {
            EditorGUILayout.HelpBox($"Could not find the AnimationClip for \"{entry.Name}\" at {entry.SourceAssetPath}.", MessageType.Warning);
            return;
        }

        AttachAvatar();
        SamplePreview();
    }

    /// <summary>
    /// Finds the clip behind a library entry. FBX files hold their clips as sub-assets, so match by
    /// name across everything the asset contains.
    /// </summary>
    private static AnimationClip ResolveClip(AvatarAnimationEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.SourceAssetPath))
        {
            return null;
        }

        var main = AssetDatabase.LoadAssetAtPath<AnimationClip>(entry.SourceAssetPath);
        if (main != null)
        {
            return main;
        }

        var clips = AssetDatabase.LoadAllAssetsAtPath(entry.SourceAssetPath).OfType<AnimationClip>().ToList();
        if (clips.Count == 0)
        {
            return null;
        }

        var exact = clips.FirstOrDefault(c => string.Equals(c.name, entry.Name, System.StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact;
        }

        var byState = clips.FirstOrDefault(c => string.Equals(c.name, entry.AnimatorStateName, System.StringComparison.OrdinalIgnoreCase));
        return byState != null ? byState : clips[0];
    }

    // ------------------------------------------------------------------ attach / preview

    private void AttachAvatar()
    {
        if (isAttached || point == null)
        {
            return;
        }

        controller = controller != null ? controller : FindFirstObjectByType<ChatdollKit.Model.ModelController>();
        if (controller == null || controller.AvatarModel == null)
        {
            EditorUtility.DisplayDialog("Sitting Points", "No ModelController with an AvatarModel was found in the scene.", "OK");
            return;
        }

        previewTarget = controller.AvatarModel;
        originalParent = previewTarget.transform.parent;
        originalLocalPosition = previewTarget.transform.localPosition;
        originalLocalRotation = previewTarget.transform.localRotation;

        previewTarget.transform.SetParent(point.transform, false);
        previewTarget.transform.localPosition = Vector3.zero;
        previewTarget.transform.localRotation = point.UsePointRotation ? Quaternion.identity : Quaternion.Inverse(point.transform.rotation);
        isAttached = true;
        attachAvatar = true;

        if (!Application.isPlaying)
        {
            StartAnimationMode();
            SamplePreview();
        }
    }

    private void DetachAvatar()
    {
        attachAvatar = false;
        if (!isAttached || previewTarget == null)
        {
            isAttached = false;
            return;
        }

        previewTarget.transform.SetParent(originalParent, false);
        previewTarget.transform.localPosition = originalLocalPosition;
        previewTarget.transform.localRotation = originalLocalRotation;
        isAttached = false;
        previewTarget = null;

        if (!Application.isPlaying)
        {
            StopAnimationMode();
        }

        EditorUtility.SetDirty(previewTarget);
    }

    private void OnEditorUpdate()
    {
        if (Application.isPlaying || previewClip == null || !previewPlaying || !isAttached)
        {
            lastTick = (float)EditorApplication.timeSinceStartup;
            return;
        }

        var now = (float)EditorApplication.timeSinceStartup;
        var delta = now - lastTick;
        lastTick = now;
        if (delta <= 0f || delta > 1f)
        {
            return;
        }

        previewTime += delta;
        if (previewTime > previewClip.length)
        {
            previewTime = 0f;
        }

        SamplePreview();
        Repaint();
    }

    private float lastTick;

    private void SamplePreview()
    {
        if (Application.isPlaying || previewClip == null || previewTarget == null)
        {
            return;
        }

        StartAnimationMode();
        AnimationMode.SampleAnimationClip(previewTarget, previewClip, previewTime);
        SceneView.RepaintAll();
    }

    private static void StartAnimationMode()
    {
        if (!AnimationMode.InAnimationMode())
        {
            AnimationMode.StartAnimationMode();
        }
    }

    private static void StopAnimationMode()
    {
        if (AnimationMode.InAnimationMode())
        {
            AnimationMode.StopAnimationMode();
        }
    }
}
