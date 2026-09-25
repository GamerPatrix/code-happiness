using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using ChatdollKit.Model;

public static class SetupFace
{
    private const string SceneName = "Assets/Scenes/AICompanion.unity";
    private const string AssetPath = "Assets/Resources/Faces-AICompanion.asset";
    private const string ModelName = "SK_VEX_AllPartsTogether Variant";
    private const string HeadMeshName = "SK_CNG_HEAD";
    private const string PrimaryBlinkShape = "eyeBlinkLeft";

    public static string Main()
    {
        var sb = new StringBuilder();

        var open = SceneManager.GetActiveScene();
        sb.AppendLine("activeScene=" + open.name);
        if (!open.name.Contains("AICompanion"))
        {
            return "ABORT: active scene is " + open.name;
        }

        var companion = GameObject.Find("/AICompanion");
        if (companion == null)
        {
            return "ABORT: /AICompanion not found";
        }

        var mc = companion.GetComponent<ModelController>();
        if (mc == null)
        {
            return "ABORT: ModelController missing";
        }

        // 1. Re-point AvatarModel at the character instance the user dropped in.
        var model = companion.transform.Find(ModelName);
        if (model == null)
        {
            return "ABORT: child " + ModelName + " not found";
        }

        mc.AvatarModel = model.gameObject;
        sb.AppendLine("AvatarModel=" + model.name);

        // 2. Locate the head renderer that carries the facial blend shapes.
        SkinnedMeshRenderer head = null;
        foreach (var smr in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr != null && smr.sharedMesh != null && smr.sharedMesh.name == HeadMeshName)
            {
                head = smr;
                break;
            }
        }

        if (head == null)
        {
            return "ABORT: " + HeadMeshName + " renderer not found";
        }

        sb.AppendLine("headRenderer=" + head.name + " shapes=" + head.sharedMesh.blendShapeCount);

        // 3. Author the FaceClipConfiguration the vendor's inspector flow would have produced,
        //    but with weights driven by the shapes this rig actually exposes.
        var config = AssetDatabase.LoadAsset<FaceClipConfiguration>(AssetPath);
        var isNew = config == null;
        if (isNew)
        {
            config = ScriptableObject.CreateInstance<FaceClipConfiguration>();
        }

        config.FaceClips = new List<FaceClip>
        {
            Clip("Neutral", head, null),
            Clip("Blink", head, Weights(head, PrimaryBlinkShape, 100f, "eyeBlinkRight", 100f)),
            Clip("Joy", head, Weights(head,
                "mouthSmileLeft", 80f, "mouthSmileRight", 80f,
                "cheekSquintLeft", 45f, "cheekSquintRight", 45f,
                "eyeSquintLeft", 30f, "eyeSquintRight", 30f,
                "browOuterUpLeft", 20f, "browOuterUpRight", 20f)),
            Clip("Angry", head, Weights(head,
                "browDownLeft", 85f, "browDownRight", 85f,
                "eyeSquintLeft", 45f, "eyeSquintRight", 45f,
                "mouthPressLeft", 35f, "mouthPressRight", 35f,
                "noseSneerLeft", 25f, "noseSneerRight", 25f)),
            Clip("Sorrow", head, Weights(head,
                "browInnerUp", 85f,
                "browOuterUpLeft", 30f, "browOuterUpRight", 30f,
                "mouthFrownLeft", 60f, "mouthFrownRight", 60f,
                "mouthShrugLower", 35f)),
            Clip("Fun", head, Weights(head,
                "jawOpen", 55f,
                "mouthSmileLeft", 55f, "mouthSmileRight", 55f,
                "eyeWideLeft", 40f, "eyeWideRight", 40f,
                "cheekSquintLeft", 30f, "cheekSquintRight", 30f)),
        };

        // The proxy keys off clip[0] and reads Values[0].Name as the facial-renderer keyword.
        if (config.FaceClips[0].Values.Count == 0)
        {
            return "ABORT: Neutral clip has no blend shape values";
        }

        sb.AppendLine("clip0=" + config.FaceClips[0].Name
            + " value0=" + config.FaceClips[0].Values[0].Name);

        if (isNew)
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
            {
                AssetDatabase.CreateFolder("Assets", "Resources");
            }

            if (!AssetDatabase.CreateAsset(config, AssetPath))
            {
                return "ABORT: could not create " + AssetPath;
            }

            sb.AppendLine("created " + AssetPath);
        }
        else
        {
            EditorUtility.SetDirty(config);
            sb.AppendLine("updated " + AssetPath);
        }

        // 4. Wire the three consumers to the authored config and the head renderer.
        var proxy = companion.GetComponent<VRCFaceExpressionProxy>();
        if (proxy != null)
        {
            proxy.FaceClipConfiguration = config;
            proxy.SkinnedMeshRenderer = head;
            EditorUtility.SetDirty(proxy);
            sb.AppendLine("proxy wired");
        }

        var faceController = companion.GetComponent<FaceController>();
        if (faceController != null)
        {
            faceController.SkinnedMeshRenderer = head;
            EditorUtility.SetDirty(faceController);
            sb.AppendLine("faceController wired");
        }

        // 5. Blink auto-detection is defeated by the left/right exclusion list, so pin both
        //    private fields and let Blink's mirrored-shape handling close both eyes.
        var blink = companion.GetComponent<Blink>();
        if (blink != null)
        {
            var so = new SerializedObject(blink);
            var rendererProp = so.FindProperty("skinnedMeshRenderer");
            var shapeProp = so.FindProperty("blinkBlendShapeName");
            if (rendererProp == null || shapeProp == null)
            {
                return "ABORT: Blink serialized fields not found";
            }

            rendererProp.objectReferenceValue = head;
            shapeProp.stringValue = PrimaryBlinkShape;
            so.ApplyModifiedPropertiesWithoutNotifications();
            EditorUtility.SetDirty(blink);
            sb.AppendLine("blink pinned to " + PrimaryBlinkShape);
        }

        EditorUtility.SetDirty(mc);
        EditorUtility.SetDirty(config);
        AssetDatabase.SaveAssets();

        // 6. Dirty the scene so the CLI's save_scene persists the wiring.
        EditorSceneManager.MarkSceneDirty(open, SceneModification.MarkSceneScenes);

        return sb.ToString();
    }

    private static FaceClip Clip(string name, SkinnedMeshRenderer renderer, Dictionary<string, float> weights)
    {
        return new FaceClip(name, renderer, weights ?? new Dictionary<string, float>());
    }

    // Builds a weight map, silently dropping names this mesh does not expose.
    private static Dictionary<string, float> Weights(SkinnedMeshRenderer renderer, params object[] pairs)
    {
        var map = new Dictionary<string, float>();
        for (var i = 0; i + 1 < pairs.Length; i += 2)
        {
            var shape = (string)pairs[i];
            var weight = (float)pairs[i + 1];
            if (renderer.sharedMesh.GetBlendShapeIndex(shape) < 0)
            {
                continue;
            }

            map[shape] = weight;
        }

        return map;
    }
}
