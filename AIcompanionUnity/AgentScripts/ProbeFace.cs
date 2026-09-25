using System.Text;
using UnityEditor;
using UnityEngine;

public static class ProbeFace
{
    public static string Main()
    {
        var sb = new StringBuilder();

        var companion = GameObject.Find("/AICompanion");
        sb.AppendLine("companion=" + (companion == null ? "NULL" : companion.name));
        if (companion == null)
        {
            return sb.ToString();
        }

        sb.AppendLine("--- direct children of /AICompanion ---");
        for (var i = 0; i < companion.transform.childCount; i++)
        {
            var child = companion.transform.GetChild(i);
            var renderers = child.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var shapes = 0;
            foreach (var r in renderers)
            {
                if (r != null && r.sharedMesh != null)
                {
                    shapes += r.sharedMesh.blendShapeCount;
                }
            }

            sb.AppendLine("  child[" + i + "]=" + child.name
                + " activeSelf=" + child.gameObject.activeSelf
                + " skinnedRenderers=" + renderers.Length
                + " totalBlendShapes=" + shapes);
        }

        var mc = companion.GetComponent<ChatdollKit.Model.ModelController>();
        sb.AppendLine("modelController=" + (mc == null ? "NULL" : "ok"));
        if (mc == null)
        {
            return sb.ToString();
        }

        var model = mc.AvatarModel;
        sb.AppendLine("avatarModel=" + (model == null ? "NULL" : model.name + " id=" + model.GetInstanceID()));

        sb.AppendLine("--- skinned renderers carrying blend shapes (whole subtree) ---");
        foreach (var r in companion.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (r == null || r.sharedMesh == null || r.sharedMesh.blendShapeCount <= 0)
            {
                continue;
            }

            sb.AppendLine("RENDERER " + GetPath(r.transform) + " | mesh=" + r.sharedMesh.name
                + " | blendShapes=" + r.sharedMesh.blendShapeCount);
            for (var i = 0; i < r.sharedMesh.blendShapeCount; i++)
            {
                sb.AppendLine("  SHAPE " + i + "=" + r.sharedMesh.GetBlendShapeName(i));
            }
        }

        var proxy = companion.GetComponent<ChatdollKit.Model.VRCFaceExpressionProxy>();
        if (proxy != null)
        {
            sb.AppendLine("proxy.FaceClipConfiguration="
                + (proxy.FaceClipConfiguration == null ? "NULL" : proxy.FaceClipConfiguration.name));
            sb.AppendLine("proxy.SkinnedMeshRenderer="
                + (proxy.SkinnedMeshRenderer == null ? "NULL" : proxy.SkinnedMeshRenderer.name));
        }

        var fc = companion.GetComponent<ChatdollKit.Model.FaceController>();
        if (fc != null)
        {
            sb.AppendLine("faceController.SkinnedMeshRenderer="
                + (fc.SkinnedMeshRenderer == null ? "NULL" : fc.SkinnedMeshRenderer.name));
        }

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(UnityEngine.Application.dataPath, "..", "AgentScripts", "probe-out.txt"),
            sb.ToString());

        return "wrote probe-out.txt chars=" + sb.Length;
    }

    private static string GetPath(Transform t)
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
