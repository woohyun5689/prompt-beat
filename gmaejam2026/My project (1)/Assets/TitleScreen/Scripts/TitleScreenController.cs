using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public sealed class TitleScreenController : MonoBehaviour
{
    [Header("Scene Flow")]
    [SerializeField] private string nextSceneName = "";
    [SerializeField] private UnityEvent onStartRequested;

    [Header("Animation")]
    [SerializeField] private RectTransform background;
    [SerializeField] private RectTransform logo;
    [SerializeField] private CanvasGroup touchPromptGroup;
    [SerializeField] private float logoFloatAmplitude = 8f;
    [SerializeField] private float logoFloatSpeed = 1.2f;
    [SerializeField] private float promptBlinkSpeed = 3.2f;
    [SerializeField] private float promptMinAlpha = 0.42f;

    [Header("Background Parallax")]
    [SerializeField] private Vector2 backgroundParallaxAmount = new Vector2(12f, 7f);
    [SerializeField] private float backgroundParallaxSpeed = 0.18f;
    [SerializeField] private float backgroundParallaxScale = 0.006f;

    [Header("Logo Effects")]
    [SerializeField] private RectTransform[] logoEffectTransforms = Array.Empty<RectTransform>();
    [SerializeField] private CanvasGroup[] logoGlowGroups = Array.Empty<CanvasGroup>();
    [SerializeField] private RectTransform[] logoSparkleTransforms = Array.Empty<RectTransform>();
    [SerializeField] private CanvasGroup[] logoSparkleGroups = Array.Empty<CanvasGroup>();
    [SerializeField] private float logoGlowPulseSpeed = 2.15f;
    [SerializeField] private float logoGlowScalePulse = 0.045f;
    [SerializeField] private float sparkleTwinkleSpeed = 3.1f;
    [SerializeField] private float sparkleDriftAmount = 4f;

    [Header("Start Reaction")]
    [SerializeField] private bool useStartReaction = true;
    [SerializeField] private float startReactionDuration = 0.72f;
    [SerializeField] private float startFlashMaxAlpha = 0.34f;
    [SerializeField] private float startLogoPunchScale = 0.085f;
    [SerializeField] private float startBurstParticleRatio = 0.48f;

    [Header("Sky Star Particles")]
    [SerializeField] private bool useSkyStarParticles = true;
    [SerializeField] private int skyStarParticleCount = 36;
    [SerializeField] private Vector2 skyStarSizeRange = new Vector2(14f, 54f);
    [SerializeField] private Vector2 skyStarAlphaRange = new Vector2(0.38f, 0.9f);
    [SerializeField] private float skyStarTwinkleSpeed = 1.65f;

    [Header("Logo Burst Particles")]
    [SerializeField] private bool useLogoBurstParticles = true;
    [SerializeField] private Material logoParticleMaterial;
    [SerializeField] private int logoBurstParticleCount = 650;
    [SerializeField] private Vector2 logoBurstEllipse = new Vector2(360f, 130f);
    [SerializeField] private float logoBurstExpandAmount = 300f;
    [SerializeField] private Vector2 logoBurstLifetimeRange = new Vector2(6.2f, 9.2f);
    [SerializeField] private Vector2 logoBurstSpawnDelayRange = new Vector2(0f, 0.03f);
    [SerializeField] private float logoBurstAlpha = 1f;
    [SerializeField] private float logoBurstSize = 420f;

    private Vector2 backgroundStartPosition;
    private Vector3 backgroundStartScale = Vector3.one;
    private Vector2 logoStartPosition;
    private Vector3 logoStartScale = Vector3.one;
    private Vector2[] logoEffectStartPositions = Array.Empty<Vector2>();
    private Vector3[] logoEffectStartScales = Array.Empty<Vector3>();
    private Vector2[] logoSparkleStartPositions = Array.Empty<Vector2>();
    private Vector3[] logoSparkleStartScales = Array.Empty<Vector3>();
    private Quaternion[] logoSparkleStartRotations = Array.Empty<Quaternion>();
    private SkyStarParticle[] skyStarParticles = Array.Empty<SkyStarParticle>();
    private LogoBurstParticle[] logoBurstParticles = Array.Empty<LogoBurstParticle>();
    private Image startFlashImage;
    private Material runtimeSkyStarMaterial;
    private Material runtimeLogoParticleMaterial;
    private float startReactionTime = -1000f;
    private bool startRequested;

    private struct SkyStarParticle
    {
        public RectTransform Rect;
        public CanvasGroup Group;
        public Vector2 BasePosition;
        public Vector2 Drift;
        public float Phase;
        public float Size;
        public float RotationSpeed;
        public float Alpha;
    }

    private struct LogoBurstParticle
    {
        public RectTransform Rect;
        public Image Image;
        public CanvasGroup Group;
        public Vector2 Direction;
        public Vector2 SpawnOffset;
        public Vector2 EndOffset;
        public float Phase;
        public float Size;
        public float SpinSpeed;
        public float Age;
        public float Lifetime;
        public Color Color;
    }

    private void Awake()
    {
        CacheBackgroundState();

        if (logo != null)
        {
            logoStartPosition = logo.anchoredPosition;
            logoStartScale = logo.localScale;
        }

        CacheLogoEffectState();
        CreateStartReactionOverlay();
        CreateSkyStarParticles();
        CreateLogoBurstParticles();
    }

    private void Update()
    {
        AnimateTitle();

        if (!startRequested && WasStartPressed())
        {
            RequestStart();
        }
    }

    public void RequestStart()
    {
        if (startRequested)
        {
            return;
        }

        startRequested = true;
        onStartRequested?.Invoke();
        TriggerStartReaction();

        if (!string.IsNullOrWhiteSpace(nextSceneName))
        {
            StartCoroutine(LoadNextSceneAfterReaction());
            return;
        }

        Debug.Log("Title screen start requested. Assign Next Scene Name on TitleScreenController to continue.");
    }

    private void AnimateTitle()
    {
        float time = Time.unscaledTime;
        AnimateBackgroundParallax(time);
        AnimateStartReaction(time);
        AnimateSkyStarParticles(time);

        if (logo != null)
        {
            float y = Mathf.Sin(time * logoFloatSpeed) * logoFloatAmplitude;
            logo.anchoredPosition = logoStartPosition + Vector2.up * y;
            logo.localScale = logoStartScale * (1f + GetStartReactionPunch(time));
            AnimateLogoEffects(time, y);
            AnimateLogoBurstParticles(time, y);
        }

        if (touchPromptGroup != null)
        {
            float blink = (Mathf.Sin(time * promptBlinkSpeed) + 1f) * 0.5f;
            float promptAlpha = Mathf.Lerp(promptMinAlpha, 1f, blink);
            if (startRequested)
            {
                promptAlpha *= 1f - Smooth01(GetStartReactionProgress(time));
            }

            touchPromptGroup.alpha = promptAlpha;
        }
    }

    private IEnumerator LoadNextSceneAfterReaction()
    {
        yield return new WaitForSecondsRealtime(useStartReaction ? startReactionDuration : 0f);
        SceneManager.LoadScene(nextSceneName);
    }

    private void CacheBackgroundState()
    {
        if (background == null && logo != null && logo.parent != null)
        {
            Transform backgroundTransform = logo.parent.Find("Background");
            if (backgroundTransform != null)
            {
                background = backgroundTransform.GetComponent<RectTransform>();
            }
        }

        if (background == null)
        {
            return;
        }

        backgroundStartPosition = background.anchoredPosition;
        backgroundStartScale = background.localScale;
    }

    private void AnimateBackgroundParallax(float time)
    {
        if (background == null)
        {
            return;
        }

        Vector2 offset = new Vector2(
            Mathf.Sin(time * backgroundParallaxSpeed) * backgroundParallaxAmount.x,
            Mathf.Cos(time * backgroundParallaxSpeed * 0.73f) * backgroundParallaxAmount.y);
        float scalePulse = (Mathf.Sin(time * backgroundParallaxSpeed * 0.57f) + 1f) * 0.5f;

        background.anchoredPosition = backgroundStartPosition + offset;
        background.localScale = backgroundStartScale * (1f + scalePulse * backgroundParallaxScale);
    }

    private void CreateStartReactionOverlay()
    {
        if (!useStartReaction || logo == null || logo.parent == null)
        {
            return;
        }

        GameObject flashObject = new GameObject("Start Reaction Flash", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        flashObject.layer = logo.gameObject.layer;
        flashObject.transform.SetParent(logo.parent, false);
        flashObject.transform.SetAsLastSibling();

        RectTransform rect = flashObject.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = Vector2.zero;

        startFlashImage = flashObject.GetComponent<Image>();
        startFlashImage.color = new Color(1f, 0.9f, 1f, 0f);
        startFlashImage.raycastTarget = false;
    }

    private void TriggerStartReaction()
    {
        if (!useStartReaction)
        {
            return;
        }

        startReactionTime = Time.unscaledTime;
        TriggerLogoBurstReaction();
    }

    private void AnimateStartReaction(float time)
    {
        if (startFlashImage == null)
        {
            return;
        }

        float progress = GetStartReactionProgress(time);
        if (progress >= 1f)
        {
            startFlashImage.color = new Color(1f, 0.9f, 1f, 0f);
            return;
        }

        float flash = Mathf.Sin(progress * Mathf.PI);
        startFlashImage.color = new Color(1f, 0.9f, 1f, flash * startFlashMaxAlpha);
    }

    private float GetStartReactionProgress(float time)
    {
        if (!useStartReaction || startReactionTime < 0f)
        {
            return 1f;
        }

        return Mathf.Clamp01((time - startReactionTime) / Mathf.Max(0.01f, startReactionDuration));
    }

    private float GetStartReactionPunch(float time)
    {
        float progress = GetStartReactionProgress(time);
        if (progress >= 1f)
        {
            return 0f;
        }

        return Mathf.Sin(progress * Mathf.PI) * startLogoPunchScale;
    }

    private void CreateSkyStarParticles()
    {
        if (!useSkyStarParticles || logo == null || logo.parent == null || skyStarParticleCount <= 0)
        {
            skyStarParticles = Array.Empty<SkyStarParticle>();
            return;
        }

        Material starMaterial;
        if (logoParticleMaterial == null)
        {
            Shader shader = Shader.Find("UI/TitleScreen/LogoSparkle");
            if (shader == null)
            {
                skyStarParticles = Array.Empty<SkyStarParticle>();
                return;
            }

            runtimeSkyStarMaterial = new Material(shader)
            {
                name = "Runtime Sky Star Material"
            };
        }
        else
        {
            runtimeSkyStarMaterial = new Material(logoParticleMaterial)
            {
                name = "Runtime Sky Star Material"
            };
        }

        if (runtimeSkyStarMaterial.HasProperty("_Core"))
        {
            runtimeSkyStarMaterial.SetFloat("_Core", 0.055f);
        }

        if (runtimeSkyStarMaterial.HasProperty("_RaySharpness"))
        {
            runtimeSkyStarMaterial.SetFloat("_RaySharpness", 24f);
        }

        starMaterial = runtimeSkyStarMaterial;

        Transform parent = logo.parent;
        skyStarParticles = new SkyStarParticle[skyStarParticleCount];
        int siblingIndex = Mathf.Min(1, parent.childCount);

        for (int i = 0; i < skyStarParticles.Length; i++)
        {
            GameObject starObject = new GameObject($"Sky Star Particle {i + 1:00}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
            starObject.layer = logo.gameObject.layer;
            starObject.transform.SetParent(parent, false);
            starObject.transform.SetSiblingIndex(Mathf.Min(siblingIndex + i, parent.childCount - 1));

            RectTransform rect = starObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            Image image = starObject.GetComponent<Image>();
            image.material = starMaterial;
            image.color = RandomSkyStarColor();
            image.raycastTarget = false;

            CanvasGroup group = starObject.GetComponent<CanvasGroup>();
            group.interactable = false;
            group.blocksRaycasts = false;

            float size = RandomRange(skyStarSizeRange);
            Vector2 basePosition = RandomSkyStarPosition(i);
            rect.sizeDelta = Vector2.one * size;
            rect.anchoredPosition = basePosition;
            rect.localRotation = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));

            skyStarParticles[i] = new SkyStarParticle
            {
                Rect = rect,
                Group = group,
                BasePosition = basePosition,
                Drift = new Vector2(UnityEngine.Random.Range(8f, 30f), UnityEngine.Random.Range(5f, 22f)),
                Phase = UnityEngine.Random.Range(0f, Mathf.PI * 2f),
                Size = size,
                RotationSpeed = UnityEngine.Random.Range(-10f, 16f),
                Alpha = RandomRange(skyStarAlphaRange)
            };
        }
    }

    private void AnimateSkyStarParticles(float time)
    {
        for (int i = 0; i < skyStarParticles.Length; i++)
        {
            SkyStarParticle particle = skyStarParticles[i];
            if (particle.Rect == null || particle.Group == null)
            {
                continue;
            }

            float twinkle = (Mathf.Sin(time * skyStarTwinkleSpeed + particle.Phase) + 1f) * 0.5f;
            float slowTwinkle = (Mathf.Sin(time * 0.48f + particle.Phase * 0.67f) + 1f) * 0.5f;
            Vector2 drift = new Vector2(
                Mathf.Sin(time * 0.36f + particle.Phase) * particle.Drift.x,
                Mathf.Cos(time * 0.3f + particle.Phase * 1.27f) * particle.Drift.y);

            particle.Rect.anchoredPosition = particle.BasePosition + drift;
            particle.Rect.localScale = Vector3.one * Mathf.Lerp(0.72f, 1.32f, twinkle);
            particle.Rect.localRotation = Quaternion.Euler(0f, 0f, time * particle.RotationSpeed + particle.Phase * 34f);
            particle.Group.alpha = Mathf.Lerp(particle.Alpha * 0.28f, particle.Alpha, Mathf.Max(twinkle, slowTwinkle * 0.82f));

            skyStarParticles[i] = particle;
        }
    }

    private void CreateLogoBurstParticles()
    {
        if (!useLogoBurstParticles || logo == null || logo.parent == null || logoBurstParticleCount <= 0)
        {
            logoBurstParticles = Array.Empty<LogoBurstParticle>();
            return;
        }

        Material particleMaterial;
        if (logoParticleMaterial == null)
        {
            Shader shader = Shader.Find("UI/TitleScreen/LogoSparkle");
            if (shader == null)
            {
                logoBurstParticles = Array.Empty<LogoBurstParticle>();
                return;
            }

            runtimeLogoParticleMaterial = new Material(shader)
            {
                name = "Runtime Logo Burst Particle Material"
            };
        }
        else
        {
            runtimeLogoParticleMaterial = new Material(logoParticleMaterial)
            {
                name = "Runtime Logo Burst Particle Material"
            };
        }

        if (runtimeLogoParticleMaterial.HasProperty("_Core"))
        {
            runtimeLogoParticleMaterial.SetFloat("_Core", 0.2f);
        }

        if (runtimeLogoParticleMaterial.HasProperty("_RaySharpness"))
        {
            runtimeLogoParticleMaterial.SetFloat("_RaySharpness", 8f);
        }

        particleMaterial = runtimeLogoParticleMaterial;

        Transform parent = logo.parent;
        logoBurstParticles = new LogoBurstParticle[logoBurstParticleCount];
        int siblingIndex = logo.GetSiblingIndex();

        for (int i = 0; i < logoBurstParticles.Length; i++)
        {
            GameObject particleObject = new GameObject($"Logo Burst Particle {i + 1:00}", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(CanvasGroup));
            particleObject.layer = logo.gameObject.layer;
            particleObject.transform.SetParent(parent, false);
            particleObject.transform.SetSiblingIndex(siblingIndex + i);

            RectTransform rect = particleObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Vector2.one * logoBurstSize;
            rect.anchoredPosition = logoStartPosition;
            rect.localScale = Vector3.zero;

            Image image = particleObject.GetComponent<Image>();
            image.material = particleMaterial;
            image.raycastTarget = false;

            CanvasGroup group = particleObject.GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.interactable = false;
            group.blocksRaycasts = false;

            logoBurstParticles[i] = new LogoBurstParticle
            {
                Rect = rect,
                Image = image,
                Group = group,
            };

            RespawnLogoBurstParticle(i, true);
        }
    }

    private void AnimateLogoBurstParticles(float time, float floatOffset)
    {
        for (int i = 0; i < logoBurstParticles.Length; i++)
        {
            LogoBurstParticle particle = logoBurstParticles[i];
            if (particle.Rect == null || particle.Group == null)
            {
                continue;
            }

            particle.Age += Time.unscaledDeltaTime;
            if (particle.Age < 0f)
            {
                particle.Group.alpha = 0f;
                logoBurstParticles[i] = particle;
                continue;
            }

            if (particle.Age >= particle.Lifetime)
            {
                logoBurstParticles[i] = particle;
                RespawnLogoBurstParticle(i, false);
                continue;
            }

            float progress = Mathf.Clamp01(particle.Age / Mathf.Max(0.01f, particle.Lifetime));
            float easeOut = 1f - Mathf.Pow(1f - progress, 2.4f);
            float scaleIn = Smooth01(progress / 0.1f);
            float fadeOut = 1f - Smooth01(Mathf.InverseLerp(0.78f, 1f, progress));
            float scaleOut = 1f - Smooth01(Mathf.InverseLerp(0.72f, 1f, progress)) * 0.18f;
            Vector2 drift = new Vector2(
                Mathf.Sin(time * 1.4f + particle.Phase) * 7f,
                Mathf.Cos(time * 1.1f + particle.Phase * 0.7f) * 5f);
            Vector2 offset = Vector2.Lerp(particle.SpawnOffset, particle.EndOffset, easeOut) + drift;

            particle.Rect.anchoredPosition = logoStartPosition + Vector2.up * floatOffset + offset;
            particle.Rect.sizeDelta = Vector2.one * particle.Size;
            particle.Rect.localScale = Vector3.one * Mathf.Lerp(0.96f, 1.24f, scaleIn) * scaleOut;
            particle.Rect.localRotation = Quaternion.Euler(0f, 0f, time * particle.SpinSpeed + particle.Phase * 23f);
            particle.Group.alpha = logoBurstAlpha * fadeOut;

            logoBurstParticles[i] = particle;
        }
    }

    private void TriggerLogoBurstReaction()
    {
        if (logoBurstParticles.Length == 0)
        {
            return;
        }

        int reactionCount = Mathf.CeilToInt(logoBurstParticles.Length * Mathf.Clamp01(startBurstParticleRatio));
        for (int i = 0; i < reactionCount; i++)
        {
            int index = (i * 37) % logoBurstParticles.Length;
            RespawnLogoBurstParticle(index, false);

            LogoBurstParticle particle = logoBurstParticles[index];
            if (particle.Rect == null || particle.Group == null)
            {
                continue;
            }

            particle.Age = UnityEngine.Random.Range(0f, 0.08f);
            particle.Lifetime = UnityEngine.Random.Range(1.35f, 2.15f);
            particle.EndOffset *= UnityEngine.Random.Range(1.16f, 1.36f);
            particle.Size *= UnityEngine.Random.Range(1.08f, 1.26f);
            particle.Group.alpha = 1f;
            logoBurstParticles[index] = particle;
        }
    }

    private void RespawnLogoBurstParticle(int index, bool useInitialDelay)
    {
        LogoBurstParticle particle = logoBurstParticles[index];
        if (particle.Rect == null || particle.Group == null)
        {
            return;
        }

        float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        Vector2 direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
        float spawnRadius = UnityEngine.Random.Range(0.82f, 1.18f);
        float outwardDistance = UnityEngine.Random.Range(logoBurstExpandAmount * 0.35f, logoBurstExpandAmount * 0.92f);
        Vector2 tangent = new Vector2(-direction.y, direction.x) * UnityEngine.Random.Range(-92f, 92f);

        particle.Direction = direction;
        particle.SpawnOffset = new Vector2(direction.x * logoBurstEllipse.x * spawnRadius, direction.y * logoBurstEllipse.y * spawnRadius);
        particle.EndOffset = particle.SpawnOffset + direction * outwardDistance + tangent;
        particle.Phase = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        particle.Size = logoBurstSize * UnityEngine.Random.Range(0.88f, 1.42f);
        particle.SpinSpeed = UnityEngine.Random.Range(-115f, 115f);
        particle.Age = -(useInitialDelay ? UnityEngine.Random.Range(0f, 0.18f) : RandomRange(logoBurstSpawnDelayRange));
        particle.Lifetime = RandomRange(logoBurstLifetimeRange);
        particle.Color = RandomBurstColor();

        particle.Rect.anchoredPosition = logoStartPosition + particle.SpawnOffset;
        particle.Rect.sizeDelta = Vector2.one * particle.Size;
        particle.Rect.localScale = Vector3.zero;
        particle.Rect.localRotation = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
        particle.Group.alpha = 0f;

        if (particle.Image != null)
        {
            particle.Image.color = particle.Color;
        }

        logoBurstParticles[index] = particle;
    }

    private static Vector2 RandomSkyStarPosition(int index)
    {
        Vector2 anchor;
        switch (index % 9)
        {
            case 0:
                anchor = new Vector2(-770f, 420f);
                break;
            case 1:
                anchor = new Vector2(-650f, 265f);
                break;
            case 2:
                anchor = new Vector2(-485f, 72f);
                break;
            case 3:
                anchor = new Vector2(-285f, 430f);
                break;
            case 4:
                anchor = new Vector2(-710f, 335f);
                break;
            case 5:
                anchor = new Vector2(-555f, 165f);
                break;
            case 6:
                anchor = new Vector2(-405f, 320f);
                break;
            case 7:
                anchor = new Vector2(-220f, 385f);
                break;
            default:
                anchor = new Vector2(-825f, 250f);
                break;
        }

        return anchor + new Vector2(UnityEngine.Random.Range(-42f, 42f), UnityEngine.Random.Range(-34f, 34f));
    }

    private static Color RandomSkyStarColor()
    {
        float pick = UnityEngine.Random.value;
        if (pick < 0.34f)
        {
            return new Color(1f, 1f, 1f, 1f);
        }

        if (pick < 0.58f)
        {
            return new Color(1f, 1f, 0.58f, 1f);
        }

        if (pick < 0.78f)
        {
            return new Color(0.62f, 0.95f, 1f, 1f);
        }

        return new Color(1f, 0.72f, 1f, 1f);
    }

    private static float RandomRange(Vector2 range)
    {
        float min = Mathf.Min(range.x, range.y);
        float max = Mathf.Max(range.x, range.y);
        return Mathf.Approximately(min, max) ? min : UnityEngine.Random.Range(min, max);
    }

    private static float Smooth01(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }

    private static Color RandomBurstColor()
    {
        float pick = UnityEngine.Random.value;
        if (pick < 0.25f)
        {
            return Color.white;
        }

        if (pick < 0.5f)
        {
            return new Color(1f, 0.58f, 0.9f, 1f);
        }

        if (pick < 0.75f)
        {
            return new Color(0.68f, 0.48f, 1f, 1f);
        }

        return new Color(0.42f, 0.82f, 1f, 1f);
    }

    private void CacheLogoEffectState()
    {
        logoEffectStartPositions = new Vector2[logoEffectTransforms.Length];
        logoEffectStartScales = new Vector3[logoEffectTransforms.Length];

        for (int i = 0; i < logoEffectTransforms.Length; i++)
        {
            RectTransform effect = logoEffectTransforms[i];
            if (effect == null)
            {
                continue;
            }

            logoEffectStartPositions[i] = effect.anchoredPosition;
            logoEffectStartScales[i] = effect.localScale;
        }

        logoSparkleStartPositions = new Vector2[logoSparkleTransforms.Length];
        logoSparkleStartScales = new Vector3[logoSparkleTransforms.Length];
        logoSparkleStartRotations = new Quaternion[logoSparkleTransforms.Length];

        for (int i = 0; i < logoSparkleTransforms.Length; i++)
        {
            RectTransform sparkle = logoSparkleTransforms[i];
            if (sparkle == null)
            {
                continue;
            }

            logoSparkleStartPositions[i] = sparkle.anchoredPosition;
            logoSparkleStartScales[i] = sparkle.localScale;
            logoSparkleStartRotations[i] = sparkle.localRotation;
        }
    }

    private void AnimateLogoEffects(float time, float floatOffset)
    {
        for (int i = 0; i < logoEffectTransforms.Length; i++)
        {
            RectTransform effect = logoEffectTransforms[i];
            if (effect == null)
            {
                continue;
            }

            float pulse = (Mathf.Sin(time * logoGlowPulseSpeed + i * 1.4f) + 1f) * 0.5f;
            effect.anchoredPosition = logoEffectStartPositions[i] + Vector2.up * floatOffset;
            effect.localScale = logoEffectStartScales[i] * (1f + pulse * logoGlowScalePulse);

            if (i < logoGlowGroups.Length && logoGlowGroups[i] != null)
            {
                logoGlowGroups[i].alpha = Mathf.Lerp(0.18f, 0.48f, pulse);
            }
        }

        for (int i = 0; i < logoSparkleTransforms.Length; i++)
        {
            RectTransform sparkle = logoSparkleTransforms[i];
            if (sparkle == null)
            {
                continue;
            }

            float twinkle = (Mathf.Sin(time * sparkleTwinkleSpeed + i * 1.75f) + 1f) * 0.5f;
            float driftX = Mathf.Sin(time * (0.85f + i * 0.11f) + i) * sparkleDriftAmount;
            float driftY = Mathf.Cos(time * (0.75f + i * 0.09f) + i * 1.3f) * sparkleDriftAmount;

            sparkle.anchoredPosition = logoSparkleStartPositions[i] + new Vector2(driftX, floatOffset + driftY);
            sparkle.localScale = logoSparkleStartScales[i] * Mathf.Lerp(0.72f, 1.18f, twinkle);
            sparkle.localRotation = logoSparkleStartRotations[i] * Quaternion.Euler(0f, 0f, time * (18f + i * 7f));

            if (i < logoSparkleGroups.Length && logoSparkleGroups[i] != null)
            {
                logoSparkleGroups[i].alpha = Mathf.Lerp(0.22f, 0.95f, twinkle);
            }
        }
    }

#if ENABLE_INPUT_SYSTEM
    private static bool WasStartPressed()
    {
        if (Keyboard.current != null && Keyboard.current.anyKey.wasPressedThisFrame)
        {
            return true;
        }

        if (Mouse.current != null &&
            (Mouse.current.leftButton.wasPressedThisFrame ||
             Mouse.current.rightButton.wasPressedThisFrame ||
             Mouse.current.middleButton.wasPressedThisFrame))
        {
            return true;
        }

        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
        {
            return true;
        }

        if (Gamepad.current != null &&
            (Gamepad.current.buttonSouth.wasPressedThisFrame ||
             Gamepad.current.startButton.wasPressedThisFrame))
        {
            return true;
        }

        return false;
    }
#else
    private static bool WasStartPressed()
    {
        return Input.anyKeyDown || Input.GetMouseButtonDown(0) || Input.touchCount > 0;
    }
#endif
}
