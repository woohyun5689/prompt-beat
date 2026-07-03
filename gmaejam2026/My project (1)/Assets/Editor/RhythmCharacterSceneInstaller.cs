using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Spine;
using Spine.Unity;

public static class RhythmCharacterSceneInstaller
{
    private const string ScenePath = "Assets/Scenes/SampleScene.unity";
    private const string SkeletonDataPath = "Assets/Resources/Spine/Character03/03_SkeletonData.asset";
    private const string CharacterName = "Rhythm Runner Character";

    [MenuItem("Game Jam/Install Rhythm Character In Scene")]
    public static void Install()
    {
        Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        SkeletonDataAsset dataAsset = AssetDatabase.LoadAssetAtPath<SkeletonDataAsset>(SkeletonDataPath);
        if (dataAsset == null)
        {
            throw new System.InvalidOperationException("Spine SkeletonDataAsset was not found: " + SkeletonDataPath);
        }
        dataAsset.defaultMix = 0.045f;
        EditorUtility.SetDirty(dataAsset);

        GameObject existing = GameObject.Find(CharacterName);
        if (existing != null)
        {
            Object.DestroyImmediate(existing);
        }

        SkeletonData skeletonData = dataAsset.GetSkeletonData(true);
        SkeletonAnimation character = SkeletonAnimation.NewSkeletonAnimationGameObject(dataAsset);
        character.name = CharacterName;
        character.loop = true;
        character.AnimationName = "Walk";
        character.Initialize(false);
        character.AnimationState.Data.DefaultMix = 0.045f;

        float dataScale = Mathf.Max(0.0001f, dataAsset.scale);
        float importedHeight = skeletonData.Height * dataScale;
        float importedX = skeletonData.X * dataScale;
        float importedY = skeletonData.Y * dataScale;
        float characterScale = importedHeight > 0.01f ? 5.2f / importedHeight : 0.19f;
        character.transform.localScale = Vector3.one * characterScale;
        character.transform.position = new Vector3(
            -8.78f - importedX * characterScale,
            -3.15f - importedY * characterScale,
            0f);

        MeshRenderer meshRenderer = character.GetComponent<MeshRenderer>();
        if (meshRenderer != null)
        {
            meshRenderer.sortingOrder = 3;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;
            meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            meshRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            meshRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            meshRenderer.allowOcclusionWhenDynamic = false;
        }

        if (skeletonData.FindAnimation("Walk") != null)
        {
            character.AnimationState.SetAnimation(0, "Walk", true);
        }

        EditorUtility.SetDirty(character);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene);
        AssetDatabase.SaveAssets();
        Debug.Log("Installed Spine character in " + ScenePath);
    }
}
