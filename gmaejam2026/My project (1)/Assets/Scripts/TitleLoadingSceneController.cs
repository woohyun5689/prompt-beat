using System.Collections;
using Spine;
using Spine.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;

public sealed class TitleLoadingSceneController : MonoBehaviour
{
    private const int LoadingEffectSortingOrder = 120;

    [SerializeField] private string nextSceneName = "SampleScene";
    [SerializeField] private float holdSeconds = 2.5f;

    private SkeletonAnimation loadingCharacter;
    private string loadingAnimationName;
    private Transform loadingEffectRoot;
    private Transform loadingSpinnerRoot;
    private Transform loadingPulseRing;
    private SpriteRenderer loadingPulseRenderer;
    private SpriteRenderer[] loadingSpinnerDots = new SpriteRenderer[0];
    private float loadingEffectStartTime;

    private IEnumerator Start()
    {
        BuildLoadingVisual();
        yield return new WaitForSecondsRealtime(holdSeconds);
        SceneManager.LoadScene(nextSceneName);
    }

    private void Update()
    {
        AnimateLoadingEffect();
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

        CreateLoadingCenterEffect(camera);

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

    private void CreateLoadingCenterEffect(Camera camera)
    {
        loadingEffectStartTime = Time.unscaledTime;

        GameObject rootObject = new GameObject("Loading Center Effect");
        rootObject.transform.SetParent(camera.transform, false);
        rootObject.transform.localPosition = new Vector3(0f, -0.08f, 10f);
        loadingEffectRoot = rootObject.transform;

        Sprite ringSprite = CreateRingSprite(192, 72f, 8f);
        Sprite dotSprite = CreateSoftCircleSprite(64);

        GameObject pulseObject = new GameObject("Loading Pulse Ring");
        pulseObject.transform.SetParent(loadingEffectRoot, false);
        loadingPulseRing = pulseObject.transform;
        loadingPulseRenderer = pulseObject.AddComponent<SpriteRenderer>();
        loadingPulseRenderer.sprite = ringSprite;
        loadingPulseRenderer.sortingOrder = LoadingEffectSortingOrder;
        loadingPulseRenderer.color = new Color(0.9f, 0.32f, 1f, 0.44f);
        loadingPulseRing.localScale = Vector3.one * 0.028f;

        GameObject spinnerObject = new GameObject("Loading Spinner");
        spinnerObject.transform.SetParent(loadingEffectRoot, false);
        loadingSpinnerRoot = spinnerObject.transform;

        const int dotCount = 14;
        loadingSpinnerDots = new SpriteRenderer[dotCount];
        for (int i = 0; i < dotCount; i++)
        {
            float angle = i * Mathf.PI * 2f / dotCount;
            GameObject dotObject = new GameObject($"Loading Spinner Dot {i + 1:00}");
            dotObject.transform.SetParent(loadingSpinnerRoot, false);
            dotObject.transform.localPosition = new Vector3(Mathf.Cos(angle) * 1.35f, Mathf.Sin(angle) * 1.35f, 0f);
            dotObject.transform.localScale = Vector3.one * 0.16f;

            SpriteRenderer dotRenderer = dotObject.AddComponent<SpriteRenderer>();
            dotRenderer.sprite = dotSprite;
            dotRenderer.sortingOrder = LoadingEffectSortingOrder + 1;
            dotRenderer.color = GetLoadingDotColor(i, dotCount, 1f);
            loadingSpinnerDots[i] = dotRenderer;
        }

    }

    private void AnimateLoadingEffect()
    {
        if (loadingEffectRoot == null)
        {
            return;
        }

        float elapsed = Time.unscaledTime - loadingEffectStartTime;
        float pulse = (Mathf.Sin(elapsed * 4.2f) + 1f) * 0.5f;

        if (loadingSpinnerRoot != null)
        {
            loadingSpinnerRoot.localRotation = Quaternion.Euler(0f, 0f, -elapsed * 185f);
        }

        if (loadingPulseRing != null)
        {
            float scale = 0.028f + pulse * 0.006f;
            loadingPulseRing.localScale = Vector3.one * scale;
        }

        if (loadingPulseRenderer != null)
        {
            loadingPulseRenderer.color = new Color(0.9f, 0.32f, 1f, 0.28f + pulse * 0.34f);
        }

        for (int i = 0; i < loadingSpinnerDots.Length; i++)
        {
            SpriteRenderer dot = loadingSpinnerDots[i];
            if (dot == null)
            {
                continue;
            }

            float dotPulse = (Mathf.Sin(elapsed * 6.4f + i * 0.55f) + 1f) * 0.5f;
            dot.transform.localScale = Vector3.one * Mathf.Lerp(0.11f, 0.22f, dotPulse);
            dot.color = GetLoadingDotColor(i, loadingSpinnerDots.Length, Mathf.Lerp(0.42f, 1f, dotPulse));
        }

    }

    private static Color GetLoadingDotColor(int index, int count, float alpha)
    {
        float t = count <= 1 ? 0f : index / (float)(count - 1);
        Color cyan = new Color(0.24f, 0.95f, 1f, alpha);
        Color pink = new Color(1f, 0.28f, 0.92f, alpha);
        Color white = new Color(0.98f, 1f, 1f, alpha);

        if (t < 0.5f)
        {
            return Color.Lerp(cyan, white, t * 2f);
        }

        return Color.Lerp(white, pink, (t - 0.5f) * 2f);
    }

    private static Sprite CreateRingSprite(int size, float radius, float thickness)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;
        Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float distance = Vector2.Distance(new Vector2(x, y), center);
                float ring = 1f - Mathf.SmoothStep(thickness * 0.55f, thickness, Mathf.Abs(distance - radius));
                float glow = 1f - Mathf.SmoothStep(thickness, thickness * 3.1f, Mathf.Abs(distance - radius));
                float alpha = Mathf.Clamp01(ring * 0.92f + glow * 0.24f);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    private static Sprite CreateSoftCircleSprite(int size)
    {
        Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;
        Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
        float radius = size * 0.34f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float distance = Vector2.Distance(new Vector2(x, y), center);
                float alpha = 1f - Mathf.SmoothStep(radius * 0.62f, radius, distance);
                texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }

        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
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
