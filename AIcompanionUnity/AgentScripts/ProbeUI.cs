using System.Text;
using UnityEngine;
using ChatdollKit;
using ChatdollKit.Dialog;
using ChatdollKit.LLM;
using ChatdollKit.Model;
using ChatdollKit.SpeechListener;
using ChatdollKit.SpeechSynthesizer;
using ChatdollKit.UI;
using UnityEngine.UI;

public static class ProbeUI
{
    public static string Main()
    {
        var sb = new StringBuilder();
        var companion = GameObject.Find("/AICompanion");
        if (companion == null)
        {
            return "ABORT: /AICompanion not found";
        }

        sb.AppendLine("scene=" + companion.scene.name);

        var avatar = companion.GetComponent<AIAvatar>();
        if (avatar != null)
        {
            sb.AppendLine("AIAvatar.enabled=" + avatar.enabled);
            sb.AppendLine("  DialogProcessor=" + (avatar.DialogProcessor == null ? "NULL" : "ok"));
            sb.AppendLine("  ModelController=" + (avatar.ModelController == null ? "NULL" : "ok"));
            sb.AppendLine("  SpeechListener=" + (avatar.SpeechListener == null ? "NULL" : avatar.SpeechListener.GetType().Name));
            sb.AppendLine("  MicrophoneManager=" + (avatar.MicrophoneManager == null ? "NULL" : avatar.MicrophoneManager.GetType().Name));
            sb.AppendLine("  UserMessageWindow=" + (avatar.UserMessageWindow == null ? "NULL" : avatar.UserMessageWindow.GetType().Name));
            sb.AppendLine("  CharacterMessageWindow=" + (avatar.CharacterMessageWindow == null ? "NULL" : avatar.CharacterMessageWindow.GetType().Name));
        }

        sb.AppendLine("--- components on /AICompanion ---");
        foreach (var c in companion.GetComponents<Component>())
        {
            if (c == null || c is Transform)
            {
                continue;
            }

            sb.AppendLine("  " + c.GetType().FullName + " enabled=" + (c is Behaviour b ? b.enabled.ToString() : "-"));
        }

        sb.AppendLine("--- Canvas subtree ---");
        var canvas = companion.transform.Find("Canvas");
        if (canvas == null)
        {
            sb.AppendLine("  (no Canvas child)");
        }
        else
        {
            Walk(canvas, 1, sb);
        }

        sb.AppendLine("--- InputFields / Buttons / MessageInput in whole scene ---");
        var roots = new System.Collections.Generic.List<GameObject>();
        companion.scene.GetRootGameObjects(roots);
        foreach (var go in roots)
        {
            Scan(go.transform, sb);
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "AgentScripts", "probe-ui-out.txt"),
            sb.ToString());
        return "ok chars=" + sb.Length;
    }

    private static void Walk(Transform t, int depth, StringBuilder sb)
    {
        var indent = new string(' ', depth * 2);
        var names = new StringBuilder();
        foreach (var c in t.GetComponents<Component>())
        {
            if (c == null || c is Transform)
            {
                continue;
            }

            names.Append(c.GetType().Name).Append(',');
        }

        sb.AppendLine(indent + t.name + " active=" + t.gameObject.activeSelf + " [" + names + "]");
        for (var i = 0; i < t.childCount; i++)
        {
            Walk(t.GetChild(i), depth + 1, sb);
        }
    }

    private static void Scan(Transform t, StringBuilder sb)
    {
        foreach (var f in t.gameObject.GetComponents<InputField>())
        {
            if (f != null)
            {
                sb.AppendLine("  INPUTFIELD " + Path(t) + " interactable=" + f.interactable
                        + " text='" + f.text + "' placeholderType=" + (f.placeholder == null ? "none" : f.placeholder.GetType().Name));
            }
        }

        foreach (var b in t.gameObject.GetComponents<Button>())
        {
            if (b != null)
            {
                sb.AppendLine("  BUTTON " + Path(t) + " interactable=" + b.interactable);
            }
        }

        foreach (var m in t.gameObject.GetComponents<MessageInput>())
        {
            if (m != null)
            {
                sb.AppendLine("  MESSAGEINPUT " + Path(t));
            }
        }

        for (var i = 0; i < t.childCount; i++)
        {
            Scan(t.GetChild(i), sb);
        }
    }

    private static string Path(Transform t)
    {
        var path = t.name;
        var parent = t.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }

        return path;
    }
}
