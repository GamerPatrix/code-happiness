using System;
using System.Text;
using System.Linq;
using System.Reflection;
using UnityEngine;

public static class ProbeApi
{
    public static string Main()
    {
        var sb = new StringBuilder();
        Dump(sb, "UnityEditor.AssetDatabase", "UnityEditor.AssetDatabase, UnityEditor");
        Dump(sb, "UnityEditor.EditorUtility", "UnityEditor.EditorUtility, UnityEditor");
        Dump(sb, "UnityEditor.SceneManagement.EditorSceneManager", "UnityEditor.SceneManagement.EditorSceneManager, UnityEditor");
        Dump(sb, "UnityEditor.SerializedObject", "UnityEditor.SerializedObject, UnityEditor");
        Dump(sb, "UnityEditor.SceneManagement.SceneModification", "UnityEditor.SceneManagement.SceneModification, UnityEditor");
        Dump(sb, "UnityEngine.EventSystems.EventSystem", "UnityEngine.EventSystems.EventSystem, UnityEngine.CoreModule");
        Dump(sb, "UnityEngine.EventSystems.BaseInputModule", "UnityEngine.EventSystems.BaseInputModule, UnityEngine.UI");

        // Which assemblies define a type literally named AssetDatabase / SceneModification
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies().Where(x => x.GetName().Name.Contains("Editor")))
        {
            Type[] types;
            try { types = a.GetTypes(); } catch { continue; }
            foreach (var t in types.Where(t => t.Name == "AssetDatabase" || t.Name == "SceneModification" || t.Name == "BaseInputModule"))
            {
                sb.AppendLine("TYPE " + t.FullName + "  <-- " + a.GetName().Name);
            }
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "AgentScripts", "probe-api-out.txt"),
            sb.ToString());
        return "wrote chars=" + sb.Length;
    }

    private static void Dump(StringBuilder sb, string label, string assemblyQualified)
    {
        Type t = Type.GetType(assemblyQualified);
        if (t == null)
        {
            sb.AppendLine("=== " + label + " : TYPE NOT FOUND via " + assemblyQualified);
            return;
        }

        sb.AppendLine("=== " + label + " (" + t.Assembly.GetName().Name + ")");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (m.Name.StartsWith("get_") || m.Name.StartsWith("set_") || m.Name == "GetType" || m.Name == "ToString"
                || m.Name == "Equals" || m.Name == "GetHashCode" || m.Name == ".ctor")
            {
                continue;
            }

            var ps = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name));
            sb.AppendLine("  " + (m.IsStatic ? "static " : "") + m.ReturnType.Name + " " + m.Name + "(" + ps + ")");
        }
    }
}
