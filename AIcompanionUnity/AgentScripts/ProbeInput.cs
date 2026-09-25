using System;
using System.Text;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;

public static class ProbeInput
{
    public static string Main()
    {
        var sb = new StringBuilder();

        sb.AppendLine("--- PlayerSettings methods matching InputHandler ---");
        foreach (var m in typeof(PlayerSettings).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
        {
            if (m.Name.Contains("InputHandler"))
            {
                sb.AppendLine("  " + m.ReturnType.Name + " " + m.Name + "(" +
                    string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            }
        }

        sb.AppendLine("--- assemblies matching InputSystem/UGUI ---");
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var n = a.GetName().Name;
            if (n.Contains("InputSystem") || n.Contains("UGUI") || n.Contains("UnityEngine.UI"))
            {
                sb.AppendLine("  " + n);
            }
        }

        sb.AppendLine("--- candidate UI input module types ---");
        foreach (var tn in new[] {
            "Unity.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem",
            "Unity.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem.UI",
            "UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem.UI",
            "UnityEngine.EventSystems.InputSystemUIInputModule, Unity.InputSystem.UI" })
        {
            var t = Type.GetType(tn);
            sb.AppendLine("  " + tn + " => " + (t == null ? "NULL" : "FOUND in " + t.Assembly.GetName().Name));
        }

        sb.AppendLine("--- scan loaded assemblies for *UIInputModule subtypes ---");
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!a.GetName().Name.Contains("InputSystem")) { continue; }
            Type[] types;
            try { types = a.GetTypes(); } catch { continue; }
            foreach (var t in types)
            {
                if (t.IsAbstract || !t.IsPublic) { continue; }
                if (t.Name.EndsWith("UIInputModule"))
                {
                    sb.AppendLine("  " + t.FullName + "  [" + a.GetName().Name + "]");
                }
            }
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "AgentScripts", "probe-input-out.txt"),
            sb.ToString());
        return "wrote chars=" + sb.Length;
    }
}
