using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem.UI;
#endif

public static class TitleScreenBuilder
{
    private const string ScenePath = "Assets/Scenes/TitleScene.unity";
    private const string LoadingScenePath = "Assets/Scenes/LoadingScene.unity";
    private const string SongSelectScenePath = "Assets/Scenes/SampleScene.unity";
    private const string ArtRoot = "Assets/TitleScreen/Art";
    private const string BackgroundPath = ArtRoot + "/illust.png";
    private const string LogoPath = ArtRoot + "/title_logo.png";
    private const string TouchPromptPath = ArtRoot + "/touch_screen.png";
    private const string PromptRailMaterialPath = "Assets/TitleScreen/Materials/PromptRailGlow.mat";
    private const string LogoSparkleMaterialPath = "Assets/TitleScreen/Materials/LogoSparkle.mat";
    private const string AutoBuildSessionKey = "TitleScreenBuilder.AutoBuildDone";

    [InitializeOnLoadMethod]
    private static void AutoBuildOnce()
    {
        EditorApplication.delayCall += () =>
        {
            ApplyPlayModeStartScene();

            string fullScenePath = Path.Combine(Directory.GetCurrentDirectory(), ScenePath);
            if (Application.isBatchMode || SessionState.GetBool(AutoBuildSessionKey, false) || File.Exists(fullScenePath))
            {
                return;
            }

            SessionState.SetBool(AutoBuildSessionKey, true);
            BuildTitleScene();
        };
    }

    private static void ApplyPlayModeStartScene()
    {
        SceneAsset titleScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
        if (titleScene != null && EditorSceneManager.playModeStartScene != titleScene)
        {
            EditorSceneManager.playModeStartScene = titleScene;
        }
    }

