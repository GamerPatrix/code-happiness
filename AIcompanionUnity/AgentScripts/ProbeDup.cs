using System;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using ChatdollKit;
using ChatdollKit.Dialog;
using ChatdollKit.Model;
using ChatdollKit.LLM;

public static class ProbeDup
{
    public static string Main()
    {
        var sb = new StringBuilder();
        for (var s = 0; s < SceneManager.sceneCount; s++)
        {
            var sc = SceneManager.GetSceneAt(s);
            if (!sc.IsValid() || !sc.isLoaded) { continue; }
            sb.AppendLine("=== scene " + sc.name + " ===");

            var roots = new List<GameObject>();
            sc.GetRootGameObjects(roots);
            foreach (var r in roots)
            {
                sb.AppendLine("ROOT " + r.name + " active=" + r.activeSelf);
                foreach (var t in r.GetComponentsInChildren<Transform>(true))
                {
                    var go = t.gameObject;
                    foreach (var c in go.GetComponents<Component>())
                    {
                        var tn = c.GetType().Name;
                        if (tn.Contains("EventSystem") || tn.Contains("InputModule")
                            || tn == "VRCFaceExpressionProxy" || tn == "AIAvatar"
                            || tn == "DialogProcessor" || tn.Contains("ChatGPTService")
                            || tn == "FaceController" || tn == "Blink" || tn == "ModelController")
                        {
                            sb.AppendLine("   " + Path(go.transform) + "  <" + tn + ">");
                        }
                    }
                }
            }
        }

        var es = GameObject.Find("/EventSystem");
        if (es != null)
        {
            sb.AppendLine("--- /EventSystem components ---");
            foreach (var c in es.GetComponents<Component>())
            {
                sb.AppendLine("   " + c.GetType().FullName + " enabled=" + (c is Behaviour ? ((Behaviour)c).enabled.ToString() : "-"));
            }
            var sys = es.GetComponent<EventSystem>();
            sb.AppendLine("   currentInputModule=" + (sys.currentInputModule == null ? "null" : sys.currentInputModule.GetType().Name));
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "AgentScripts", "probe-dup-out.txt"),
            sb.ToString());
        return "wrote chars=" + sb.Length;
    }

    private static string Path(Transform t)
    {
        var s = t.name;
        var p = t.parent;
        while (p != null) { s = p.name + "/" + s; p = p.parent; }
        return s;
    }
}
