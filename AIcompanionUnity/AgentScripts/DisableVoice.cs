using System;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using ChatdollKit;
using ChatdollKit.SpeechListener;

public static class DisableVoice
{
    public static string Main()
    {
        var sb = new StringBuilder();

        // 1. Turn off every microphone: no device capture, no driver-capability probe.
        var mics = new List<MicrophoneManager>();
        for (var s = 0; s < SceneManager.sceneCount; s++)
        {
            var sc = SceneManager.GetSceneAt(s);
            if (!sc.IsValid() || !sc.isLoaded) { continue; }
            var roots = new List<GameObject>();
            sc.GetRootGameObjects(roots);
            foreach (var r in roots)
            {
                mics.AddRange(r.GetComponentsInChildren<MicrophoneManager>(true));
            }
        }
        foreach (var mic in mics)
        {
            var so = new SerializedObject(mic);
            var auto = so.FindProperty("AutoStart");
            if (auto != null) { auto.boolValue = false; so.ApplyModifiedPropertiesWithoutUndo(); }
            mic.enabled = false;
            EditorUtility.SetDirty(mic);
            sb.AppendLine("mic " + Path(mic.transform) + " AutoStart=false enabled=false");
        }

        // 2. The scene carries a second, non-functional AIAvatar root (no ChatGPTService,
        //    no speech listener, message windows hidden). Its AIAvatar.Update() throws
        //    every frame because SpeechListener never resolves. Silence it.
        var stray = GameObject.Find("/AIAvatar");
        if (stray != null)
        {
            var hasLlm = stray.GetComponent<ChatdollKit.LLM.ChatGPT.ChatGPTService>() != null;
            var hasListener = stray.GetComponent<ISpeechListener>() != null;
            if (!hasLlm && !hasListener)
            {
                stray.SetActive(false);
                EditorUtility.SetDirty(stray);
                sb.AppendLine("disabled stray root /AIAvatar (no LLMService, no ISpeechListener)");
            }
            else
            {
                sb.AppendLine("/AIAvatar looks functional (llm=" + hasLlm + " listener=" + hasListener + "), left alone");
            }
        }
        else
        {
            sb.AppendLine("no /AIAvatar root");
        }

        // 3. Read back what remains live.
        var companion = GameObject.Find("/AICompanion");
        if (companion == null) { return sb + "FAIL: no /AICompanion"; }
        sb.AppendLine("--- /AICompanion ---");
        sb.AppendLine("  activeInHierarchy=" + companion.activeInHierarchy);
        foreach (var c in companion.GetComponents<Component>())
        {
            var b = c as Behaviour;
            if (b == null) { continue; }
            if (c is MicrophoneManager || c is ISpeechListener
                || c is ChatdollKit.LLM.ChatGPT.ChatGPTService || c is AIAvatar)
            {
                sb.AppendLine("  <" + c.GetType().Name + "> enabled=" + b.enabled);
            }
        }

        EditorSceneManager.MarkSceneDirty(companion.scene);
        sb.AppendLine("scene dirty=" + companion.scene.name);
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