    [MenuItem("Tools/Title Screen/Rebuild Title Scene")]
    public static void BuildTitleScene()
    {
        Sprite backgroundSprite = PrepareSprite(BackgroundPath);
        Sprite logoSprite = PrepareSprite(LogoPath);
        Sprite touchPromptSprite = PrepareSprite(TouchPromptPath);

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        CreateCamera();
        Canvas canvas = CreateCanvas();
        RectTransform backgroundRect = CreateBackground(canvas.transform, backgroundSprite);
        CreatePromptRail(canvas.transform);

        List<RectTransform> logoEffectRects = new List<RectTransform>();
        List<CanvasGroup> logoGlowGroups = new List<CanvasGroup>();
        CreateLogoGlow(canvas.transform, logoSprite, "Title Logo Aura Pink", new Vector2(-6f, -218f), new Vector2(748f, 414f), new Color(1f, 0.2f, 0.95f, 0.34f), logoEffectRects, logoGlowGroups);
        CreateLogoGlow(canvas.transform, logoSprite, "Title Logo Aura Cyan", new Vector2(6f, -218f), new Vector2(766f, 424f), new Color(0.16f, 0.9f, 1f, 0.26f), logoEffectRects, logoGlowGroups);

        Image logoImage = CreateImage("Title Logo", canvas.transform, logoSprite, Color.white, false);
        RectTransform logoRect = logoImage.rectTransform;
        SetCentered(logoRect, new Vector2(0f, -218f), new Vector2(700f, 388f));

        List<RectTransform> sparkleRects = new List<RectTransform>();
        List<CanvasGroup> sparkleGroups = new List<CanvasGroup>();
        Material sparkleMaterial = AssetDatabase.LoadAssetAtPath<Material>(LogoSparkleMaterialPath);
        CreateLogoSparkle(canvas.transform, sparkleMaterial, "Logo Sparkle Top", new Vector2(-220f, -18f), 38f, 8f, new Color(1f, 1f, 1f, 0.88f), sparkleRects, sparkleGroups);
        CreateLogoSparkle(canvas.transform, sparkleMaterial, "Logo Sparkle Cyan", new Vector2(248f, -118f), 28f, -10f, new Color(0.4f, 0.95f, 1f, 0.8f), sparkleRects, sparkleGroups);
        CreateLogoSparkle(canvas.transform, sparkleMaterial, "Logo Sparkle Pink", new Vector2(-330f, -224f), 24f, 18f, new Color(1f, 0.35f, 0.95f, 0.78f), sparkleRects, sparkleGroups);
        CreateLogoSparkle(canvas.transform, sparkleMaterial, "Logo Sparkle Small", new Vector2(322f, -270f), 18f, -18f, new Color(0.86f, 0.78f, 1f, 0.7f), sparkleRects, sparkleGroups);

        GameObject promptObject = new GameObject("Touch Prompt", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
        promptObject.transform.SetParent(canvas.transform, false);
        Image promptImage = promptObject.GetComponent<Image>();
        promptImage.sprite = touchPromptSprite;
        promptImage.preserveAspect = true;
        promptImage.raycastTarget = false;
        CanvasGroup promptGroup = promptObject.GetComponent<CanvasGroup>();
        RectTransform promptRect = promptObject.GetComponent<RectTransform>();
        SetBottomCentered(promptRect, new Vector2(0f, 82f), new Vector2(760f, 82f));

        TitleScreenController controller = CreateController(backgroundRect, logoRect, promptGroup, logoEffectRects, logoGlowGroups, sparkleRects, sparkleGroups);
        CreateInputArea(canvas.transform, controller);
        CreateEventSystem();

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), ScenePath);
        UpdateBuildSettings();
        ApplyPlayModeStartScene();

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"Title scene rebuilt at {ScenePath}");
    }

    private static Sprite PrepareSprite(string assetPath)
    {
        TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null)
        {
            throw new FileNotFoundException($"Could not find texture asset at {assetPath}");
        }

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.alphaSource = TextureImporterAlphaSource.FromInput;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.filterMode = FilterMode.Bilinear;
        importer.maxTextureSize = 2048;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();

        Sprite sprite = AssetDatabase.LoadAssetAtPath<Sprite>(assetPath);
        if (sprite == null)
        {
            throw new FileNotFoundException($"Could not load sprite from {assetPath}");
        }

        return sprite;
    }

    private static void CreateCamera()
    {
        GameObject cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
        cameraObject.tag = "MainCamera";
        cameraObject.transform.position = new Vector3(0f, 0f, -10f);

        Camera camera = cameraObject.GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.09f, 0.02f, 0.16f, 1f);
        camera.orthographic = true;
        camera.orthographicSize = 5f;
    }

    private static Canvas CreateCanvas()
    {
        GameObject canvasObject = new GameObject("Title Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        Canvas canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 0;

        CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        return canvas;
    }

    private static RectTransform CreateBackground(Transform parent, Sprite sprite)
    {
        Image background = CreateImage("Background", parent, sprite, Color.white, false);
        background.preserveAspect = false;

        RectTransform rect = background.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(1920f, 1080f);

        AspectRatioFitter fitter = background.gameObject.AddComponent<AspectRatioFitter>();
        fitter.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent;
        fitter.aspectRatio = 16f / 9f;

        return rect;
    }

    private static void CreatePromptRail(Transform parent)
    {
        Image rail = CreateSolid("Prompt Rail", parent, Color.white);
        rail.material = AssetDatabase.LoadAssetAtPath<Material>(PromptRailMaterialPath);
        RectTransform railRect = rail.rectTransform;
        railRect.anchorMin = new Vector2(0f, 0f);
        railRect.anchorMax = new Vector2(1f, 0f);
        railRect.pivot = new Vector2(0.5f, 0f);
        railRect.anchoredPosition = Vector2.zero;
        railRect.sizeDelta = new Vector2(0f, 150f);

    }

    private static void CreateLogoGlow(Transform parent, Sprite logoSprite, string name, Vector2 position, Vector2 size, Color color, List<RectTransform> effectRects, List<CanvasGroup> glowGroups)
    {
        GameObject glowObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
        glowObject.transform.SetParent(parent, false);

        Image image = glowObject.GetComponent<Image>();
        image.sprite = logoSprite;
        image.color = color;
        image.preserveAspect = true;
        image.raycastTarget = false;

        CanvasGroup canvasGroup = glowObject.GetComponent<CanvasGroup>();
        canvasGroup.alpha = color.a;
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;

        RectTransform rect = glowObject.GetComponent<RectTransform>();
        SetCentered(rect, position, size);

        effectRects.Add(rect);
        glowGroups.Add(canvasGroup);
    }

    private static void CreateLogoSparkle(Transform parent, Material material, string name, Vector2 position, float size, float rotation, Color color, List<RectTransform> sparkleRects, List<CanvasGroup> sparkleGroups)
    {
        GameObject sparkleObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
        sparkleObject.transform.SetParent(parent, false);

        Image image = sparkleObject.GetComponent<Image>();
        image.color = color;
        image.material = material;
        image.raycastTarget = false;

        CanvasGroup canvasGroup = sparkleObject.GetComponent<CanvasGroup>();
        canvasGroup.alpha = color.a;
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;

        RectTransform rect = sparkleObject.GetComponent<RectTransform>();
        SetCentered(rect, position, new Vector2(size, size));
        rect.localRotation = Quaternion.Euler(0f, 0f, rotation);

        sparkleRects.Add(rect);
        sparkleGroups.Add(canvasGroup);
    }

    private static void CreateRailLine(string name, Transform parent, float y, float alpha)
    {
        Image line = CreateSolid(name, parent, new Color(1f, 1f, 1f, alpha));
        RectTransform rect = line.rectTransform;
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, y);
        rect.sizeDelta = new Vector2(0f, 2f);
    }

    private static TitleScreenController CreateController(RectTransform background, RectTransform logo, CanvasGroup touchPromptGroup, IReadOnlyList<RectTransform> logoEffectRects, IReadOnlyList<CanvasGroup> logoGlowGroups, IReadOnlyList<RectTransform> sparkleRects, IReadOnlyList<CanvasGroup> sparkleGroups)
    {
        GameObject controllerObject = new GameObject("Title Screen Controller");
        TitleScreenController controller = controllerObject.AddComponent<TitleScreenController>();

        SerializedObject serializedController = new SerializedObject(controller);
        serializedController.FindProperty("nextSceneName").stringValue = "LoadingScene";
        serializedController.FindProperty("background").objectReferenceValue = background;
        serializedController.FindProperty("logo").objectReferenceValue = logo;
        serializedController.FindProperty("touchPromptGroup").objectReferenceValue = touchPromptGroup;
        serializedController.FindProperty("logoFloatAmplitude").floatValue = 8f;
        serializedController.FindProperty("logoFloatSpeed").floatValue = 1.2f;
        serializedController.FindProperty("promptBlinkSpeed").floatValue = 3.2f;
        serializedController.FindProperty("promptMinAlpha").floatValue = 0.42f;
        serializedController.FindProperty("backgroundParallaxAmount").vector2Value = new Vector2(12f, 7f);
        serializedController.FindProperty("backgroundParallaxSpeed").floatValue = 0.18f;
        serializedController.FindProperty("backgroundParallaxScale").floatValue = 0.006f;
        SetObjectArray(serializedController.FindProperty("logoEffectTransforms"), logoEffectRects);
        SetObjectArray(serializedController.FindProperty("logoGlowGroups"), logoGlowGroups);
        SetObjectArray(serializedController.FindProperty("logoSparkleTransforms"), sparkleRects);
        SetObjectArray(serializedController.FindProperty("logoSparkleGroups"), sparkleGroups);
        serializedController.FindProperty("logoGlowPulseSpeed").floatValue = 2.15f;
        serializedController.FindProperty("logoGlowScalePulse").floatValue = 0.045f;
        serializedController.FindProperty("sparkleTwinkleSpeed").floatValue = 3.1f;
        serializedController.FindProperty("sparkleDriftAmount").floatValue = 4f;
        serializedController.FindProperty("useStartReaction").boolValue = true;
        serializedController.FindProperty("startReactionDuration").floatValue = 0.72f;
        serializedController.FindProperty("startFlashMaxAlpha").floatValue = 0.34f;
        serializedController.FindProperty("startLogoPunchScale").floatValue = 0.085f;
        serializedController.FindProperty("startBurstParticleRatio").floatValue = 0.22f;
        serializedController.FindProperty("useSkyStarParticles").boolValue = true;
        serializedController.FindProperty("skyStarParticleCount").intValue = 36;
        serializedController.FindProperty("skyStarSizeRange").vector2Value = new Vector2(14f, 54f);
        serializedController.FindProperty("skyStarAlphaRange").vector2Value = new Vector2(0.38f, 0.9f);
        serializedController.FindProperty("skyStarTwinkleSpeed").floatValue = 1.65f;
        serializedController.FindProperty("useLogoBurstParticles").boolValue = true;
        serializedController.FindProperty("logoParticleMaterial").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Material>(LogoSparkleMaterialPath);
        serializedController.FindProperty("logoBurstParticleCount").intValue = 115;
        serializedController.FindProperty("logoBurstEllipse").vector2Value = new Vector2(300f, 100f);
        serializedController.FindProperty("logoBurstExpandAmount").floatValue = 140f;
        serializedController.FindProperty("logoBurstLifetimeRange").vector2Value = new Vector2(6.2f, 9.2f);
        serializedController.FindProperty("logoBurstSpawnDelayRange").vector2Value = new Vector2(0f, 0.03f);
        serializedController.FindProperty("logoBurstAlpha").floatValue = 1f;
        serializedController.FindProperty("logoBurstSize").floatValue = 17f;
        serializedController.ApplyModifiedPropertiesWithoutUndo();

        return controller;
    }

    private static void SetObjectArray<T>(SerializedProperty property, IReadOnlyList<T> references) where T : UnityEngine.Object
    {
        property.arraySize = references.Count;
        for (int i = 0; i < references.Count; i++)
        {
            property.GetArrayElementAtIndex(i).objectReferenceValue = references[i];
        }
    }

    private static void CreateInputArea(Transform parent, TitleScreenController controller)
    {
        GameObject inputObject = new GameObject("Full Screen Input Area", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        inputObject.transform.SetParent(parent, false);

        Image image = inputObject.GetComponent<Image>();
        image.color = new Color(1f, 1f, 1f, 0f);
        image.raycastTarget = true;

        Button button = inputObject.GetComponent<Button>();
        button.transition = Selectable.Transition.None;
        button.targetGraphic = image;
        UnityEventTools.AddPersistentListener(button.onClick, controller.RequestStart);

        Stretch(inputObject.GetComponent<RectTransform>());
    }

    private static void CreateEventSystem()
    {
        GameObject eventSystemObject = new GameObject("EventSystem", typeof(EventSystem));

#if ENABLE_INPUT_SYSTEM
        eventSystemObject.AddComponent<InputSystemUIInputModule>();
#else
        eventSystemObject.AddComponent<StandaloneInputModule>();
#endif
    }

    private static Image CreateImage(string name, Transform parent, Sprite sprite, Color color, bool raycastTarget)
    {
        GameObject gameObject = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        gameObject.transform.SetParent(parent, false);

        Image image = gameObject.GetComponent<Image>();
        image.sprite = sprite;
        image.color = color;
        image.preserveAspect = true;
        image.raycastTarget = raycastTarget;

        return image;
    }

    private static Image CreateSolid(string name, Transform parent, Color color)
    {
        Image image = CreateImage(name, parent, null, color, false);
        image.type = Image.Type.Simple;
        return image;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.localScale = Vector3.one;
    }

    private static void SetCentered(RectTransform rect, Vector2 position, Vector2 size)
    {
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }

    private static void SetBottomCentered(RectTransform rect, Vector2 position, Vector2 size)
    {
        rect.anchorMin = new Vector2(0.5f, 0f);
        rect.anchorMax = new Vector2(0.5f, 0f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = position;
        rect.sizeDelta = size;
    }

    private static void UpdateBuildSettings()
    {
        List<EditorBuildSettingsScene> scenes = EditorBuildSettings.scenes
            .Where(scene => scene.path != ScenePath && scene.path != LoadingScenePath && scene.path != SongSelectScenePath)
            .ToList();

        scenes.Insert(0, new EditorBuildSettingsScene(SongSelectScenePath, true));
        scenes.Insert(0, new EditorBuildSettingsScene(LoadingScenePath, true));
        scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
        EditorBuildSettings.scenes = scenes.ToArray();
    }
}
