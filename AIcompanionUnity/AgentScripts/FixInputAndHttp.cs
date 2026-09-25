using System;
using System.Text;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.EventSystems;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class FixInputAndHttp
{
    public static string Main()
    {
        var sb = new StringBuilder();

        // ---- 1. The LLM endpoint is plain http://, which Unity's web stack refuses by default.
        sb.AppendLine("insecureHttpOption before=" + PlayerSettings.insecureHttpOption);
        PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;
        sb.AppendLine("insecureHttpOption after =" + PlayerSettings.insecureHttpOption);

        // ---- 2. This project runs the Input System package, so the legacy StandaloneInputModule
        //         throws every frame. Swap in the Input System UI module.
        var esGo = GameObject.Find("/EventSystem");
        if (esGo == null) { return sb + "FAIL: /EventSystem not found"; }
        var es = esGo.GetComponent<EventSystem>();
        if (es == null) { return sb + "FAIL: /EventSystem has no EventSystem"; }

        var moduleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
        if (moduleType == null) { return sb + "FAIL: InputSystemUIInputModule type not found"; }

        foreach (var c in esGo.GetComponents<Component>())
        {
            if (c is StandaloneInputModule)
            {
                UnityEngine.Object.DestroyImmediate(c, true);
                sb.AppendLine("removed StandaloneInputModule");
            }
        }

        var existing = esGo.GetComponent(moduleType);
        if (existing == null)
        {
            existing = esGo.AddComponent(moduleType);
            sb.AppendLine("added " + existing.GetType().FullName);
        }
        else
        {
            sb.AppendLine("module already present: " + existing.GetType().FullName);
        }

        es.enabled = true;
        EditorUtility.SetDirty(es);
        EditorUtility.SetDirty(esGo);
        sb.AppendLine("module=" + existing.GetType().FullName + " enabled=" + (existing as Behaviour).enabled);

        // Report the module's serialized action bindings so unassigned ones are visible.
        foreach (var p in existing.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            sb.AppendLine("  field " + p.Name + " : " + p.FieldType.Name);
        }

        var open = esGo.scene;
        EditorSceneManager.MarkSceneDirty(open);
        sb.AppendLine("scene dirty=" + open.name + " " + open.isDirty);
        return sb.ToString();
    }
}
