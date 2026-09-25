using System;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using ChatdollKit;
using ChatdollKit.Dialog;
using ChatdollKit.UI;
using ChatdollKit.SpeechListener;

public static class SetupChatUI
{
    private const string MessageInputPrefab = "Assets/ChatdollKit/Prefabs/Runtime/MessageInput.prefab";

    public static string Main()
    {
        var sb = new StringBuilder();

        var companion = GameObject.Find("/AICompanion");
        if (companion == null) { return "FAIL: no /AICompanion"; }
        var avatar = companion.GetComponent<AIAvatar>();
        if (avatar == null) { return "FAIL: no AIAvatar"; }
        var dialog = companion.GetComponent<DialogProcessor>();
        if (dialog == null) { return "FAIL: no DialogProcessor"; }

        // ---- 1. Dummy speech listener: the kit's sanctioned text-only mode.
        //         AIAvatar.Awake() picks up ISpeechListener components on its own, and this is
        //         also what stops the AIAvatar.cs:381 NullReferenceException spam.
        if (companion.GetComponent<DummySpeechListener>() == null)
        {
            companion.AddComponent<DummySpeechListener>();
            EditorUtility.SetDirty(companion);
            sb.AppendLine("added DummySpeechListener to /AICompanion");
        }
        else
        {
            sb.AppendLine("DummySpeechListener already present");
        }

        // ---- 2. EventSystem + input module, otherwise the text box can never be typed into.
        if (GameObject.Find("/EventSystem") == null)
        {
            var es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();
            EditorUtility.SetDirty(es);
            sb.AppendLine("added /EventSystem + StandaloneInputModule");
        }
        else
        {
            sb.AppendLine("/EventSystem already present");
        }

        // ---- 3. The chat text box itself.
        var canvas = companion.GetComponentInChildren<Canvas>(true);
        if (canvas == null) { return sb + "FAIL: no Canvas under /AICompanion"; }

        if (canvas.GetComponentInChildren<MessageInput>(true) != null)
        {
            sb.AppendLine("MessageInput already present, skipping instantiate");
        }
        else
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(MessageInputPrefab);
            if (prefab == null) { return sb + "FAIL: prefab missing at " + MessageInputPrefab; }

            var go = UnityEngine.Object.Instantiate(prefab, canvas.transform);
            go.name = "MessageInput";
            EditorUtility.SetDirty(go);

            var mi = go.GetComponent<MessageInput>();
            if (mi == null) { return sb + "FAIL: instantiated root has no MessageInput"; }

            var so = new SerializedObject(mi);
            so.FindProperty("dialogProcessor").objectReferenceValue = dialog;
            so.ApplyModifiedPropertiesWithoutUndo();
            sb.AppendLine("instantiated " + Path(go.transform) + " and wired dialogProcessor");

            var field = go.GetComponentInChildren<InputField>(true);
            sb.AppendLine("  InputField=" + (field == null ? "NULL!" : field.gameObject.name + " interactable=" + field.interactable));
            foreach (var b in go.GetComponentsInChildren<Button>(true))
            {
                sb.AppendLine("  Button=" + Path(b.transform) + " interactable=" + b.interactable);
            }
            foreach (var img in go.GetComponentsInChildren<Image>(true))
            {
                sb.AppendLine("  Image=" + Path(img.transform) + " sprite=" + (img.sprite == null ? "none" : img.sprite.name));
            }
            foreach (var t in go.GetComponentsInChildren<Text>(true))
            {
                sb.AppendLine("  Text=" + Path(t.transform) + " '" + t.text + "'");
            }

            // The serialized refs that must be non-null for OnSubmitMessageInput() to survive.
            var so2 = new SerializedObject(mi);
            foreach (var pn in new[] { "dialogProcessor", "simpleCamera", "messageInput",
                                        "messageTextRect", "messagePlaceholderRect", "imagePreview" })
            {
                var p = so2.FindProperty(pn);
                sb.AppendLine("  ref " + pn + "=" + (p == null ? "NO SUCH PROP"
                    : (p.objectReferenceValue == null ? "NULL" : p.objectReferenceValue.name)));
            }

            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0.0f, 0.0f);
                rt.anchorMax = new Vector2(1.0f, 0.0f);
                rt.pivot = new Vector2(0.5f, 0.0f);
                rt.offsetMin = new Vector2(20.0f, 20.0f);
                rt.offsetMax = new Vector2(-20.0f, 90.0f);
                EditorUtility.SetDirty(rt);
                sb.AppendLine("  anchored to bottom, height 70");
            }
        }

        // ---- 4. Report the final wiring state.
        sb.AppendLine("MessageInput in scene="
            + (canvas.GetComponentInChildren<MessageInput>(true) == null ? "NULL!" : "ok"));

        EditorSceneManager.MarkSceneDirty(companion.scene);
        AssetDatabase.SaveAssets();
        sb.AppendLine("scene marked dirty: " + companion.scene.name);
        return sb.ToString();
    }

    private static string Path(Transform t)
    {
        var s = t.name;
        var p = t.parent;
        while (p != null) { s = p.name + "/" + s; p = p.parent; }
        return s;
    }
}
