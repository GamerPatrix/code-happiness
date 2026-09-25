using System;
using System.Text;
using System.Linq;
using System.Reflection;

public static class ProbeApi2
{
    public static string Main()
    {
        var sb = new StringBuilder();
        var asms = AppDomain.CurrentDomain.GetAssemblies();

        foreach (var a in asms)
        {
            Type[] types;
            try { types = a.GetTypes(); } catch { continue; }
            foreach (var t in types.Where(t => t.Name == "EventSystem" || t.Name == "SerializedProperty" || t.Name == "StandaloneInputModule"))
            {
                sb.AppendLine("TYPE " + t.FullName + "  <-- " + a.GetName().Name);
            }
        }

        var sp = asms.SelectMany(a =>
        {
            try { return a.GetTypes(); } catch { return new Type[0]; }
        }).FirstOrDefault(t => t.FullName == "UnityEditor.SerializedProperty");

        if (sp != null)
        {
            sb.AppendLine("=== SerializedProperty props/methods of interest ===");
            foreach (var p in sp.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (p.Name.Contains("Value") || p.Name == "objectReferenceValue")
                {
                    sb.AppendLine("  prop " + p.PropertyType.Name + " " + p.Name);
                }
            }
            foreach (var m in sp.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.Name.Contains("Enumerate") || m.Name.Contains("SetValue") || m.Name.StartsWith("get_objectReference"))
                {
                    sb.AppendLine("  meth " + m.Name);
                }
            }
        }
        else
        {
            sb.AppendLine("SerializedProperty NOT FOUND");
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "AgentScripts", "probe-api2-out.txt"),
            sb.ToString());
        return "wrote chars=" + sb.Length;
    }
}
