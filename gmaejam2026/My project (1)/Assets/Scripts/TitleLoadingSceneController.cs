using System.Collections;
using Spine;
using Spine.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class TitleLoadingSceneController : MonoBehaviour
{
    [SerializeField] private string nextSceneName = "SampleScene";
    [SerializeField] private float holdSeconds = 2.5f;

    private SkeletonAnimation loadingCharacter;
    private string loadingAnimationName;

    private IEnumerator Start()
    {
        BuildLoadingVisual();
        yield return new WaitForSecondsRealtime(holdSeconds);
        SceneManager.LoadScene(nextSceneName);
    }

    private void BuildLoadingVisual()
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            GameObject cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 0f, -10f);
            camera = cameraObject.GetComponent<Camera>();
        }

        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.black;
        camera.orthographic = true;
        camera.orthographicSize = 5f;

        float halfHeight = camera.orthographicSize;
        float halfWidth = halfHeight * camera.aspect;

        SkeletonDataAsset[] dataAssets = Resources.LoadAll<SkeletonDataAsset>("Spine/Character03");
        if (dataAssets == null || dataAssets.Length == 0)
        {
            return;
        }

        SkeletonDataAsset dataAsset = dataAssets[0];
        SkeletonData skeletonData = dataAsset.GetSkeletonData(true);
        if (skeletonData == null)
        {
            return;
        }

        loadingCharacter = SkeletonAnimation.NewSkeletonAnimationGameObject(dataAsset);
        loadingCharacter.name = "Loading Screen Character";
        loadingCharacter.transform.SetParent(camera.transform, false);
        loadingCharacter.Initialize(false);

        Skeleton skeleton = loadingCharacter.Skeleton;
        if (skeleton != null)
        {
            Skin skin = skeleton.Data.FindSkin("Skin70") ?? skeleton.Data.FindSkin("Skin0");
            if (skin != null)
            {
                skeleton.SetSkin(skin);
                skeleton.SetSlotsToSetupPose();
            }
        }

        float dataScale = Mathf.Max(0.0001f, dataAsset.scale);
        float importedHeight = skeletonData.Height * dataScale;
        float importedCenterX = (skeletonData.X + skeletonData.Width * 0.5f) * dataScale;
        float importedCenterY = (skeletonData.Y + skeletonData.Height * 0.5f) * dataScale;
        float characterScale = importedHeight > 0.01f ? 3.3f / importedHeight : 0.12f;

        loadingCharacter.transform.localScale = Vector3.one * characterScale;
        loadingCharacter.transform.localPosition = new Vector3(
            halfWidth - 0.7f - importedCenterX * characterScale,
            -halfHeight + 2.4f - importedCenterY * characterScale,
            10f);

        MeshRenderer renderer = loadingCharacter.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            renderer.sortingOrder = 210;
        }

        loadingAnimationName = FindLoadingAnimationName(skeletonData);
        if (!string.IsNullOrEmpty(loadingAnimationName))
        {
            loadingCharacter.AnimationState.SetAnimation(0, loadingAnimationName, true);
        }
    }

    private static string FindLoadingAnimationName(SkeletonData skeletonData)
    {
        foreach (Spine.Animation animation in skeletonData.Animations)
        {
            if (animation.Name.ToLowerInvariant().Contains("loading"))
            {
                return animation.Name;
            }
        }

        foreach (Spine.Animation animation in skeletonData.Animations)
        {
            if (animation.Name.ToLowerInvariant().Contains("think"))
            {
                return animation.Name;
            }
        }

        return skeletonData.Animations.Count > 0 ? skeletonData.Animations.Items[0].Name : string.Empty;
    }
}
