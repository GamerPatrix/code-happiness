using System;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using ChatdollKit;
using ChatdollKit.SpeechListener;

public static class SwapMic
{
    public static string Main()
    {
        var sb = new StringBuilder();
        var companion = GameObject.Find("/AICompanion");
        if (companion == null) { return "FAIL: no /AICompanion"; }

        // The kit ships a no-op IMicrophoneManager for exactly this text-only setup.
        // Replacing the real one removes every device/driver code path.
        var real = companion.GetComponent<MicrophoneManager>();
        if (real != null)
        {
            UnityEngine.Object.DestroyImmediate(real, true);
            sb.AppendLine("removed MicrophoneManager");
        }
        if (companion.GetComponent<DummyMicrophoneManager>() == null)
        {
            companion.AddComponent<DummyMicrophoneManager>();
            sb.AppendLine("added DummyMicrophoneManager");
        }
        EditorUtility.SetDirty(companion);

        // AIAvatar.MicrophoneManager is a public interface field (not serialized) and
        // Awake() only fills it when null, so re-resolve it explicitly here.
        var avatar = companion.GetComponent<AIAvatar>();
        avatar.MicrophoneManager = companion.GetComponent<IMicrophoneManager>();
        sb.AppendLine("avatar.MicrophoneManager=" + (avatar.MicrophoneManager == null ? "NULL!" : avatar.MicrophoneManager.GetType().Name));
        sb.AppendLine("avatar.SpeechListener=" + (avatar.SpeechListener == null ? "NULL" : avatar.SpeechListener.GetType().Name));

        // Any other scene mic left behind.
        for (var s = 0; s < SceneManager.sceneCount; s++)
        {
            var sc = SceneManager.GetSceneAt(s);
            if (!sc.IsValid() || !sc.isLoaded) { continue; }
            var roots = new List<GameObject>();
            sc.GetRootGameObjects(roots);
            foreach (var r in roots)
            {
                foreach (var m in r.GetComponentsInChildren<MicrophoneManager>(true))
                {
                    var so = new SerializedObject(m);
                    var auto = so.FindProperty("AutoStart");
                    if (auto != null) { auto.boolValue = false; so.ApplyModifiedPropertiesWithoutUndo(); }
                    m.enabled = false;
                    EditorUtility.SetDirty(m);
                    sb.AppendLine("other mic disabled: " + m.gameObject.name);
                }
            }
        }

        EditorSceneManager.MarkSceneDirty(companion.scene);
        sb.AppendLine("scene dirty=" + companion.scene.name);
        return sb.ToString();
    }
}
