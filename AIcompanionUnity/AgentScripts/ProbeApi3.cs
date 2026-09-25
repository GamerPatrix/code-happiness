using System;
using System.Text;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;

public static class ProbeApi3
{
    public static string Main()
    {
        var sb = new StringBuilder();

        var so = typeof(ScriptableObject);
        foreach (var m in so.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            sb.AppendLine("ScriptableObject." + m.Name + "(" +
                string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ") -> " + m.ReturnType.Name);
        }

        sb.AppendLine("--- AssetDatabase members matching IsValid/CreateAsset ---");
        foreach (var m in typeof(AssetDatabase).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (m.Name.Contains("IsValid") || m.Name == "CreateAsset" || m.Name == "CreateFolder" || m.Name.Contains("LoadAsset"))
            {
                sb.AppendLine("  " + m.ReturnType.Name + " " + m.Name + "(" +
                    string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            }
        }

        sb.AppendLine("--- Scene has? ---");
        foreach (var m in typeof(UnityEngine.SceneManagement.Scene).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (m.Name.Contains("Root") || m.Name == "GetRootGameObjects" || m.Name.Contains("Dirty"))
            {
                sb.AppendLine("  " + m.ReturnType.Name + " " + m.Name + "(" +
                    string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name)) + ")");
            }
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "AgentScripts", "probe-api3-out.txt"),
            sb.ToString());
        return "wrote chars=" + sb.Length;
    }
}
