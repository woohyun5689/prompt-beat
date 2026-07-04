using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;
using Spine;
using Spine.Unity;

[DefaultExecutionOrder(-100)]
public sealed class RhythmGamePrototype : MonoBehaviour
{
    private enum JudgementKind
    {
        None,
        Good,
        Great,
        Perfect,
        Miss
    }

    private enum NoteKind
    {
        BadTap,
        GoodTap,
        BadWheelDown,
        GoodWheelUp
    }

    private enum RhythmDifficulty
    {
        Easy,
        Normal,
        Hard
    }

    private struct NoteSpec
    {
        public readonly float Time;
        public readonly NoteKind Kind;
        public readonly int LaneIndex;

        public NoteSpec(float time, NoteKind kind, int laneIndex)
        {
            Time = time;
            Kind = kind;
            LaneIndex = laneIndex;
        }
    }

    private sealed class Note
    {
        public NoteKind Kind;
        public double HitDspTime;
        public float LaneY;
        public GameObject Root;
        public bool Judged;
        public Transform[] GlitchLayers;
        public Transform[] GlitchBars;
        public float GlitchSeed;
    }

    private sealed class SongAnalysis
    {
        public float Bpm;
        public float BeatOffset;
        public float BeatDuration;
        public float EnvelopeRate;
        public float[] OnsetStrength;
        public float[] EnergyEnvelope;
        public readonly List<float> BeatTimes = new List<float>();
    }

    private sealed class LocalSongEntry
    {
        public string Name;
        public string FilePath;
        public AudioClip ResourceClip;
        public AudioClip LoadedClip;
    }

    private sealed class DifficultyPreset
    {
        public string Label;
        public float AnalysisStrongBase;
        public float AnalysisStrongOnset;
        public float AnalysisStrongEnergy;
        public float AnalysisWeakBase;
        public float AnalysisWeakOnset;
        public float AnalysisWeakEnergy;
        public float AnalysisHighOnsetThreshold;
        public float AnalysisHighOnsetMinChance;
        public float AnalysisExtraNoteChance;
        public float LongEnergyThreshold;
        public float LongNoteChance;
        public float FallbackStrongChance;
        public float FallbackWeakChance;
        public float FallbackLongChance;
        public float FallbackExtraNoteChance;
        public float TapGap;
        public float LongGap;
        public float LaneChangeChance;
        public float WideLaneJumpChance;
        public float ColorSwitchChance;
        public float GoodNoteChance;
        public float PeakSearchRadius;
        public float PeakTimeInfluence;
    }

    private sealed class ColorRecoveryTarget
    {
        public SpriteRenderer Renderer;
        public Color OriginalColor;
        public Color GrayscaleColor;
    }

    private sealed class ParallaxLayer
    {
        public Transform FirstTile;
        public Transform SecondTile;
        public float TileWidth;
        public float CenterY;
        public float Speed;
    }

    [Serializable]
    private sealed class MurekaGenerateRequest
    {
        public string prompt;
        public string provider;
    }

    [Serializable]
    private sealed class MurekaGenerateResponse
    {
        public string trackId;
        public string title;
        public string style;
        public float bpm;
        public string audioUrl;
        public string provider;
        public string warning;
    }

    [Serializable]
    private sealed class MurekaHealthResponse
    {
        public bool ok;
        public bool pythonReady;
    }

    private const float LaneY = -0.35f;
    private const float HitX = -3.75f;
    private const float SpawnX = 8.75f;
    private const float DespawnX = -8.5f;
    private const float NoteSpeed = 4.35f;
    private const float HitWindow = 0.34f;
    private const float WheelHitWindow = 0.40f;
    private const float TapMissInputWindow = 0.58f;
    private const float WheelMissInputWindow = 0.68f;
    private const float MissWindow = 0.42f;
    private const float AutoFollowLookAhead = 1.85f;
    private const float AutoFollowSpeed = 12f;
    private const float MinTapNoteGap = 0.38f;
    private const float MinLongNoteGap = 0.95f;
    private const float MusicLeadIn = 2.1f;
    private const float MinimumLoadingScreenDuration = 2.5f;
    private const float HitFxLifetime = 0.28f;
    private const float LongNoteScratchDuration = 0.22f;
    private const float LongNoteTailDistance = 2.74f;
    private const float BlueLongNoteVerticalOffset = 1.94f;
    private const float CharacterAfterimageDuration = 0.40f;
    private const float WheelGestureThreshold = 0.35f;
    private const float WheelGestureReleaseDelay = 0.065f;
    private const float CharacterAnimationMix = 0.045f;
    private const float BackgroundSourcePixelsPerUnit = 108f;
    private const float BackgroundSourceWidth = 3346f;
    private const int BeatsPerBar = 4;
    private const float AnalysisTargetRate = 100f;
    private const float MinimumAnalyzedBpm = 80f;
    private const float MaximumAnalyzedBpm = 180f;
    private const int SourceUiSheetWidth = 3560;
    private const int SourceUiSheetHeight = 2000;
    private const string MurekaBackendUrl = "http://127.0.0.1:8067";
    private const string MurekaProvider = "mureka_web_automation_imported";
    private const string MurekaBackendFolderName = "MurekaBackend";
    private const string MurekaBackendScriptName = "start_mureka_backend.ps1";
    private const float MurekaBackendStartupTimeout = 45f;
    private const string PromptControlName = "MurekaPromptField";
    private const string MusicResourcesPath = "Music";
    private const string PromptLaneMessage =
        "오늘 저녁 메뉴 추천해줘  ·  이 오류를 고쳐줘  ·  여행 계획을 짜줘  ·  이 글을 요약해줘  ·  자연스럽게 번역해줘  ·  아이디어를 브레인스토밍해줘  ·  이메일을 정중하게 다듬어줘  ·  공부 계획을 만들어줘  ·  ";
    private const string DefaultMurekaPrompt =
        "Bright energetic K-pop rhythm game song, clean strong beat, cute arcade mood, 128 bpm, catchy synth hook, short intro, no long silence";

    private static readonly bool ShowMurekaControls = false;
    private static readonly Color BadColor = new Color(1f, 0.12f, 0.12f, 1f);
    private static readonly Color BadDarkColor = new Color(0.55f, 0.02f, 0.04f, 1f);
    private static readonly Color GoodColor = new Color(0.08f, 0.42f, 1f, 1f);
    private static readonly Color GoodDarkColor = new Color(0.02f, 0.13f, 0.46f, 1f);
    private static readonly Color WhiteColor = new Color(0.97f, 0.99f, 1f, 1f);
    private static readonly Color LaneColor = new Color(0.85f, 0.97f, 1f, 0.92f);
    private static readonly float[] NoteYs = { -1.7f, LaneY, 0.45f };
    private static readonly DifficultyPreset EasyDifficulty = new DifficultyPreset
    {
        Label = "EASY",
        AnalysisStrongBase = 0.16f,
        AnalysisStrongOnset = 0.50f,
        AnalysisStrongEnergy = 0.08f,
        AnalysisWeakBase = 0.02f,
        AnalysisWeakOnset = 0.28f,
        AnalysisWeakEnergy = 0.04f,
        AnalysisHighOnsetThreshold = 0.86f,
        AnalysisHighOnsetMinChance = 0.78f,
        AnalysisExtraNoteChance = 0f,
        LongEnergyThreshold = 0.52f,
        LongNoteChance = 0.22f,
        FallbackStrongChance = 0.78f,
        FallbackWeakChance = 0.28f,
        FallbackLongChance = 0.18f,
        FallbackExtraNoteChance = 0.04f,
        TapGap = 0.52f,
        LongGap = 1.20f,
        LaneChangeChance = 0.55f,
        WideLaneJumpChance = 0.08f,
        ColorSwitchChance = 0.30f,
        GoodNoteChance = 0.58f,
        PeakSearchRadius = 0.17f,
        PeakTimeInfluence = 1f
    };
    private static readonly DifficultyPreset NormalDifficulty = new DifficultyPreset
    {
        Label = "NORMAL",
        AnalysisStrongBase = 0.26f,
        AnalysisStrongOnset = 0.62f,
        AnalysisStrongEnergy = 0.12f,
        AnalysisWeakBase = 0.08f,
        AnalysisWeakOnset = 0.68f,
        AnalysisWeakEnergy = 0.08f,
        AnalysisHighOnsetThreshold = 0.78f,
        AnalysisHighOnsetMinChance = 0.94f,
        AnalysisExtraNoteChance = 0f,
        LongEnergyThreshold = 0.38f,
        LongNoteChance = 0.58f,
        FallbackStrongChance = 0.95f,
        FallbackWeakChance = 0.76f,
        FallbackLongChance = 0.56f,
        FallbackExtraNoteChance = 0.18f,
        TapGap = MinTapNoteGap,
        LongGap = MinLongNoteGap,
        LaneChangeChance = 0.90f,
        WideLaneJumpChance = 0.35f,
        ColorSwitchChance = 0.50f,
        GoodNoteChance = 0.55f,
        PeakSearchRadius = 0.17f,
        PeakTimeInfluence = 1f
    };
    private static readonly DifficultyPreset HardDifficulty = new DifficultyPreset
    {
        Label = "HARD",
        AnalysisStrongBase = 0.44f,
        AnalysisStrongOnset = 0.78f,
        AnalysisStrongEnergy = 0.16f,
        AnalysisWeakBase = 0.24f,
        AnalysisWeakOnset = 0.86f,
        AnalysisWeakEnergy = 0.12f,
        AnalysisHighOnsetThreshold = 0.66f,
        AnalysisHighOnsetMinChance = 0.98f,
        AnalysisExtraNoteChance = 0.52f,
        LongEnergyThreshold = 0.54f,
        LongNoteChance = 0.24f,
        FallbackStrongChance = 0.98f,
        FallbackWeakChance = 0.96f,
        FallbackLongChance = 0.22f,
        FallbackExtraNoteChance = 0.58f,
        TapGap = 0.24f,
        LongGap = 0.78f,
        LaneChangeChance = 1f,
        WideLaneJumpChance = 0.70f,
        ColorSwitchChance = 0.82f,
        GoodNoteChance = 0.52f,
        PeakSearchRadius = 0.055f,
        PeakTimeInfluence = 0.35f
    };

    private static Sprite badTapSprite;
    private static Sprite goodTapSprite;
    private static Sprite badLongHeadSprite;
    private static Sprite goodLongHeadSprite;
    private static Sprite judgeRingSprite;
    private static Sprite hitLineSprite;
    private static Sprite whiteSprite;
    private static Sprite softCircleSprite;
    private static Sprite musicNoteSprite;
    private static Sprite musicNoteDoubleSprite;
    private static Sprite loadingGradientSprite;
    private static Texture2D whiteTexture;
    private static Texture2D uiSheetTexture;
    private static Sprite[] redTapSprites;
    private static Sprite[] blueTapSprites;
    private static Sprite redLongSprite;
    private static Sprite blueLongSprite;
    private static Sprite qGuideSprite;
    private static Sprite eGuideSprite;
    private static Sprite mouseDownGuideSprite;
    private static Sprite mouseUpGuideSprite;
    private static Shader characterAfterimageShader;
    private static Material backgroundColorRecoveryMaterial;

    private readonly List<Note> notes = new List<Note>();
    private readonly List<NoteSpec> chart = new List<NoteSpec>();
    private readonly List<ColorRecoveryTarget> colorRecoveryTargets = new List<ColorRecoveryTarget>();
    private readonly List<ParallaxLayer> parallaxLayers = new List<ParallaxLayer>();
    private readonly Dictionary<string, SongAnalysis> songAnalysisCache = new Dictionary<string, SongAnalysis>();
    private readonly HashSet<NoteKind> tutorialKindsShown = new HashSet<NoteKind>();

    private Transform notesRoot;
    private Transform judgeRing;
    private SpriteRenderer judgeRingRenderer;
    private SpriteRenderer hitLineRenderer;
    private readonly Transform[] promptLaneSegments = new Transform[2];
    private readonly TextMesh[] promptLaneTexts = new TextMesh[2];
    private readonly int[] promptLaneHiddenCharacters = { -1, -1 };
    private float promptLaneSegmentWidth = 24f;
    private float promptLaneCharacterWidth = 0.22f;
    private Camera gameCamera;
    private Color cameraOriginalBackgroundColor;
    private Vector3 cameraBasePosition;
    private float cameraShakeUntil;
    private float wheelInputAccumulator;
    private float lastWheelSignalTime = -999f;
    private bool wheelGestureConsumed;
    private AudioSource musicSource;
    private AudioSource hitSoundSource;
    private AudioSource longScratchSoundSource;
    private AudioClip hitSoundClip;
    private AudioClip longScratchSoundClip;
    private AudioClip currentSongClip;
    private AudioClip generatedSongClip;
    private SongAnalysis currentSongAnalysis;
    private readonly List<LocalSongEntry> localSongs = new List<LocalSongEntry>();
    private Vector2 localSongScroll;
    private double songStartDspTime;
    private double audioVisualLatency;
    private float chartDuration;
    private float generatedBpm = 128f;
    private float generatedSongLength = 14f;
    private int generatedSongSeed;
    private string generatedSongLabel = "MUREKA SONG";
    private string generatedSongProvider = "MUREKA";
    private string generatedSongWarning = "";
    private string murekaPrompt = DefaultMurekaPrompt;
    private string murekaStatus = "MUREKA website backend idle. Press START BACKEND or GENERATE.";
    private int selectedLocalSongIndex = -1;
    private bool isRequestingMurekaSong;
    private bool isStartingMurekaBackend;
    private bool isLoadingLocalSong;
    private bool isEditingPrompt;
    private RhythmDifficulty selectedDifficulty = RhythmDifficulty.Normal;
    private bool songSelectionVisible = true;
    private bool worldHiddenForSongSelect;
    private float songCarouselOffset;
    private float songCarouselVelocity;
    private bool chartFinished;
    private float lastMurekaBackendStartAttempt = -999f;
    private int score;
    private int combo;
    private int bestCombo;
    private int successfulHitCount;
    private float displayedHeartFill;
    private float displayedColorRecovery;
    private JudgementKind judgementKind = JudgementKind.None;
    private float judgementVisibleUntil;
    private Texture2D goodJudgementTexture;
    private Texture2D greatJudgementTexture;
    private Texture2D perfectJudgementTexture;
    private Texture2D missJudgementTexture;
    private GUIStyle titleStyle;
    private GUIStyle numberStyle;
    private GUIStyle comboNumberStyle;
    private GUIStyle comboLabelStyle;
    private GUIStyle smallStyle;
    private GUIStyle guideStyle;
    private float guiStyleScale = -1f;
    private Font gameFont;
    private SkeletonAnimation characterAnimation;
    private GameObject loadingScreenRoot;
    private SkeletonAnimation loadingScreenCharacter;
    private string loadingScreenAnimationName;
    private bool isLoadingScreenVisible;
    private float loadingScreenShownAt;
    private Coroutine loadingScreenFadeRoutine;
    private string characterRunAnimation;
    private string currentCharacterSkin;
    private string lastBlueReactionAnimation;
    private string lastRedReactionAnimation;
    private SkeletonDataAsset blueHitFxData;
    private SkeletonDataAsset redHitFxData;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void InstallOnPlay()
    {
        if (FindFirstObjectByType<RhythmGamePrototype>() != null)
        {
            return;
        }

        var gameObject = new GameObject("Rhythm Game Prototype");
        gameObject.AddComponent<RhythmGamePrototype>();
    }

    private void Awake()
    {
        EnsureSharedAssets();
        LoadUiSheetAssets();
        LoadJudgementTextures();
        SetupCamera();
        SetupStage();
        SetupCharacter();
        LoadHitFxAssets();
        SetupAudio();
        LoadLocalSongs();
    }

    private void OnEnable()
    {
        if (Application.isPlaying)
        {
            RebindAndAlignParallaxLayers();
        }
    }

    private void Update()
    {
        ReadInput();
        UpdateSongSelectWorldVisibility();
        UpdateSongCarousel();
        UpdateNotes();
        UpdateJudgeRing();
        UpdateCameraShake();
        UpdateParallaxBackground();
        UpdateHitLineBlink();
        UpdatePromptLane();
        UpdateHeartAndWorldColor();
    }

    private void OnDestroy()
    {
        if (generatedSongClip != null)
        {
            Destroy(generatedSongClip);
            generatedSongClip = null;
        }

        if (hitSoundClip != null)
        {
            Destroy(hitSoundClip);
            hitSoundClip = null;
        }

        if (longScratchSoundClip != null)
        {
            Destroy(longScratchSoundClip);
            longScratchSoundClip = null;
        }

        for (int i = 0; i < localSongs.Count; i++)
        {
            if (localSongs[i].LoadedClip != null)
            {
                Destroy(localSongs[i].LoadedClip);
                localSongs[i].LoadedClip = null;
            }
        }
    }

    private void OnGUI()
    {
        EnsureGuiStyles();

        float scale = Mathf.Clamp(Screen.height / 720f, 0.72f, 1.35f);
        if (isLoadingScreenVisible)
        {
            // The loading composition (character, crescent, "생각 중..." text) is part
            // of the Spine Loading animation, so no IMGUI is drawn here.
            return;
        }

        if (songSelectionVisible)
        {
            DrawSongSelectScene(scale);
            return;
        }

        Rect comboNumberRect = new Rect((Screen.width - 300f * scale) * 0.5f, 30f * scale, 300f * scale, 76f * scale);
        Rect comboLabelRect = new Rect(comboNumberRect.x, 88f * scale, comboNumberRect.width, 32f * scale);
        DrawComboNumber(comboNumberRect, combo, comboNumberStyle, new Color(0.12f, 0.04f, 0.16f, 1f), 4f * scale);
        DrawContinuousGradientOutlinedLabel(
            comboLabelRect,
            "COMBO",
            comboLabelStyle,
            new Color(0.12f, 0.04f, 0.16f, 1f),
            3f * scale,
            new Color(1f, 0.24f, 0.72f, 1f),
            new Color(0.08f, 0.94f, 1f, 1f));

        if (Time.time <= judgementVisibleUntil)
        {
            DrawJudgementImage(scale);
        }

        if (ShowMurekaControls)
        {
            DrawMurekaControls(scale);
        }

        DrawGameplayHud(scale);
    }

    private void DrawSongSelectScene(float scale)
    {
        EnsureSelectedSongIndex();

        float referenceWidth = 1672f;
        float referenceHeight = 941f;
        float fit = Mathf.Min(Screen.width / referenceWidth, Screen.height / referenceHeight);
        float offsetX = (Screen.width - referenceWidth * fit) * 0.5f;
        float offsetY = (Screen.height - referenceHeight * fit) * 0.5f;
        float uiScale = Mathf.Clamp(fit, 0.62f, 1.28f);

        Rect R(float x, float y, float width, float height)
        {
            return new Rect(offsetX + x * fit, offsetY + y * fit, width * fit, height * fit);
        }

        DrawSongSelectBackdrop(R(0f, 0f, referenceWidth, referenceHeight));

        GUIStyle titleTextStyle = CreateSongSelectStyle(52f, uiScale, TextAnchor.MiddleLeft, FontStyle.Bold, new Color(1f, 0.93f, 0.16f, 1f));
        GUIStyle infoTitleStyle = CreateSongSelectStyle(32f, uiScale, TextAnchor.MiddleLeft, FontStyle.Bold, WhiteColor);
        GUIStyle promptTitleStyle = CreateSongSelectStyle(22f, uiScale, TextAnchor.MiddleLeft, FontStyle.Bold, new Color(0f, 1f, 0.98f, 1f));
        GUIStyle promptTextStyle = CreateSongSelectStyle(17f, uiScale, TextAnchor.UpperLeft, FontStyle.Bold, new Color(0.88f, 0.95f, 1f, 0.94f));
        promptTextStyle.wordWrap = true;
        GUIStyle statusStyle = CreateSongSelectStyle(17f, uiScale, TextAnchor.MiddleRight, FontStyle.Bold, new Color(0.84f, 0.95f, 1f, 0.86f));

        Rect titleRect = R(14f, 18f, 555f, 100f);
        DrawNeonPanel(titleRect, new Color(0.02f, 0.07f, 0.34f, 0.96f), new Color(0.12f, 0.92f, 1f, 0.82f), fit);
        DrawOutlinedLabel(new Rect(titleRect.x + 34f * fit, titleRect.y + 5f * fit, titleRect.width - 70f * fit, titleRect.height - 10f * fit), "SONG SELECT", titleTextStyle, new Color(0.04f, 0.1f, 0.5f, 1f), 3f * uiScale);
        DrawFloatingNote(R(468f, 45f, 25f, 46f), new Color(1f, 0.27f, 0.88f, 1f));
        DrawFloatingNote(R(520f, 14f, 26f, 48f), new Color(0.98f, 0.34f, 1f, 1f));

        bool hasSongs = localSongs.Count > 0;
        string selectedSongName = GetLocalSongName(selectedLocalSongIndex);

        DrawSongCarousel(offsetX, offsetY, fit, hasSongs, uiScale);

        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && hasSongs && localSongs.Count > 1 && !isLoadingLocalSong;
        if (DrawCircleButton(R(54f, 392f, 106f, 106f), "<", uiScale))
        {
            SelectSongOffset(-1);
        }

        if (DrawCircleButton(R(1518f, 394f, 96f, 104f), ">", uiScale))
        {
            SelectSongOffset(1);
        }

        GUI.enabled = previousEnabled;

        Rect infoRect = R(352f, 598f, 982f, 206f);
        DrawNeonPanel(infoRect, new Color(0.015f, 0.035f, 0.14f, 0.96f), new Color(0.72f, 0.9f, 1f, 0.9f), fit);
        DrawCircleIcon(R(398f, 642f, 112f, 112f), new Color(0.68f, 0.45f, 1f, 1f), fit);
        GUI.Label(R(538f, 618f, 380f, 48f), hasSongs ? selectedSongName : "노래 없음", infoTitleStyle);
        DrawRect(R(538f, 680f, 730f, 3f), new Color(0.48f, 0.24f, 1f, 0.56f));
        GUI.Label(R(538f, 690f, 300f, 32f), "노래 프롬프트", promptTitleStyle);
        GUI.Label(
            R(540f, 732f, 565f, 58f),
            hasSongs
                ? "로컬 음악 파일에서 BPM과 노트를 자동 분석합니다.\n선택한 난이도로 바로 게임을 시작합니다."
                : "Assets/Resources/Music 폴더에 MP3 파일을 넣으면 이 화면에 표시됩니다.",
            promptTextStyle);
        DrawEqualizer(R(992f, 636f, 270f, 42f), fit);
        DrawNeonPanel(R(1115f, 730f, 158f, 46f), new Color(0.06f, 0.04f, 0.20f, 0.92f), new Color(0.56f, 0.35f, 1f, 0.95f), fit);
        GUI.Label(R(1128f, 731f, 130f, 44f), generatedBpm.ToString("0") + " BPM", CreateSongSelectStyle(24f, uiScale, TextAnchor.MiddleCenter, FontStyle.Bold, WhiteColor));

        // Bottom row: generate on the far left, difficulty centered, PLAY on the right.
        DrawSongSelectDifficultyButton(R(504f, 812f, 212f, 112f), RhythmDifficulty.Easy, new Color(0.02f, 0.42f, 1f, 1f), uiScale);
        DrawSongSelectDifficultyButton(R(730f, 812f, 212f, 112f), RhythmDifficulty.Normal, new Color(0.08f, 0.74f, 0.25f, 1f), uiScale);
        DrawSongSelectDifficultyButton(R(956f, 812f, 212f, 112f), RhythmDifficulty.Hard, new Color(1f, 0.12f, 0.35f, 1f), uiScale);

        GUI.enabled = previousEnabled && !isRequestingMurekaSong && !isStartingMurekaBackend && !isLoadingLocalSong;
        if (DrawArcadeButton(R(24f, 812f, 292f, 112f), "새 노래 생성", new Color(0.46f, 0.08f, 1f, 1f), new Color(0.92f, 0.20f, 1f, 1f), uiScale))
        {
            GenerateNewSong();
        }

        GUI.enabled = previousEnabled && hasSongs && !isLoadingLocalSong;
        if (DrawArcadeButton(R(1340f, 812f, 316f, 112f), "PLAY", new Color(0.10f, 0.74f, 0.20f, 1f), new Color(0.58f, 1f, 0.38f, 1f), uiScale))
        {
            PlayLocalSong(selectedLocalSongIndex);
        }

        GUI.enabled = previousEnabled;

        string status = isLoadingLocalSong
            ? "노래를 준비하는 중..."
            : murekaStatus;
        GUI.Label(R(916f, 42f, 600f, 36f), status, statusStyle);
    }

    private static GUIStyle CreateSongSelectStyle(float size, float scale, TextAnchor anchor, FontStyle fontStyle, Color color)
    {
        GUIStyle style = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(size * scale),
            fontStyle = fontStyle,
            alignment = anchor,
            clipping = TextClipping.Clip
        };
        style.normal.textColor = color;
        return style;
    }

    private static void DrawSongSelectBackdrop(Rect rect)
    {
        DrawRect(rect, new Color(0.02f, 0.18f, 0.58f, 0.18f));
        DrawRect(new Rect(rect.x, rect.y + rect.height * 0.62f, rect.width, rect.height * 0.38f), new Color(0.37f, 0.07f, 0.78f, 0.16f));
        DrawRect(new Rect(rect.x, rect.y + rect.height * 0.76f, rect.width, rect.height * 0.03f), new Color(0.10f, 0.95f, 1f, 0.18f));
    }

    private static void DrawNeonPanel(Rect rect, Color fill, Color border, float scale, float alpha = 1f)
    {
        DrawRect(rect, new Color(border.r, border.g, border.b, 0.24f * alpha));
        DrawRect(new Rect(rect.x + 4f * scale, rect.y + 4f * scale, rect.width - 8f * scale, rect.height - 8f * scale), new Color(border.r, border.g, border.b, border.a * alpha));
        DrawRect(new Rect(rect.x + 9f * scale, rect.y + 9f * scale, rect.width - 18f * scale, rect.height - 18f * scale), new Color(fill.r, fill.g, fill.b, fill.a * alpha));
        DrawRect(new Rect(rect.x + 14f * scale, rect.y + 14f * scale, rect.width - 28f * scale, 3f * scale), new Color(1f, 1f, 1f, 0.25f * alpha));
    }

    // Reference-space layout anchors for carousel slots -2..+2.
    private static readonly float[] CarouselAnchorX = { -220f, 196f, 570f, 1216f, 1712f };
    private static readonly float[] CarouselAnchorY = { 396f, 310f, 116f, 310f, 396f };
    private static readonly float[] CarouselAnchorW = { 180f, 260f, 532f, 260f, 180f };
    private static readonly float[] CarouselAnchorH = { 152f, 224f, 468f, 224f, 152f };

    private static readonly Color[] SongCardAccents =
    {
        new Color(0.46f, 0.12f, 0.96f, 1f),
        new Color(0f, 0.75f, 0.82f, 1f),
        new Color(0.05f, 0.30f, 0.95f, 1f),
        new Color(0.95f, 0.20f, 0.75f, 1f),
        new Color(0.10f, 0.80f, 0.45f, 1f)
    };

    private void UpdateSongSelectWorldVisibility()
    {
        // On the song-select screen only the parallax backdrop should remain:
        // the runner, prompt lane, hit line, ring and notes are gameplay-only.
        if (worldHiddenForSongSelect == songSelectionVisible)
        {
            return;
        }

        worldHiddenForSongSelect = songSelectionVisible;
        bool show = !songSelectionVisible;
        if (characterAnimation != null)
        {
            characterAnimation.gameObject.SetActive(show);
        }

        if (judgeRing != null)
        {
            judgeRing.gameObject.SetActive(show);
        }

        if (hitLineRenderer != null)
        {
            hitLineRenderer.gameObject.SetActive(show);
        }

        if (notesRoot != null)
        {
            notesRoot.gameObject.SetActive(show);
        }

        for (int i = 0; i < promptLaneSegments.Length; i++)
        {
            if (promptLaneSegments[i] != null)
            {
                promptLaneSegments[i].gameObject.SetActive(show);
            }
        }
    }

    private void UpdateSongCarousel()
    {
        if (songCarouselOffset == 0f && songCarouselVelocity == 0f)
        {
            return;
        }

        // Underdamped spring toward zero: the cards glide over and land with
        // a tiny bounce instead of snapping into place.
        const float stiffness = 130f;
        const float damping = 17f;
        float deltaTime = Mathf.Min(Time.unscaledDeltaTime, 0.033f);
        songCarouselVelocity += (-songCarouselOffset * stiffness - songCarouselVelocity * damping) * deltaTime;
        songCarouselOffset += songCarouselVelocity * deltaTime;

        if (Mathf.Abs(songCarouselOffset) < 0.0015f && Mathf.Abs(songCarouselVelocity) < 0.02f)
        {
            songCarouselOffset = 0f;
            songCarouselVelocity = 0f;
        }
    }

    private void DrawSongCarousel(float offsetX, float offsetY, float fit, bool hasSongs, float uiScale)
    {
        float animOffset = songCarouselOffset;
        List<int> slots = new List<int> { -2, -1, 0, 1, 2 };
        // Cards closer to the center draw later so they sit on top mid-slide.
        slots.Sort((a, b) => Mathf.Abs(b - animOffset).CompareTo(Mathf.Abs(a - animOffset)));

        for (int i = 0; i < slots.Count; i++)
        {
            int slot = slots[i];
            float t = slot - animOffset;
            float distance = Mathf.Abs(t);
            if (distance > 2.35f)
            {
                continue;
            }

            float clamped = Mathf.Clamp(t, -2f, 2f);
            int lower = Mathf.Clamp(Mathf.FloorToInt(clamped), -2, 1);
            float frac = clamped - lower;
            int anchorA = lower + 2;
            int anchorB = anchorA + 1;
            float x = Mathf.Lerp(CarouselAnchorX[anchorA], CarouselAnchorX[anchorB], frac);
            float y = Mathf.Lerp(CarouselAnchorY[anchorA], CarouselAnchorY[anchorB], frac);
            float width = Mathf.Lerp(CarouselAnchorW[anchorA], CarouselAnchorW[anchorB], frac);
            float height = Mathf.Lerp(CarouselAnchorH[anchorA], CarouselAnchorH[anchorB], frac);

            int songIndex = hasSongs ? WrapSongIndex(selectedLocalSongIndex + slot) : -1;
            float selection = Mathf.Clamp01(1f - distance);
            float alpha = Mathf.Clamp01(2f - distance);

            // Gentle idle bobbing, calmer for the focused card.
            y += Mathf.Sin(Time.unscaledTime * 2.1f + (songIndex < 0 ? slot : songIndex) * 1.7f)
                * Mathf.Lerp(7f, 3.5f, selection);

            string songName = hasSongs ? GetLocalSongName(songIndex) : (slot == 0 ? "NO LOCAL SONG" : "NO SONG");
            Color accent = SongCardAccents[songIndex < 0 ? 0 : songIndex % SongCardAccents.Length];
            Rect rect = new Rect(offsetX + x * fit, offsetY + y * fit, width * fit, height * fit);
            DrawSongCard(rect, songName, accent, selection, alpha, uiScale);
        }
    }

    private void DrawSongCard(Rect rect, string songName, Color accent, float selection, float alpha, float scale)
    {
        if (alpha <= 0.01f)
        {
            return;
        }

        Color selectedFill = new Color(accent.r * 0.45f, accent.g * 0.25f, accent.b * 0.72f, 0.96f);
        Color unselectedFill = new Color(accent.r * 0.55f, accent.g * 0.45f, accent.b * 0.75f, 0.72f);
        Color fill = Color.Lerp(unselectedFill, selectedFill, selection);
        DrawNeonPanel(rect, fill, new Color(0.78f, 0.95f, 1f, Mathf.Lerp(0.68f, 0.96f, selection)), scale, alpha);

        Rect iconRect = new Rect(rect.center.x - rect.width * 0.16f, rect.y + rect.height * 0.15f, rect.width * 0.32f, rect.height * 0.32f);
        DrawCircleIcon(iconRect, new Color(0.95f, 0.74f, 1f, Mathf.Lerp(0.72f, 1f, selection)), scale, alpha);

        // Fixed font size + GUI.matrix scaling: animating the font size itself
        // regenerates dynamic-font glyphs every frame, and the atlas rebuild makes
        // every label on screen flicker with garbled overlaps mid-slide.
        GUIStyle style = CreateSongSelectStyle(44f, scale, TextAnchor.MiddleCenter, FontStyle.Bold, WhiteColor);
        Rect selectedLabelRect = new Rect(rect.x + 42f * scale, rect.yMax - 130f * scale, rect.width - 84f * scale, 76f * scale);
        Rect unselectedLabelRect = new Rect(rect.x + 20f * scale, rect.yMax - 74f * scale, rect.width - 40f * scale, 46f * scale);
        Rect labelRect = LerpRect(unselectedLabelRect, selectedLabelRect, selection);
        float textScale = Mathf.Lerp(21f / 44f, 1f, selection);
        Rect textRect = new Rect(
            labelRect.center.x - labelRect.width / textScale * 0.5f,
            labelRect.center.y - labelRect.height / textScale * 0.5f,
            labelRect.width / textScale,
            labelRect.height / textScale);
        Color previousGuiColor = GUI.color;
        GUI.color = new Color(previousGuiColor.r, previousGuiColor.g, previousGuiColor.b, previousGuiColor.a * alpha);
        Matrix4x4 previousMatrix = GUI.matrix;
        GUIUtility.ScaleAroundPivot(new Vector2(textScale, textScale), labelRect.center);
        DrawOutlinedLabel(textRect, songName, style, new Color(0.16f, 0.04f, 0.34f, 1f), 2.4f * scale);
        GUI.matrix = previousMatrix;
        GUI.color = previousGuiColor;
    }

    private static Rect LerpRect(Rect a, Rect b, float t)
    {
        return new Rect(
            Mathf.Lerp(a.x, b.x, t),
            Mathf.Lerp(a.y, b.y, t),
            Mathf.Lerp(a.width, b.width, t),
            Mathf.Lerp(a.height, b.height, t));
    }

    private static void DrawFloatingNote(Rect rect, Color color)
    {
        DrawRect(new Rect(rect.x + rect.width * 0.58f, rect.y, rect.width * 0.22f, rect.height * 0.64f), color);
        DrawRect(new Rect(rect.x + rect.width * 0.58f, rect.y, rect.width * 0.56f, rect.height * 0.14f), color);
        DrawRect(new Rect(rect.x + rect.width * 0.06f, rect.y + rect.height * 0.58f, rect.width * 0.55f, rect.height * 0.28f), color);
    }

    private static void DrawCircleIcon(Rect rect, Color color, float scale, float alpha = 1f)
    {
        DrawRect(rect, new Color(color.r, color.g, color.b, 0.42f * alpha));
        Rect inner = new Rect(rect.x + 9f * scale, rect.y + 9f * scale, rect.width - 18f * scale, rect.height - 18f * scale);
        DrawRect(inner, new Color(color.r * 0.55f, color.g * 0.55f, color.b * 0.9f, 0.92f * alpha));
        Color previousGuiColor = GUI.color;
        GUI.color = new Color(previousGuiColor.r, previousGuiColor.g, previousGuiColor.b, previousGuiColor.a * alpha);
        GUI.Label(inner, "♪", CreateSongSelectStyle(42f, scale, TextAnchor.MiddleCenter, FontStyle.Bold, WhiteColor));
        GUI.color = previousGuiColor;
    }

    private static void DrawEqualizer(Rect rect, float scale)
    {
        int bars = 16;
        float gap = 5f * scale;
        float barWidth = (rect.width - gap * (bars - 1)) / bars;
        for (int i = 0; i < bars; i++)
        {
            float normalized = 0.25f + Mathf.Abs(Mathf.Sin(i * 1.33f + Time.unscaledTime * 3.1f)) * 0.75f;
            float height = rect.height * normalized;
            Rect bar = new Rect(rect.x + i * (barWidth + gap), rect.yMax - height, barWidth, height);
            Color color = Color.Lerp(new Color(1f, 0.18f, 0.85f, 0.95f), new Color(0f, 0.95f, 1f, 0.95f), i / (float)(bars - 1));
            DrawRect(bar, color);
        }
    }

    private bool DrawCircleButton(Rect rect, string text, float scale)
    {
        DrawRect(rect, new Color(0.68f, 0.85f, 1f, 0.42f));
        Rect inner = new Rect(rect.x + 10f * scale, rect.y + 10f * scale, rect.width - 20f * scale, rect.height - 20f * scale);
        DrawRect(inner, new Color(0.02f, 0.03f, 0.24f, GUI.enabled ? 0.92f : 0.42f));
        GUIStyle style = CreateSongSelectStyle(54f, scale, TextAnchor.MiddleCenter, FontStyle.Bold, WhiteColor);
        GUI.Label(inner, text, style);
        return GUI.Button(rect, GUIContent.none, GUIStyle.none);
    }

    private bool DrawArcadeButton(Rect rect, string label, Color fill, Color highlight, float scale)
    {
        DrawNeonPanel(rect, new Color(fill.r, fill.g, fill.b, GUI.enabled ? 0.95f : 0.38f), new Color(highlight.r, highlight.g, highlight.b, GUI.enabled ? 0.92f : 0.35f), scale);
        Rect shine = new Rect(rect.x + 14f * scale, rect.y + 12f * scale, rect.width - 28f * scale, rect.height * 0.22f);
        DrawRect(shine, new Color(1f, 1f, 1f, GUI.enabled ? 0.22f : 0.08f));
        GUIStyle style = CreateSongSelectStyle(label == "PLAY" ? 42f : 27f, scale, TextAnchor.MiddleCenter, FontStyle.Bold, WhiteColor);
        DrawOutlinedLabel(rect, label, style, new Color(0f, 0f, 0.12f, 0.82f), 2f * scale);
        return GUI.Button(rect, GUIContent.none, GUIStyle.none);
    }

    private void DrawSongSelectDifficultyButton(Rect rect, RhythmDifficulty difficulty, Color fill, float scale)
    {
        bool selected = selectedDifficulty == difficulty;
        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && !isLoadingLocalSong && !isRequestingMurekaSong;
        Color border = selected ? new Color(1f, 1f, 1f, 0.96f) : new Color(0.72f, 0.95f, 1f, 0.72f);
        DrawNeonPanel(rect, new Color(fill.r, fill.g, fill.b, selected ? 0.95f : 0.70f), border, scale);
        GUIStyle labelStyle = CreateSongSelectStyle(30f, scale, TextAnchor.MiddleCenter, FontStyle.Bold, WhiteColor);
        GUI.Label(new Rect(rect.x, rect.y + 12f * scale, rect.width, 44f * scale), GetDifficultyPreset(difficulty).Label, labelStyle);
        GUI.Label(new Rect(rect.x, rect.y + 54f * scale, rect.width, 36f * scale), GetDifficultyStars(difficulty), CreateSongSelectStyle(23f, scale, TextAnchor.MiddleCenter, FontStyle.Bold, WhiteColor));
        if (GUI.Button(rect, GUIContent.none, GUIStyle.none))
        {
            SetDifficulty(difficulty);
        }

        GUI.enabled = previousEnabled;
    }

    private static string GetDifficultyStars(RhythmDifficulty difficulty)
    {
        switch (difficulty)
        {
            case RhythmDifficulty.Easy:
                return "☆";
            case RhythmDifficulty.Hard:
                return "☆☆☆";
            default:
                return "☆☆";
        }
    }

    private void EnsureSelectedSongIndex()
    {
        if (localSongs.Count == 0)
        {
            selectedLocalSongIndex = -1;
            return;
        }

        if (selectedLocalSongIndex < 0)
        {
            selectedLocalSongIndex = 0;
        }
        else if (selectedLocalSongIndex >= localSongs.Count)
        {
            selectedLocalSongIndex = localSongs.Count - 1;
        }
    }

    private int WrapSongIndex(int index)
    {
        if (localSongs.Count == 0)
        {
            return -1;
        }

        int wrapped = index % localSongs.Count;
        return wrapped < 0 ? wrapped + localSongs.Count : wrapped;
    }

    private void SelectSongOffset(int offset)
    {
        if (localSongs.Count == 0)
        {
            selectedLocalSongIndex = -1;
            return;
        }

        selectedLocalSongIndex = WrapSongIndex(selectedLocalSongIndex + offset);
        // Keep the cards visually in place, then let the spring ease them into
        // their new slots so browsing feels like sliding a shelf of albums.
        songCarouselOffset = Mathf.Clamp(songCarouselOffset + offset, -2.2f, 2.2f);
        generatedSongLabel = GetLocalSongName(selectedLocalSongIndex);
        generatedSongProvider = "LOCAL";
        generatedSongWarning = string.Empty;
        murekaStatus = "Selected " + generatedSongLabel + ".";
    }

    private string GetLocalSongName(int index)
    {
        if (index < 0 || index >= localSongs.Count || localSongs[index] == null || string.IsNullOrWhiteSpace(localSongs[index].Name))
        {
            return "NO SONG";
        }

        return localSongs[index].Name;
    }

    private void DrawGameplayHud(float scale)
    {
        float heartSize = 104f * scale;
        Rect heartRect = new Rect(18f * scale, Screen.height - heartSize - 12f * scale, heartSize, heartSize);
        DrawUiSheetRegion(heartRect, 851, 606, 552, 527);

        if (displayedHeartFill > 0.001f)
        {
            float filledHeartWidth = heartRect.width * (400f / 552f);
            float filledHeartHeight = heartRect.height * (386f / 527f);
            Rect filledHeartRect = new Rect(
                heartRect.center.x - filledHeartWidth * 0.5f,
                heartRect.center.y - filledHeartHeight * 0.5f,
                filledHeartWidth,
                filledHeartHeight);
            DrawUiSheetRegionBottomFill(filledHeartRect, 1465, 685, 400, 386, displayedHeartFill);
        }

        float progressWidth = Mathf.Min(520f * scale, Screen.width * 0.42f);
        float progressHeight = 13f * scale;
        float progressX = (Screen.width - progressWidth) * 0.5f;
        float progressY = Screen.height - 27f * scale;
        float progress = 0f;
        if (currentSongClip != null && currentSongClip.length > 0.01f)
        {
            progress = Mathf.Clamp01((float)(AudioSettings.dspTime - songStartDspTime) / currentSongClip.length);
        }

        DrawRect(new Rect(progressX - 3f * scale, progressY - 3f * scale, progressWidth + 6f * scale, progressHeight + 6f * scale), new Color(0.16f, 0.035f, 0.28f, 0.92f));
        DrawRect(new Rect(progressX, progressY, progressWidth, progressHeight), new Color(0.34f, 0.14f, 0.58f, 0.9f));
        DrawRect(new Rect(progressX, progressY, progressWidth * progress, progressHeight), new Color(1f, 0.25f, 0.82f, 1f));

        smallStyle.normal.textColor = new Color(1f, 1f, 1f, 0.82f);
        GUI.Label(new Rect(Screen.width - 390f * scale, 16f * scale, 370f * scale, 26f * scale), generatedSongLabel + "  " + generatedBpm.ToString("0.0") + " BPM", smallStyle);
        GUI.Label(new Rect(126f * scale, Screen.height - 56f * scale, 180f * scale, 26f * scale), "BEST " + bestCombo + "x", smallStyle);
    }

    private void DrawMurekaControls(float scale)
    {
        float panelWidth = Mathf.Min(520f * scale, Screen.width - 36f * scale);
        float panelHeight = 124f * scale;
        float panelX = Mathf.Max(18f * scale, Screen.width - panelWidth - 18f * scale);
        float panelY = 104f * scale;
        Rect panelRect = new Rect(panelX, panelY, panelWidth, panelHeight);

        DrawPanel(panelRect, new Color(0.02f, 0.09f, 0.18f, 0.88f));

        GUIStyle promptTitleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(14f * scale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        promptTitleStyle.normal.textColor = new Color(0.8f, 0.94f, 1f, 1f);

        GUIStyle promptTextStyle = new GUIStyle(GUI.skin.textArea)
        {
            fontSize = Mathf.RoundToInt(13f * scale),
            wordWrap = true
        };

        GUI.Label(new Rect(panelX + 14f * scale, panelY + 8f * scale, 220f * scale, 22f * scale), "MUREKA PROMPT", promptTitleStyle);

        GUI.SetNextControlName(PromptControlName);
        murekaPrompt = GUI.TextArea(
            new Rect(panelX + 14f * scale, panelY + 34f * scale, panelWidth - 28f * scale, 48f * scale),
            murekaPrompt,
            240,
            promptTextStyle);
        isEditingPrompt = GUI.GetNameOfFocusedControl() == PromptControlName;

        bool previousEnabled = GUI.enabled;
        GUI.enabled = !isRequestingMurekaSong && !isStartingMurekaBackend;
        if (GUI.Button(new Rect(panelX + 14f * scale, panelY + 88f * scale, 154f * scale, 26f * scale), "START BACKEND"))
        {
            GUI.FocusControl(string.Empty);
            isEditingPrompt = false;
            StartMurekaBackendOnly();
        }

        if (GUI.Button(new Rect(panelX + 178f * scale, panelY + 88f * scale, 154f * scale, 26f * scale), "GENERATE"))
        {
            GUI.FocusControl(string.Empty);
            isEditingPrompt = false;
            GenerateNewSong();
        }

        GUI.enabled = previousEnabled;
    }

    private void DrawLocalSongSelector(float scale)
    {
        float panelX = 18f * scale;
        float panelY = 108f * scale;

        if (!songSelectionVisible)
        {
            if (GUI.Button(new Rect(panelX, panelY, 118f * scale, 30f * scale), "SONGS"))
            {
                songSelectionVisible = true;
            }

            DrawDifficultyButtons(panelX + 128f * scale, panelY, 250f * scale, 30f * scale, scale);

            return;
        }

        float panelWidth = Mathf.Min(390f * scale, Screen.width - 36f * scale);
        float panelHeight = Mathf.Clamp(Screen.height - panelY - 124f * scale, 180f * scale, 416f * scale);
        Rect panelRect = new Rect(panelX, panelY, panelWidth, panelHeight);
        DrawPanel(panelRect, new Color(0.02f, 0.09f, 0.18f, 0.88f));

        GUIStyle selectorTitleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(15f * scale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        selectorTitleStyle.normal.textColor = new Color(0.8f, 0.94f, 1f, 1f);

        GUIStyle songLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(13f * scale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip
        };
        songLabelStyle.normal.textColor = WhiteColor;

        GUI.Label(new Rect(panelX + 14f * scale, panelY + 8f * scale, 190f * scale, 24f * scale), "LOCAL SONGS", selectorTitleStyle);
        if (GUI.Button(new Rect(panelX + panelWidth - 76f * scale, panelY + 10f * scale, 62f * scale, 24f * scale), "HIDE"))
        {
            songSelectionVisible = false;
        }

        DrawDifficultyButtons(panelX + 14f * scale, panelY + 40f * scale, panelWidth - 28f * scale, 28f * scale, scale);

        if (localSongs.Count == 0)
        {
            GUI.Label(
                new Rect(panelX + 14f * scale, panelY + 78f * scale, panelWidth - 28f * scale, 64f * scale),
                "No songs found in Assets/Resources/Music.",
                songLabelStyle);
            return;
        }

        Rect viewRect = new Rect(panelX + 14f * scale, panelY + 78f * scale, panelWidth - 28f * scale, panelHeight - 92f * scale);
        float rowHeight = 34f * scale;
        Rect contentRect = new Rect(0f, 0f, viewRect.width - 18f * scale, localSongs.Count * rowHeight);
        localSongScroll = GUI.BeginScrollView(viewRect, localSongScroll, contentRect, false, true);

        for (int i = 0; i < localSongs.Count; i++)
        {
            LocalSongEntry entry = localSongs[i];
            Rect rowRect = new Rect(0f, i * rowHeight, contentRect.width, rowHeight - 4f * scale);
            bool selected = i == selectedLocalSongIndex;
            DrawRect(rowRect, selected ? new Color(0.08f, 0.42f, 1f, 0.72f) : new Color(0f, 0f, 0f, 0.24f));

            string songName = entry == null || string.IsNullOrWhiteSpace(entry.Name) ? "Missing Song" : entry.Name;
            GUI.Label(new Rect(rowRect.x + 8f * scale, rowRect.y, rowRect.width - 86f * scale, rowRect.height), songName, songLabelStyle);

            string buttonText = selected && musicSource != null && musicSource.isPlaying ? "PLAYING" : "PLAY";
            bool previousEnabled = GUI.enabled;
            GUI.enabled = !isLoadingLocalSong;
            if (GUI.Button(new Rect(rowRect.x + rowRect.width - 74f * scale, rowRect.y + 3f * scale, 68f * scale, rowRect.height - 6f * scale), buttonText))
            {
                PlayLocalSong(i);
            }

            GUI.enabled = previousEnabled;
        }

        GUI.EndScrollView();
    }

    private void DrawDifficultyButtons(float x, float y, float width, float height, float scale)
    {
        RhythmDifficulty[] difficulties =
        {
            RhythmDifficulty.Easy,
            RhythmDifficulty.Normal,
            RhythmDifficulty.Hard
        };

        float gap = 6f * scale;
        float buttonWidth = (width - gap * 2f) / difficulties.Length;
        bool previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && !isLoadingLocalSong && !isRequestingMurekaSong;

        for (int i = 0; i < difficulties.Length; i++)
        {
            RhythmDifficulty difficulty = difficulties[i];
            Rect rect = new Rect(x + i * (buttonWidth + gap), y, buttonWidth, height);
            bool selected = difficulty == selectedDifficulty;
            DrawRect(rect, selected ? new Color(0.08f, 0.42f, 1f, 0.62f) : new Color(0f, 0f, 0f, 0.18f));
            if (GUI.Button(rect, GetDifficultyPreset(difficulty).Label))
            {
                SetDifficulty(difficulty);
            }
        }

        GUI.enabled = previousEnabled;
    }

    private void ReadInput()
    {
        if (isEditingPrompt)
        {
            return;
        }

        Keyboard keyboard = Keyboard.current;
        if (songSelectionVisible)
        {
            if (keyboard != null)
            {
                if (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame)
                {
                    SelectSongOffset(-1);
                }

                if (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame)
                {
                    SelectSongOffset(1);
                }

                if ((keyboard.enterKey.wasPressedThisFrame || keyboard.spaceKey.wasPressedThisFrame) && selectedLocalSongIndex >= 0)
                {
                    PlayLocalSong(selectedLocalSongIndex);
                }
            }

            return;
        }

        if (keyboard != null)
        {
            if (keyboard.qKey.wasPressedThisFrame)
            {
                TryHit(NoteKind.GoodTap);
            }

            if (keyboard.eKey.wasPressedThisFrame)
            {
                TryHit(NoteKind.BadTap);
            }
        }

        Mouse mouse = Mouse.current;
        if (mouse == null)
        {
            return;
        }

        ReadWheelInput(mouse.scroll.ReadValue().y);
    }

    private void ReadWheelInput(float scrollY)
    {
        if (Mathf.Abs(scrollY) <= 0.001f)
        {
            if (Time.unscaledTime - lastWheelSignalTime >= WheelGestureReleaseDelay)
            {
                wheelInputAccumulator = 0f;
                wheelGestureConsumed = false;
            }

            return;
        }

        lastWheelSignalTime = Time.unscaledTime;
        if (wheelGestureConsumed)
        {
            return;
        }

        if (!Mathf.Approximately(wheelInputAccumulator, 0f)
            && Mathf.Sign(wheelInputAccumulator) != Mathf.Sign(scrollY))
        {
            wheelInputAccumulator = 0f;
        }

        wheelInputAccumulator += scrollY;
        if (Mathf.Abs(wheelInputAccumulator) < WheelGestureThreshold)
        {
            return;
        }

        NoteKind wheelKind = wheelInputAccumulator > 0f
            ? NoteKind.GoodWheelUp
            : NoteKind.BadWheelDown;
        wheelGestureConsumed = true;
        wheelInputAccumulator = 0f;
        TryHit(wheelKind);
    }

    private void TryHit(NoteKind inputKind)
    {
        Note target = FindClosestNote(inputKind);
        if (target != null)
        {
            ApplyHit(target);
            return;
        }

        Note wrongTarget = FindClosestSameInputFamilyNote(inputKind);
        if (wrongTarget != null)
        {
            ApplyMiss(wrongTarget);
        }
    }

    private Note FindClosestNote(NoteKind kind)
    {
        Note best = null;
        float bestDelta = GetHitWindow(kind);
        double now = AudioSettings.dspTime;

        for (int i = 0; i < notes.Count; i++)
        {
            Note note = notes[i];
            if (note.Judged || note.Kind != kind)
            {
                continue;
            }

            float delta = (float)Math.Abs(now - note.HitDspTime);
            if (delta <= bestDelta)
            {
                best = note;
                bestDelta = delta;
            }
        }

        return best;
    }

    private Note FindClosestSameInputFamilyNote(NoteKind inputKind)
    {
        Note best = null;
        float bestDelta = GetMissInputWindow(inputKind);
        double now = AudioSettings.dspTime;

        for (int i = 0; i < notes.Count; i++)
        {
            Note note = notes[i];
            if (note.Judged || !UsesSameInputFamily(inputKind, note.Kind))
            {
                continue;
            }

            float delta = (float)Math.Abs(now - note.HitDspTime);
            if (delta <= bestDelta)
            {
                best = note;
                bestDelta = delta;
            }
        }

        return best;
    }

    private static bool UsesSameInputFamily(NoteKind inputKind, NoteKind noteKind)
    {
        return IsWheelNote(inputKind) == IsWheelNote(noteKind);
    }

    private static float GetHitWindow(NoteKind kind)
    {
        return IsWheelNote(kind) ? WheelHitWindow : HitWindow;
    }

    private static float GetMissInputWindow(NoteKind kind)
    {
        return IsWheelNote(kind) ? WheelMissInputWindow : TapMissInputWindow;
    }

    private static bool IsWheelNote(NoteKind kind)
    {
        return kind == NoteKind.BadWheelDown || kind == NoteKind.GoodWheelUp;
    }

    private void ApplyHit(Note note)
    {
        float delta = (float)Math.Abs(AudioSettings.dspTime - note.HitDspTime);
        JudgementKind judgement;
        int points;

        if (delta <= 0.075f)
        {
            judgement = JudgementKind.Perfect;
            points = 1000;
        }
        else if (delta <= 0.17f)
        {
            judgement = JudgementKind.Great;
            points = 650;
        }
        else
        {
            judgement = JudgementKind.Good;
            points = 350;
        }

        combo++;
        bestCombo = Mathf.Max(bestCombo, combo);
        score += points + combo * 12;
        successfulHitCount++;
        note.Judged = true;
        PlayHitFeedback(note);
        FlashJudgement(judgement);
        if (IsWheelNote(note.Kind))
        {
            StartCoroutine(ScratchLongNoteRoutine(note));
        }
        else
        {
            ClearNote(note);
        }
    }

    private void ApplyMiss(Note note)
    {
        combo = 0;
        note.Judged = true;
        PlayCharacterMissReaction();
        FlashJudgement(JudgementKind.Miss);
        ClearNote(note);
    }

    private void ClearNote(Note note)
    {
        if (note.Root != null)
        {
            Destroy(note.Root);
            note.Root = null;
        }
    }

    private IEnumerator ScratchLongNoteRoutine(Note note)
    {
        if (note.Root == null)
        {
            yield break;
        }

        GameObject root = note.Root;
        root.transform.position = new Vector3(HitX, note.LaneY, 0f);

        bool red = note.Kind == NoteKind.BadWheelDown;
        Vector2 direction = red
            ? new Vector2(0.72f, -0.72f).normalized
            : new Vector2(0.72f, 0.72f).normalized;
        Transform longVisual = root.transform.Find(red ? "Red Long Note" : "Blue Long Note");
        Vector3 visualBaseScale = longVisual != null ? longVisual.localScale : Vector3.one;
        Transform tailVisual = root.transform.Find("Long Tail Cap");
        Vector3 tailBasePosition = tailVisual != null ? tailVisual.localPosition : Vector3.zero;
        Vector3 tailBaseScale = tailVisual != null ? tailVisual.localScale : Vector3.one;
        Vector3 rootBasePosition = root.transform.position;
        SpriteRenderer[] renderers = root.GetComponentsInChildren<SpriteRenderer>(true);
        Color[] baseColors = new Color[renderers.Length];
        for (int i = 0; i < renderers.Length; i++)
        {
            baseColors[i] = renderers[i].color;
        }

        int nextFxIndex = 0;
        float elapsed = 0f;
        while (elapsed < LongNoteScratchDuration && root != null)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / LongNoteScratchDuration);
            float eased = 1f - Mathf.Pow(1f - t, 3f);

            if (longVisual != null)
            {
                longVisual.localScale = new Vector3(
                    visualBaseScale.x * Mathf.Lerp(1f, 1.08f, eased),
                    visualBaseScale.y * Mathf.Lerp(1f, 0.025f, eased),
                    visualBaseScale.z);
            }

            if (tailVisual != null)
            {
                tailVisual.localPosition = Vector3.Lerp(tailBasePosition, Vector3.zero, eased);
                tailVisual.localScale = tailBaseScale * Mathf.Lerp(1f, 0.55f, eased);
            }

            float alpha = 1f - Mathf.SmoothStep(0.35f, 1f, t);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null)
                {
                    continue;
                }

                Color color = baseColors[i];
                color.a *= alpha;
                renderers[i].color = color;
            }

            float jitter = (1f - t) * 0.055f;
            root.transform.position = rootBasePosition + new Vector3(
                Mathf.Sin(elapsed * 155f) * jitter,
                Mathf.Cos(elapsed * 181f) * jitter,
                0f);

            int fxTarget = Mathf.Min(4, Mathf.FloorToInt(t * 5f));
            while (nextFxIndex < fxTarget)
            {
                float distance = 0.52f + nextFxIndex * 0.72f;
                Vector3 fxPosition = rootBasePosition + new Vector3(
                    direction.x * distance,
                    direction.y * distance,
                    0f);
                SpawnHitFxAt(note.Kind, fxPosition, 0.72f + nextFxIndex * 0.045f);
                PlayLongScratchStepSound(note.Kind, nextFxIndex);
                nextFxIndex++;
            }

            yield return null;
        }

        ClearNote(note);
    }

    private void UpdateNotes()
    {
        if (notes.Count == 0)
        {
            return;
        }

        bool allJudged = true;
        double now = AudioSettings.dspTime;

        for (int i = 0; i < notes.Count; i++)
        {
            Note note = notes[i];
            if (note.Judged)
            {
                continue;
            }

            allJudged = false;
            float x = HitX + (float)((note.HitDspTime - now) * NoteSpeed);

            if (note.Root != null)
            {
                Vector3 notePosition = new Vector3(x, note.LaneY, 0f);
                UpdateRedNoteGlitch(note, ref notePosition);
                note.Root.transform.position = notePosition;
                bool visible = x <= SpawnX && x >= DespawnX;
                if (note.Root.activeSelf != visible)
                {
                    note.Root.SetActive(visible);
                }

            }

            if (now - note.HitDspTime > MissWindow)
            {
                ApplyMiss(note);
            }
        }

        if (allJudged && !chartFinished && now > songStartDspTime + chartDuration + 1.1f)
        {
            chartFinished = true;
            murekaStatus = "Song finished. Open SONGS to replay or choose another track.";
        }
    }

    private void UpdateJudgeRing()
    {
        if (judgeRing == null || judgeRingRenderer == null)
        {
            return;
        }

        float pulse = 1f + Mathf.Sin(Time.time * 8f) * 0.045f;
        float targetY = GetAutoFollowJudgeY();
        Vector3 position = judgeRing.position;
        float followT = 1f - Mathf.Exp(-AutoFollowSpeed * Time.deltaTime);
        judgeRing.position = new Vector3(HitX, Mathf.Lerp(position.y, targetY, followT), 0f);
        judgeRing.localScale = new Vector3(pulse, pulse, 1f);
        float ringAlpha = Mathf.Lerp(0.82f, 1f, Mathf.PingPong(Time.time * 1.8f, 1f));
        judgeRingRenderer.color = new Color(1f, 1f, 1f, ringAlpha);
    }

    private void UpdateHitLineBlink()
    {
        if (hitLineRenderer == null)
        {
            return;
        }

        float phase = Mathf.Repeat(Time.unscaledTime, 1f);
        hitLineRenderer.enabled = phase < 0.56f;
    }

    private void UpdatePromptLane()
    {
        if (promptLaneSegments[0] == null || promptLaneSegments[1] == null)
        {
            return;
        }

        const float scrollSpeed = 0.34f;
        const float promptClipX = HitX + 0.55f;
        float delta = scrollSpeed * Time.unscaledDeltaTime;
        for (int i = 0; i < promptLaneSegments.Length; i++)
        {
            promptLaneSegments[i].localPosition += Vector3.left * delta;
        }

        for (int i = 0; i < promptLaneSegments.Length; i++)
        {
            Transform segment = promptLaneSegments[i];
            if (segment.localPosition.x + promptLaneSegmentWidth >= promptClipX)
            {
                continue;
            }

            Transform other = promptLaneSegments[1 - i];
            segment.localPosition = new Vector3(
                other.localPosition.x + promptLaneSegmentWidth + 1.2f,
                LaneY,
                0f);
        }

        for (int i = 0; i < promptLaneSegments.Length; i++)
        {
            float hiddenWidth = promptClipX - promptLaneSegments[i].position.x;
            int hiddenCharacters = Mathf.Clamp(
                Mathf.CeilToInt(hiddenWidth / Mathf.Max(0.01f, promptLaneCharacterWidth)),
                0,
                PromptLaneMessage.Length);
            if (hiddenCharacters == promptLaneHiddenCharacters[i] || promptLaneTexts[i] == null)
            {
                continue;
            }

            promptLaneHiddenCharacters[i] = hiddenCharacters;
            if (hiddenCharacters <= 0)
            {
                promptLaneTexts[i].text = PromptLaneMessage;
            }
            else if (hiddenCharacters >= PromptLaneMessage.Length)
            {
                promptLaneTexts[i].text = string.Empty;
            }
            else
            {
                promptLaneTexts[i].text =
                    "<color=#FFFFFF00>" + PromptLaneMessage.Substring(0, hiddenCharacters) +
                    "</color>" + PromptLaneMessage.Substring(hiddenCharacters);
            }
        }
    }

    private float GetHeartFill()
    {
        return notes.Count > 0 ? Mathf.Clamp01(successfulHitCount / (float)notes.Count) : 0f;
    }

    private void UpdateHeartAndWorldColor()
    {
        float targetHeartFill = GetHeartFill();
        ApplyCharacterSkinForHeart(targetHeartFill);
        displayedHeartFill = Mathf.MoveTowards(displayedHeartFill, targetHeartFill, Time.unscaledDeltaTime * 1.6f);

        float targetColorRecovery = Mathf.Clamp01(targetHeartFill / 0.70f);
        displayedColorRecovery = targetColorRecovery >= 0.999f
            ? 1f
            : Mathf.MoveTowards(displayedColorRecovery, targetColorRecovery, Time.unscaledDeltaTime * 1.8f);
        ApplyWorldColorRecovery(displayedColorRecovery);
    }

    private void RegisterColorRecoveryTargets(Transform stage)
    {
        colorRecoveryTargets.Clear();
        SpriteRenderer[] renderers = stage.GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            SpriteRenderer renderer = renderers[i];
            Color original = renderer.color;
            float luminance = original.r * 0.299f + original.g * 0.587f + original.b * 0.114f;
            colorRecoveryTargets.Add(new ColorRecoveryTarget
            {
                Renderer = renderer,
                OriginalColor = original,
                GrayscaleColor = new Color(luminance, luminance, luminance, original.a)
            });
        }
    }

    private void ApplyWorldColorRecovery(float amount)
    {
        amount = Mathf.Clamp01(amount);
        if (backgroundColorRecoveryMaterial != null)
        {
            backgroundColorRecoveryMaterial.SetFloat("_ColorRecovery", amount);
        }

        for (int i = 0; i < colorRecoveryTargets.Count; i++)
        {
            ColorRecoveryTarget target = colorRecoveryTargets[i];
            if (target.Renderer != null)
            {
                target.Renderer.color = Color.Lerp(target.GrayscaleColor, target.OriginalColor, amount);
            }
        }

        if (gameCamera != null)
        {
            float luminance = cameraOriginalBackgroundColor.r * 0.299f
                + cameraOriginalBackgroundColor.g * 0.587f
                + cameraOriginalBackgroundColor.b * 0.114f;
            Color grayscale = new Color(luminance, luminance, luminance, cameraOriginalBackgroundColor.a);
            gameCamera.backgroundColor = Color.Lerp(grayscale, cameraOriginalBackgroundColor, amount);
        }
    }

    private float GetAutoFollowJudgeY()
    {
        Note target = null;
        float bestDistance = AutoFollowLookAhead;
        double now = AudioSettings.dspTime;

        for (int i = 0; i < notes.Count; i++)
        {
            Note note = notes[i];
            if (note.Judged)
            {
                continue;
            }

            float timeUntilHit = (float)(note.HitDspTime - now);
            if (timeUntilHit < -MissWindow || timeUntilHit > AutoFollowLookAhead)
            {
                continue;
            }

            float distance = Mathf.Abs(timeUntilHit);
            if (distance <= bestDistance)
            {
                target = note;
                bestDistance = distance;
            }
        }

        return target != null ? target.LaneY : LaneY;
    }

    private void RestartChart()
    {
        for (int i = 0; i < notes.Count; i++)
        {
            ClearNote(notes[i]);
        }

        notes.Clear();
        wheelInputAccumulator = 0f;
        wheelGestureConsumed = false;
        lastWheelSignalTime = -999f;
        score = 0;
        combo = 0;
        successfulHitCount = 0;
        displayedHeartFill = 0f;
        displayedColorRecovery = 0f;
        ApplyWorldColorRecovery(0f);
        if (chart.Count == 0)
        {
            murekaStatus = isRequestingMurekaSong ? murekaStatus : "Select a local song to play.";
            return;
        }

        songStartDspTime = AudioSettings.dspTime + MusicLeadIn;
        chartDuration = Mathf.Max(generatedSongLength, chart[chart.Count - 1].Time);
        chartFinished = false;

        for (int i = 0; i < chart.Count; i++)
        {
            NoteSpec spec = chart[i];
            bool showInputGuide = !tutorialKindsShown.Contains(spec.Kind);
            CreateNote(
                spec.Kind,
                spec.LaneIndex,
                songStartDspTime + spec.Time + audioVisualLatency,
                showInputGuide);
            if (showInputGuide)
            {
                tutorialKindsShown.Add(spec.Kind);
            }
        }

        PlayCurrentSong();
        judgementKind = JudgementKind.None;
        judgementVisibleUntil = 0f;
    }

    private void CreateNote(NoteKind kind, int laneIndex, double hitDspTime, bool showInputGuide)
    {
        float laneY = GetNoteY(kind, laneIndex);
        GameObject root = new GameObject(kind.ToString());
        root.transform.SetParent(notesRoot, false);
        root.transform.position = new Vector3(SpawnX, laneY, 0f);
        root.SetActive(false);

        bool isLongNote = kind == NoteKind.BadWheelDown || kind == NoteKind.GoodWheelUp;
        if (isLongNote)
        {
            CreateLongBody(root.transform, kind);
        }

        if (!isLongNote)
        {
            Sprite headSprite = GetHeadSprite(kind);
            GameObject head = new GameObject("Head");
            head.transform.SetParent(root.transform, false);
            head.transform.localScale = Vector3.one * 1.22f;

            SpriteRenderer headRenderer = head.AddComponent<SpriteRenderer>();
            headRenderer.sprite = headSprite;
            headRenderer.sortingOrder = 12;
        }

        Transform[] glitchLayers = null;
        Transform[] glitchBars = null;
        if (kind == NoteKind.BadTap || kind == NoteKind.BadWheelDown)
        {
            glitchLayers = CreateRedNoteGlitchLayers(root.transform, out glitchBars);
        }

        if (showInputGuide)
        {
            CreateNoteInputGuide(root.transform, kind);
        }

        notes.Add(new Note
        {
            Kind = kind,
            HitDspTime = hitDspTime,
            LaneY = laneY,
            Root = root,
            Judged = false,
            GlitchLayers = glitchLayers,
            GlitchBars = glitchBars,
            GlitchSeed = UnityEngine.Random.Range(0.1f, 999f)
        });
    }

    private static Transform[] CreateRedNoteGlitchLayers(Transform noteRoot, out Transform[] glitchBars)
    {
        SpriteRenderer[] sources = noteRoot.GetComponentsInChildren<SpriteRenderer>(true);
        var layers = new List<Transform>(sources.Length * 2);
        for (int i = 0; i < sources.Length; i++)
        {
            SpriteRenderer source = sources[i];
            if (source == null || source.sprite == null)
            {
                continue;
            }

            for (int channel = 0; channel < 2; channel++)
            {
                GameObject layerObject = new GameObject(channel == 0 ? "Red Glitch Split" : "Cyan Glitch Split");
                layerObject.transform.SetParent(source.transform, false);

                SpriteRenderer layerRenderer = layerObject.AddComponent<SpriteRenderer>();
                layerRenderer.sprite = source.sprite;
                layerRenderer.flipX = source.flipX;
                layerRenderer.flipY = source.flipY;
                layerRenderer.drawMode = source.drawMode;
                layerRenderer.size = source.size;
                layerRenderer.sortingLayerID = source.sortingLayerID;
                layerRenderer.sortingOrder = source.sortingOrder + 1 + channel;
                layerRenderer.color = channel == 0
                    ? new Color(1f, 0.03f, 0.1f, 0.34f)
                    : new Color(0.04f, 0.94f, 1f, 0.28f);
                layerObject.SetActive(false);
                layers.Add(layerObject.transform);
            }
        }

        var bars = new List<Transform>(6);
        for (int i = 0; i < 6; i++)
        {
            GameObject barObject = new GameObject("Red Note Noise Bar");
            barObject.transform.SetParent(noteRoot, false);
            SpriteRenderer barRenderer = barObject.AddComponent<SpriteRenderer>();
            barRenderer.sprite = whiteSprite;
            barRenderer.sortingOrder = 20 + i;
            switch (i % 3)
            {
                case 0:
                    barRenderer.color = new Color(0.05f, 0.92f, 1f, 0.58f);
                    break;
                case 1:
                    barRenderer.color = new Color(1f, 0.02f, 0.09f, 0.72f);
                    break;
                default:
                    barRenderer.color = new Color(1f, 0.88f, 0.96f, 0.42f);
                    break;
            }

            barObject.SetActive(false);
            bars.Add(barObject.transform);
        }

        glitchBars = bars.ToArray();
        return layers.ToArray();
    }

    private static void UpdateRedNoteGlitch(Note note, ref Vector3 notePosition)
    {
        if ((note.GlitchLayers == null || note.GlitchLayers.Length == 0)
            && (note.GlitchBars == null || note.GlitchBars.Length == 0))
        {
            return;
        }

        int glitchFrame = Mathf.FloorToInt(Time.unscaledTime * 45f);
        float noise = Mathf.PerlinNoise(note.GlitchSeed, glitchFrame * 0.219f);
        bool active = noise > 0.43f;
        float strength = noise > 0.78f ? 0.13f : 0.065f;
        if (active)
        {
            float horizontal = ((glitchFrame & 1) == 0 ? -1f : 1f) * strength;
            float vertical = Mathf.Sin(glitchFrame * 2.81f + note.GlitchSeed) * strength * 0.4f;
            notePosition += new Vector3(horizontal * 0.28f, vertical * 0.28f, 0f);
        }

        for (int i = 0; note.GlitchLayers != null && i < note.GlitchLayers.Length; i++)
        {
            Transform layer = note.GlitchLayers[i];
            if (layer == null)
            {
                continue;
            }

            float layerNoise = Mathf.PerlinNoise(note.GlitchSeed + i * 1.37f, glitchFrame * 0.337f);
            bool layerActive = active && layerNoise > 0.31f;
            if (layer.gameObject.activeSelf != layerActive)
            {
                layer.gameObject.SetActive(layerActive);
            }

            if (layerActive)
            {
                float direction = (i & 1) == 0 ? -1f : 1f;
                float tear = strength * Mathf.Lerp(0.65f, 1.35f, layerNoise);
                layer.localPosition = new Vector3(
                    direction * tear,
                    Mathf.Sin(glitchFrame * 1.91f + i * 2.4f) * strength * 0.3f,
                    0f);
                layer.localScale = new Vector3(1f + layerNoise * 0.045f, 1f - layerNoise * 0.025f, 1f);
            }
        }

        bool longNote = note.Kind == NoteKind.BadWheelDown;
        for (int i = 0; note.GlitchBars != null && i < note.GlitchBars.Length; i++)
        {
            Transform bar = note.GlitchBars[i];
            if (bar == null)
            {
                continue;
            }

            float barNoise = Mathf.PerlinNoise(note.GlitchSeed + 17f + i * 2.13f, glitchFrame * 0.461f);
            bool barActive = active && barNoise > 0.38f;
            if (bar.gameObject.activeSelf != barActive)
            {
                bar.gameObject.SetActive(barActive);
            }

            if (!barActive)
            {
                continue;
            }

            float phase = Mathf.Repeat(barNoise + i * 0.173f + glitchFrame * 0.071f, 1f);
            if (longNote)
            {
                float distance = phase * LongNoteTailDistance;
                bar.localPosition = new Vector3(
                    distance * 0.7071f + Mathf.Sin(glitchFrame + i) * 0.08f,
                    -distance * 0.7071f + Mathf.Cos(glitchFrame * 1.7f + i) * 0.07f,
                    0f);
            }
            else
            {
                bar.localPosition = new Vector3(
                    Mathf.Lerp(-0.72f, 0.72f, phase),
                    Mathf.Sin(note.GlitchSeed + i * 4.1f + glitchFrame * 0.63f) * 0.62f,
                    0f);
            }

            float width = (longNote ? 0.34f : 0.22f) + barNoise * (longNote ? 0.82f : 0.58f);
            float height = 0.018f + Mathf.Repeat(barNoise * 3.7f, 1f) * 0.045f;
            bar.localScale = new Vector3(width, height, 1f);
        }
    }

    private static void CreateNoteInputGuide(Transform noteRoot, NoteKind kind)
    {
        Sprite guideSprite;
        switch (kind)
        {
            case NoteKind.GoodTap:
                guideSprite = qGuideSprite;
                break;
            case NoteKind.BadTap:
                guideSprite = eGuideSprite;
                break;
            case NoteKind.GoodWheelUp:
                guideSprite = mouseUpGuideSprite;
                break;
            default:
                guideSprite = mouseDownGuideSprite;
                break;
        }

        if (guideSprite == null)
        {
            return;
        }

        GameObject guide = new GameObject("Input Guide");
        guide.transform.SetParent(noteRoot, false);
        guide.transform.localPosition = new Vector3(0f, -1.18f, 0f);
        guide.transform.localScale = Vector3.one * 0.86f;

        SpriteRenderer renderer = guide.AddComponent<SpriteRenderer>();
        renderer.sprite = guideSprite;
        renderer.sortingOrder = 22;
    }

    private static float GetLaneY(int laneIndex)
    {
        int clampedIndex = Mathf.Clamp(laneIndex, 0, NoteYs.Length - 1);
        return NoteYs[clampedIndex];
    }

    private static float GetNoteY(NoteKind kind, int laneIndex)
    {
        return kind == NoteKind.GoodWheelUp ? LaneY - BlueLongNoteVerticalOffset : LaneY;
    }

    private void CreateLongBody(Transform root, NoteKind kind)
    {
        bool bad = kind == NoteKind.BadWheelDown;
        Sprite sheetSprite = bad ? redLongSprite : blueLongSprite;
        if (sheetSprite == null)
        {
            Debug.LogWarning("Long-note UI sprite is missing from the UI sheet.");
            return;
        }

        GameObject sheetVisual = new GameObject(bad ? "Red Long Note UI" : "Blue Long Note UI");
        sheetVisual.transform.SetParent(root, false);
        sheetVisual.transform.localRotation = Quaternion.Euler(0f, 0f, bad ? -135f : -45f);
        sheetVisual.transform.localScale = Vector3.one * 0.94f;

        SpriteRenderer sheetRenderer = sheetVisual.AddComponent<SpriteRenderer>();
        sheetRenderer.sprite = sheetSprite;
        sheetRenderer.sortingOrder = 10;
    }

    private static Sprite GetHeadSprite(NoteKind kind)
    {
        switch (kind)
        {
            case NoteKind.GoodTap:
                return GetRandomSprite(blueTapSprites, goodTapSprite);
            case NoteKind.GoodWheelUp:
                return GetFirstSprite(blueTapSprites, goodLongHeadSprite);
            case NoteKind.BadWheelDown:
                return GetFirstSprite(redTapSprites, badLongHeadSprite);
            default:
                return GetRandomSprite(redTapSprites, badTapSprite);
        }
    }

    private static Sprite GetFirstSprite(Sprite[] sprites, Sprite fallback)
    {
        return sprites != null && sprites.Length > 0 && sprites[0] != null ? sprites[0] : fallback;
    }

    private static Sprite GetRandomSprite(Sprite[] sprites, Sprite fallback)
    {
        if (sprites == null || sprites.Length == 0)
        {
            return fallback;
        }

        return sprites[UnityEngine.Random.Range(0, sprites.Length)];
    }

    private void SetupCamera()
    {
        Camera camera = Camera.main;
        if (camera == null)
        {
            GameObject cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            camera = cameraObject.AddComponent<Camera>();
            cameraObject.AddComponent<AudioListener>();
        }

        camera.transform.position = new Vector3(0f, 0f, -10f);
        camera.orthographic = true;
        camera.orthographicSize = 5f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.12f, 0.035f, 0.38f, 1f);
        cameraOriginalBackgroundColor = camera.backgroundColor;
        camera.allowMSAA = true;
        QualitySettings.antiAliasing = Mathf.Max(4, QualitySettings.antiAliasing);
        gameCamera = camera;
        cameraBasePosition = camera.transform.position;
    }

    private void SetupStage()
    {
        GameObject stage = new GameObject("Prototype Stage");

        if (!SetupParallaxBackground(stage.transform))
        {
            CreateBlock(stage.transform, "Sky", new Vector2(0f, 0.8f), new Vector2(19f, 9.6f), new Color(0.24f, 0.07f, 0.67f, 1f), -20);
            CreateBlock(stage.transform, "Distant Glow", new Vector2(1.7f, -0.65f), new Vector2(16.5f, 3.15f), new Color(0.78f, 0.22f, 0.94f, 0.42f), -19);
            CreateBlock(stage.transform, "Distant City", new Vector2(1.7f, -1.55f), new Vector2(16.5f, 2.15f), new Color(0.17f, 0.62f, 0.96f, 0.38f), -18);
            CreateBlock(stage.transform, "Park Hill", new Vector2(0f, -3.65f), new Vector2(19f, 1.45f), new Color(0.86f, 0.28f, 0.82f, 1f), -15);
            CreateBlock(stage.transform, "Ground", new Vector2(0f, -4.45f), new Vector2(19f, 1.1f), new Color(1f, 0.33f, 0.74f, 1f), -14);
            CreateStageDecorations(stage.transform);
        }
        RegisterColorRecoveryTargets(stage.transform);
        CreatePromptLane(stage.transform);

        GameObject hitLine = new GameObject("Blinking Search Cursor");
        hitLine.transform.SetParent(stage.transform, false);
        hitLine.transform.position = new Vector3(HitX, LaneY, 0f);
        hitLine.transform.localScale = new Vector3(0.24f, 1f, 1f);
        hitLineRenderer = hitLine.AddComponent<SpriteRenderer>();
        hitLineRenderer.sprite = hitLineSprite;
        hitLineRenderer.sortingOrder = 2;

        GameObject ring = new GameObject("Judge Ring");
        ring.transform.SetParent(stage.transform, false);
        ring.transform.position = new Vector3(HitX, LaneY, 0f);
        judgeRing = ring.transform;
        judgeRingRenderer = ring.AddComponent<SpriteRenderer>();
        judgeRingRenderer.sprite = judgeRingSprite;
        judgeRingRenderer.sortingOrder = 10;

        notesRoot = new GameObject("Rhythm Notes").transform;
        ApplyWorldColorRecovery(0f);
    }

    private bool SetupParallaxBackground(Transform parent)
    {
        Texture2D background = Resources.Load<Texture2D>("Background/bg");
        Texture2D middleFar = Resources.Load<Texture2D>("Background/mg1");
        Texture2D middle = Resources.Load<Texture2D>("Background/mg");
        Texture2D foreground = Resources.Load<Texture2D>("Background/fg");
        if (background == null || middleFar == null || middle == null || foreground == null)
        {
            Debug.LogWarning("Parallax background textures were not imported yet. Using the fallback stage.");
            return false;
        }

        parallaxLayers.Clear();
        float importScale = background.width / BackgroundSourceWidth;
        float pixelsPerUnit = BackgroundSourcePixelsPerUnit * importScale;
        CreateParallaxLayer(parent, "BG", background, pixelsPerUnit, 0f, -30, 0.025f);
        CreateParallaxLayer(parent, "MG1", middleFar, pixelsPerUnit, 5f - middleFar.height / pixelsPerUnit * 0.5f, -27, 0.12f);
        CreateParallaxLayer(parent, "MG", middle, pixelsPerUnit, -5f + middle.height / pixelsPerUnit * 0.5f, -24, 0.30f);
        CreateParallaxLayer(parent, "FG", foreground, pixelsPerUnit, -5f + foreground.height / pixelsPerUnit * 0.5f, -10, 1.20f);
        return true;
    }

    private void CreateParallaxLayer(
        Transform parent,
        string layerName,
        Texture2D texture,
        float pixelsPerUnit,
        float centerY,
        int sortingOrder,
        float speed)
    {
        Sprite sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            pixelsPerUnit,
            0,
            SpriteMeshType.FullRect);
        float tileWidth = texture.width / pixelsPerUnit;
        float viewHalfWidth = gameCamera != null
            ? gameCamera.orthographicSize * gameCamera.aspect
            : 8.8889f;
        float firstCenterX = -viewHalfWidth + tileWidth * 0.5f;
        Transform first = CreateParallaxTile(parent, layerName + " A", sprite, new Vector2(firstCenterX, centerY), sortingOrder);
        Transform second = CreateParallaxTile(parent, layerName + " B", sprite, new Vector2(firstCenterX + tileWidth - 0.01f, centerY), sortingOrder);
        parallaxLayers.Add(new ParallaxLayer
        {
            FirstTile = first,
            SecondTile = second,
            TileWidth = tileWidth - 0.01f,
            CenterY = centerY,
            Speed = speed
        });
    }

    private static Transform CreateParallaxTile(
        Transform parent,
        string name,
        Sprite sprite,
        Vector2 position,
        int sortingOrder)
    {
        GameObject tile = new GameObject(name);
        tile.transform.SetParent(parent, false);
        tile.transform.localPosition = new Vector3(position.x, position.y, 0f);
        SpriteRenderer renderer = tile.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.sortingOrder = sortingOrder;
        if (backgroundColorRecoveryMaterial != null)
        {
            renderer.sharedMaterial = backgroundColorRecoveryMaterial;
        }

        return tile.transform;
    }

    private void UpdateParallaxBackground()
    {
        // The song-select screen keeps the backdrop drifting by lazily.
        float deltaTime = Time.deltaTime * (songSelectionVisible ? 0.3f : 1f);
        for (int i = 0; i < parallaxLayers.Count; i++)
        {
            ParallaxLayer layer = parallaxLayers[i];
            float movement = layer.Speed * deltaTime;
            layer.FirstTile.localPosition += Vector3.left * movement;
            layer.SecondTile.localPosition += Vector3.left * movement;
            WrapParallaxTile(layer.FirstTile, layer.SecondTile, layer.TileWidth);
            WrapParallaxTile(layer.SecondTile, layer.FirstTile, layer.TileWidth);
        }
    }

    private void RebindAndAlignParallaxLayers()
    {
        if (gameCamera == null)
        {
            gameCamera = Camera.main;
        }

        if (parallaxLayers.Count == 0)
        {
            string[] names = { "BG", "MG1", "MG", "FG" };
            float[] speeds = { 0.025f, 0.12f, 0.30f, 1.20f };
            for (int i = 0; i < names.Length; i++)
            {
                GameObject firstObject = GameObject.Find(names[i] + " A");
                GameObject secondObject = GameObject.Find(names[i] + " B");
                if (firstObject == null || secondObject == null)
                {
                    continue;
                }

                SpriteRenderer renderer = firstObject.GetComponent<SpriteRenderer>();
                float width = renderer != null ? renderer.bounds.size.x : 0f;
                if (width <= 0.01f)
                {
                    continue;
                }

                parallaxLayers.Add(new ParallaxLayer
                {
                    FirstTile = firstObject.transform,
                    SecondTile = secondObject.transform,
                    TileWidth = width - 0.01f,
                    CenterY = names[i] == "BG"
                        ? 0f
                        : names[i] == "MG1"
                            ? (gameCamera != null ? gameCamera.orthographicSize : 5f) - renderer.bounds.size.y * 0.5f
                            : -(gameCamera != null ? gameCamera.orthographicSize : 5f) + renderer.bounds.size.y * 0.5f,
                    Speed = speeds[i]
                });
            }
        }

        float viewLeft = gameCamera != null
            ? -gameCamera.orthographicSize * gameCamera.aspect
            : -8.8889f;
        for (int i = 0; i < parallaxLayers.Count; i++)
        {
            ParallaxLayer layer = parallaxLayers[i];
            if (layer.FirstTile == null || layer.SecondTile == null)
            {
                continue;
            }

            SpriteRenderer renderer = layer.FirstTile.GetComponent<SpriteRenderer>();
            float layerHeight = renderer != null ? renderer.bounds.size.y : 0f;
            float viewHalfHeight = gameCamera != null ? gameCamera.orthographicSize : 5f;
            if (layer.FirstTile.name.StartsWith("MG1", StringComparison.Ordinal))
            {
                layer.CenterY = viewHalfHeight - layerHeight * 0.5f;
            }
            else if (!layer.FirstTile.name.StartsWith("BG", StringComparison.Ordinal))
            {
                layer.CenterY = -viewHalfHeight + layerHeight * 0.5f;
            }

            Vector3 firstPosition = layer.FirstTile.localPosition;
            firstPosition.x = viewLeft + layer.TileWidth * 0.5f;
            firstPosition.y = layer.CenterY;
            layer.FirstTile.localPosition = firstPosition;
            Vector3 secondPosition = layer.SecondTile.localPosition;
            secondPosition.x = firstPosition.x + layer.TileWidth;
            secondPosition.y = layer.CenterY;
            layer.SecondTile.localPosition = secondPosition;
        }
    }

    private void WrapParallaxTile(Transform tile, Transform other, float tileWidth)
    {
        float viewLeft = gameCamera != null
            ? -gameCamera.orthographicSize * gameCamera.aspect
            : -8.8889f;
        if (tile.localPosition.x + tileWidth * 0.5f >= viewLeft)
        {
            return;
        }

        Vector3 position = tile.localPosition;
        position.x = other.localPosition.x + tileWidth;
        tile.localPosition = position;
    }

    private void CreatePromptLane(Transform parent)
    {
        if (gameFont == null)
        {
            gameFont = Resources.Load<Font>("Fonts/Hakgyoansim_Dunggeunmiso_B");
        }

        for (int i = 0; i < promptLaneSegments.Length; i++)
        {
            GameObject segmentObject = new GameObject("Scrolling Prompt Lane " + (i + 1));
            segmentObject.transform.SetParent(parent, false);

            TextMesh textMesh = segmentObject.AddComponent<TextMesh>();
            textMesh.text = PromptLaneMessage;
            textMesh.anchor = TextAnchor.MiddleLeft;
            textMesh.alignment = TextAlignment.Left;
            textMesh.fontSize = 64;
            textMesh.characterSize = 0.065f;
            textMesh.fontStyle = FontStyle.Bold;
            textMesh.richText = true;
            textMesh.color = new Color(0.94f, 0.96f, 1f, 0.80f);
            if (gameFont != null)
            {
                textMesh.font = gameFont;
            }

            MeshRenderer renderer = segmentObject.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sortingOrder = -1;
                if (gameFont != null && gameFont.material != null)
                {
                    renderer.sharedMaterial = gameFont.material;
                }
            }

            if (i == 0 && renderer != null)
            {
                promptLaneSegmentWidth = Mathf.Max(24f, renderer.bounds.size.x);
                promptLaneCharacterWidth = promptLaneSegmentWidth / Mathf.Max(1, PromptLaneMessage.Length);
            }

            segmentObject.transform.localPosition = new Vector3(
                HitX + 0.55f + i * (promptLaneSegmentWidth + 1.2f),
                LaneY,
                0f);
            promptLaneSegments[i] = segmentObject.transform;
            promptLaneTexts[i] = textMesh;
            promptLaneHiddenCharacters[i] = -1;
        }
    }

    private static void CreateStageDecorations(Transform stage)
    {
        Vector2[] starPositions =
        {
            new Vector2(-8.15f, 3.9f), new Vector2(-6.4f, 2.65f), new Vector2(-4.25f, 3.55f),
            new Vector2(-2.1f, 2.75f), new Vector2(0.15f, 3.95f), new Vector2(2.3f, 2.9f),
            new Vector2(4.15f, 4.05f), new Vector2(6.15f, 2.7f), new Vector2(8.25f, 3.75f)
        };
        Color[] starColors =
        {
            new Color(1f, 0.72f, 0.92f, 1f), new Color(1f, 0.88f, 0.45f, 1f),
            new Color(0.48f, 0.96f, 1f, 1f), new Color(1f, 0.45f, 0.86f, 1f)
        };

        for (int i = 0; i < starPositions.Length; i++)
        {
            float size = i % 3 == 0 ? 0.22f : 0.11f;
            GameObject star = CreateBlock(stage, "Placeholder Star", starPositions[i], new Vector2(size, size), starColors[i % starColors.Length], -16);
            star.transform.rotation = Quaternion.Euler(0f, 0f, 45f);
        }

        GameObject cyanStreak = CreateBlock(stage, "Cyan Light Streak", new Vector2(-5.9f, 4.25f), new Vector2(2.4f, 0.12f), new Color(0.2f, 0.92f, 1f, 0.85f), -17);
        cyanStreak.transform.rotation = Quaternion.Euler(0f, 0f, 29f);
        GameObject pinkStreak = CreateBlock(stage, "Pink Light Streak", new Vector2(2.25f, 2.7f), new Vector2(4.8f, 0.1f), new Color(1f, 0.34f, 0.9f, 0.75f), -17);
        pinkStreak.transform.rotation = Quaternion.Euler(0f, 0f, 31f);

        float[] buildingHeights = { 1.4f, 2.15f, 1.65f, 2.7f, 1.85f, 2.35f, 1.55f, 2.55f, 1.7f, 2.25f, 1.45f, 2.8f };
        for (int i = 0; i < buildingHeights.Length; i++)
        {
            float x = -7.8f + i * 1.42f;
            float height = buildingHeights[i];
            CreateBlock(stage, "Placeholder City", new Vector2(x, -2.75f + height * 0.5f), new Vector2(0.88f, height), new Color(0.16f, 0.16f + (i % 3) * 0.05f, 0.62f, 0.62f), -17);
            CreateBlock(stage, "City Light", new Vector2(x, -2.05f + height * 0.45f), new Vector2(0.12f, 0.12f), i % 2 == 0 ? GoodColor : new Color(1f, 0.38f, 0.82f, 1f), -16);
        }

        CreateCloud(stage, new Vector2(5.7f, -0.4f), 1.35f, new Color(0.96f, 0.62f, 1f, 0.72f));
        CreateCloud(stage, new Vector2(7.65f, 0.5f), 1.05f, new Color(0.82f, 0.66f, 1f, 0.68f));
        CreateCloud(stage, new Vector2(-7.5f, -0.5f), 0.85f, new Color(0.64f, 0.82f, 1f, 0.48f));

        CreateBlock(stage, "Platform Highlight", new Vector2(0f, -3.92f), new Vector2(19f, 0.11f), new Color(1f, 0.84f, 1f, 1f), -12);
        for (int i = 0; i < 16; i++)
        {
            float x = -8.9f + i * 1.2f;
            CreateBlock(stage, "Platform Tile", new Vector2(x, -4.28f), new Vector2(0.95f, 0.08f), new Color(0.95f, 0.5f, 0.98f, 0.72f), -12);
        }
    }

    private static void CreateCloud(Transform parent, Vector2 center, float scale, Color color)
    {
        CreateCircleBlock(parent, "Cloud", center + new Vector2(-0.65f, 0f) * scale, 0.85f * scale, color, -16);
        CreateCircleBlock(parent, "Cloud", center + new Vector2(0f, 0.18f) * scale, 1.1f * scale, color, -16);
        CreateCircleBlock(parent, "Cloud", center + new Vector2(0.72f, -0.04f) * scale, 0.78f * scale, color, -16);
    }

    private void SetupCharacter()
    {
        SkeletonDataAsset[] dataAssets = Resources.LoadAll<SkeletonDataAsset>("Spine/Character03");
        if (dataAssets == null || dataAssets.Length == 0)
        {
            Debug.LogWarning("Spine character SkeletonDataAsset was not generated yet.");
            return;
        }

        SkeletonDataAsset dataAsset = dataAssets[0];
        SkeletonData skeletonData = dataAsset.GetSkeletonData(true);
        if (skeletonData == null)
        {
            Debug.LogWarning("Spine character data could not be loaded.");
            return;
        }

        SkeletonAnimation[] sceneCharacters = FindObjectsByType<SkeletonAnimation>(FindObjectsSortMode.None);
        for (int i = 0; i < sceneCharacters.Length; i++)
        {
            if (sceneCharacters[i] != null && sceneCharacters[i].name == "Rhythm Runner Character")
            {
                characterAnimation = sceneCharacters[i];
                break;
            }
        }

        if (characterAnimation == null)
        {
            characterAnimation = SkeletonAnimation.NewSkeletonAnimationGameObject(dataAsset);
            characterAnimation.name = "Rhythm Runner Character";
        }

        characterAnimation.Initialize(false);
        ApplyCharacterSkin("Skin0");
        dataAsset.defaultMix = CharacterAnimationMix;
        characterAnimation.AnimationState.Data.DefaultMix = CharacterAnimationMix;

        float dataScale = Mathf.Max(0.0001f, dataAsset.scale);
        float importedHeight = skeletonData.Height * dataScale;
        float importedX = skeletonData.X * dataScale;
        float importedY = skeletonData.Y * dataScale;
        float characterScale = importedHeight > 0.01f ? 5.2f / importedHeight : 0.19f;
        characterAnimation.transform.localScale = Vector3.one * characterScale;
        characterAnimation.transform.position = new Vector3(
            -8.78f - importedX * characterScale,
            -3.15f - importedY * characterScale,
            0f);

        MeshRenderer meshRenderer = characterAnimation.GetComponent<MeshRenderer>();
        if (meshRenderer != null)
        {
            ConfigureSpineRenderer(meshRenderer, 3);
        }

        characterRunAnimation = skeletonData.FindAnimation("run") != null
            ? "run"
            : skeletonData.FindAnimation("Run") != null
                ? "Run"
                : skeletonData.FindAnimation("Walk") != null
                    ? "Walk"
                    : "Idle";

        if (skeletonData.FindAnimation(characterRunAnimation) != null)
        {
            characterAnimation.AnimationState.SetAnimation(0, characterRunAnimation, true);
        }
    }

    private void ApplyCharacterSkinForHeart(float heartFill)
    {
        float percentage = Mathf.Clamp01(heartFill) * 100f;
        string skinName = percentage >= 70f
            ? "Skin70"
            : percentage >= 40f
                ? "Skin40"
                : percentage >= 20f
                    ? "Skin20"
                    : "Skin0";
        ApplyCharacterSkin(skinName);
    }

    private void ApplyCharacterSkin(string skinName)
    {
        if (characterAnimation == null
            || characterAnimation.Skeleton == null
            || currentCharacterSkin == skinName
            || characterAnimation.Skeleton.Data.FindSkin(skinName) == null)
        {
            return;
        }

        characterAnimation.Skeleton.SetSkin(skinName);
        characterAnimation.Skeleton.SetSlotsToSetupPose();
        characterAnimation.AnimationState.Apply(characterAnimation.Skeleton);
        currentCharacterSkin = skinName;
    }

    private void PlayCharacterReaction(NoteKind kind)
    {
        if (characterAnimation == null || characterAnimation.Skeleton == null)
        {
            return;
        }

        string reactionName;
        if (kind == NoteKind.BadWheelDown)
        {
            reactionName = "Rednote_long";
        }
        else if (kind == NoteKind.GoodWheelUp)
        {
            reactionName = "Bluenote_long";
        }
        else
        {
            reactionName = GetRandomAvailableReaction(kind == NoteKind.BadTap);
        }

        if (string.IsNullOrEmpty(reactionName))
        {
            return;
        }

        if (characterAnimation.Skeleton.Data.FindAnimation(reactionName) == null)
        {
            return;
        }

        TrackEntry reactionEntry = characterAnimation.AnimationState.SetAnimation(0, reactionName, false);
        reactionEntry.MixDuration = CharacterAnimationMix;
        if (!string.IsNullOrEmpty(characterRunAnimation))
        {
            TrackEntry runEntry = characterAnimation.AnimationState.AddAnimation(0, characterRunAnimation, true, 0f);
            runEntry.MixDuration = CharacterAnimationMix;
        }
    }

    private void PlayCharacterMissReaction()
    {
        if (characterAnimation == null || characterAnimation.Skeleton == null)
        {
            return;
        }

        const string missAnimation = "Miss";
        if (characterAnimation.Skeleton.Data.FindAnimation(missAnimation) == null)
        {
            return;
        }

        TrackEntry missEntry = characterAnimation.AnimationState.SetAnimation(0, missAnimation, false);
        missEntry.MixDuration = CharacterAnimationMix;
        if (!string.IsNullOrEmpty(characterRunAnimation))
        {
            TrackEntry runEntry = characterAnimation.AnimationState.AddAnimation(0, characterRunAnimation, true, 0f);
            runEntry.MixDuration = CharacterAnimationMix;
        }
    }

    private string GetRandomAvailableReaction(bool red)
    {
        string prefix = red ? "Rednote" : "Bluenote";
        int candidateCount = red ? 2 : 3;
        var available = new List<string>(candidateCount);
        for (int i = 1; i <= candidateCount; i++)
        {
            string candidate = prefix + i;
            if (characterAnimation.Skeleton.Data.FindAnimation(candidate) != null)
            {
                available.Add(candidate);
            }
        }

        if (available.Count == 0)
        {
            return null;
        }

        string previous = red ? lastRedReactionAnimation : lastBlueReactionAnimation;
        int selectedIndex = UnityEngine.Random.Range(0, available.Count);
        if (available.Count > 1 && available[selectedIndex] == previous)
        {
            selectedIndex = (selectedIndex + UnityEngine.Random.Range(1, available.Count)) % available.Count;
        }

        string selected = available[selectedIndex];
        if (red)
        {
            lastRedReactionAnimation = selected;
        }
        else
        {
            lastBlueReactionAnimation = selected;
        }

        return selected;
    }

    private void LoadHitFxAssets()
    {
        SkeletonDataAsset[] blueAssets = Resources.LoadAll<SkeletonDataAsset>("Spine/FX/BlueAttack");
        SkeletonDataAsset[] redAssets = Resources.LoadAll<SkeletonDataAsset>("Spine/FX/RedAttack");
        blueHitFxData = blueAssets != null && blueAssets.Length > 0 ? blueAssets[0] : null;
        redHitFxData = redAssets != null && redAssets.Length > 0 ? redAssets[0] : null;
    }

    private void PlayHitFeedback(Note note)
    {
        SpawnCharacterAfterimage(note.Kind);
        PlayCharacterReaction(note.Kind);
        PlayHitSound(note.Kind);
        SpawnHitFx(note.Kind, note.LaneY);
        if (note.Kind == NoteKind.GoodTap || note.Kind == NoteKind.GoodWheelUp)
        {
            SpawnCharacterMusicNotes();
        }

        TriggerCameraShake();
    }

    private void SpawnCharacterMusicNotes()
    {
        if (characterAnimation == null || musicNoteSprite == null)
        {
            return;
        }

        Vector3 center = characterAnimation.transform.position + new Vector3(0.35f, 2.9f, 0f);
        MeshRenderer characterRenderer = characterAnimation.GetComponent<MeshRenderer>();
        if (characterRenderer != null)
        {
            center = characterRenderer.bounds.center + new Vector3(0f, 0.55f, 0f);
        }

        int count = UnityEngine.Random.Range(3, 5);
        for (int i = 0; i < count; i++)
        {
            Vector3 offset = new Vector3(
                UnityEngine.Random.Range(-1.15f, 1.25f),
                UnityEngine.Random.Range(-0.25f, 1.05f),
                0f);
            StartCoroutine(FloatingMusicNoteRoutine(center + offset, i * 0.045f));
        }
    }

    private static readonly Color[] MusicNotePastelTints =
    {
        new Color(0.55f, 0.86f, 1f, 1f),
        new Color(0.74f, 0.94f, 1f, 1f),
        new Color(0.62f, 0.98f, 0.92f, 1f),
        new Color(0.97f, 0.99f, 1f, 1f)
    };

    private IEnumerator FloatingMusicNoteRoutine(Vector3 startPosition, float startDelay)
    {
        if (startDelay > 0f)
        {
            yield return new WaitForSecondsRealtime(startDelay);
        }

        Sprite sprite = UnityEngine.Random.value < 0.35f ? musicNoteDoubleSprite : musicNoteSprite;
        if (sprite == null)
        {
            yield break;
        }

        GameObject noteObject = new GameObject("Character Music Note");
        noteObject.transform.position = startPosition;
        SpriteRenderer renderer = noteObject.AddComponent<SpriteRenderer>();
        renderer.sprite = sprite;
        renderer.sortingOrder = 8;
        Color tint = MusicNotePastelTints[UnityEngine.Random.Range(0, MusicNotePastelTints.Length)];
        renderer.color = tint;

        float lifetime = UnityEngine.Random.Range(0.72f, 0.95f);
        float targetScale = UnityEngine.Random.Range(0.62f, 0.9f);
        float swayPhase = UnityEngine.Random.value * Mathf.PI * 2f;
        float swaySpeed = UnityEngine.Random.Range(5.5f, 7.5f);
        float riseDistance = UnityEngine.Random.Range(0.9f, 1.35f);
        float wobbleDegrees = UnityEngine.Random.Range(9f, 16f);

        float elapsed = 0f;
        while (elapsed < lifetime && noteObject != null)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / lifetime);

            // Pop in with a springy overshoot, then shrink slightly near the end.
            float popT = Mathf.Clamp01(elapsed / 0.16f);
            float overshoot = 1f + Mathf.Sin(popT * Mathf.PI) * 0.35f;
            float endShrink = 1f - SmoothEdge(0.75f, 1f, t) * 0.25f;
            float scale = targetScale * Mathf.SmoothStep(0f, 1f, popT) * overshoot * endShrink;
            noteObject.transform.localScale = new Vector3(scale, scale, 1f);

            float sway = Mathf.Sin(swayPhase + elapsed * swaySpeed) * 0.14f;
            noteObject.transform.position = startPosition + new Vector3(
                sway,
                Mathf.SmoothStep(0f, 1f, t) * riseDistance,
                0f);
            noteObject.transform.localRotation = Quaternion.Euler(
                0f,
                0f,
                Mathf.Sin(swayPhase + elapsed * swaySpeed * 0.8f) * wobbleDegrees);

            Color color = tint;
            color.a = 1f - SmoothEdge(0.55f, 1f, t);
            renderer.color = color;
            yield return null;
        }

        if (noteObject != null)
        {
            Destroy(noteObject);
        }
    }

    private void SpawnCharacterAfterimage(NoteKind kind)
    {
        if (characterAnimation == null)
        {
            return;
        }

        MeshFilter sourceFilter = characterAnimation.GetComponent<MeshFilter>();
        MeshRenderer sourceRenderer = characterAnimation.GetComponent<MeshRenderer>();
        if (sourceFilter == null || sourceFilter.sharedMesh == null || sourceRenderer == null)
        {
            return;
        }

        Mesh snapshotMesh = Instantiate(sourceFilter.sharedMesh);
        snapshotMesh.name = "Character Previous Frame Snapshot";

        GameObject ghost = new GameObject("Character Hit Afterimage");
        Transform sourceTransform = characterAnimation.transform;
        ghost.transform.SetParent(sourceTransform.parent, false);
        ghost.transform.localPosition = sourceTransform.localPosition;
        ghost.transform.localRotation = sourceTransform.localRotation;
        ghost.transform.localScale = sourceTransform.localScale;

        MeshFilter ghostFilter = ghost.AddComponent<MeshFilter>();
        ghostFilter.sharedMesh = snapshotMesh;
        if (characterAfterimageShader == null)
        {
            characterAfterimageShader = Resources.Load<Shader>("Shaders/RhythmSpineAfterimage");
        }

        if (characterAfterimageShader == null)
        {
            Destroy(snapshotMesh);
            Destroy(ghost);
            return;
        }

        Material[] sourceMaterials = sourceRenderer.sharedMaterials;
        Material[] ghostMaterials = new Material[sourceMaterials.Length];
        for (int i = 0; i < sourceMaterials.Length; i++)
        {
            Material sourceMaterial = sourceMaterials[i];
            Material ghostMaterial = new Material(characterAfterimageShader)
            {
                name = "Character Afterimage Material"
            };
            if (sourceMaterial != null)
            {
                ghostMaterial.mainTexture = sourceMaterial.mainTexture;
                ghostMaterial.mainTextureOffset = sourceMaterial.mainTextureOffset;
                ghostMaterial.mainTextureScale = sourceMaterial.mainTextureScale;
            }

            ghostMaterials[i] = ghostMaterial;
        }

        MeshRenderer ghostRenderer = ghost.AddComponent<MeshRenderer>();
        ghostRenderer.sharedMaterials = ghostMaterials;
        ghostRenderer.sortingLayerID = sourceRenderer.sortingLayerID;
        ConfigureSpineRenderer(ghostRenderer, sourceRenderer.sortingOrder - 1);
        ghost.SetActive(false);

        Color tint = kind == NoteKind.BadTap || kind == NoteKind.BadWheelDown
            ? new Color(1f, 0.18f, 0.42f, 1f)
            : new Color(0.12f, 0.82f, 1f, 1f);
        StartCoroutine(FadeCharacterAfterimageRoutine(ghost, snapshotMesh, ghostMaterials, tint));
    }

    private static IEnumerator FadeCharacterAfterimageRoutine(
        GameObject ghost,
        Mesh snapshotMesh,
        Material[] ghostMaterials,
        Color tint)
    {
        // The mesh was captured before the reaction animation was applied. Wait one
        // rendered frame so the original character advances, then reveal only that
        // frozen previous-frame pose as the fading afterimage.
        yield return null;
        if (ghost == null || snapshotMesh == null)
        {
            DestroyAfterimageResources(snapshotMesh, ghostMaterials);
            yield break;
        }

        ghost.SetActive(true);
        float elapsed = 0f;

        while (elapsed < CharacterAfterimageDuration && ghost != null)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / CharacterAfterimageDuration);
            float fade = (1f - Mathf.SmoothStep(0f, 1f, t)) * 0.38f;
            Color fadedTint = new Color(tint.r, tint.g, tint.b, fade);
            for (int i = 0; i < ghostMaterials.Length; i++)
            {
                if (ghostMaterials[i] != null)
                {
                    ghostMaterials[i].SetColor("_TintColor", fadedTint);
                }
            }

            yield return null;
        }

        if (ghost != null)
        {
            Destroy(ghost);
        }

        DestroyAfterimageResources(snapshotMesh, ghostMaterials);
    }

    private static void DestroyAfterimageResources(Mesh snapshotMesh, Material[] ghostMaterials)
    {
        if (snapshotMesh != null)
        {
            Destroy(snapshotMesh);
        }

        if (ghostMaterials == null)
        {
            return;
        }

        for (int i = 0; i < ghostMaterials.Length; i++)
        {
            if (ghostMaterials[i] != null)
            {
                Destroy(ghostMaterials[i]);
            }
        }
    }

    private void TriggerCameraShake()
    {
        cameraShakeUntil = Time.unscaledTime + 0.11f;
    }

    private void UpdateCameraShake()
    {
        if (gameCamera == null)
        {
            return;
        }

        float remaining = cameraShakeUntil - Time.unscaledTime;
        if (remaining <= 0f)
        {
            gameCamera.transform.position = cameraBasePosition;
            return;
        }

        float damping = Mathf.Clamp01(remaining / 0.11f);
        float phase = Time.unscaledTime * 92f;
        Vector3 offset = new Vector3(Mathf.Sin(phase * 1.37f), Mathf.Cos(phase * 1.91f), 0f) * (0.065f * damping);
        gameCamera.transform.position = cameraBasePosition + offset;
    }

    private void PlayHitSound(NoteKind kind)
    {
        if (hitSoundSource == null || hitSoundClip == null)
        {
            return;
        }

        bool red = kind == NoteKind.BadTap || kind == NoteKind.BadWheelDown;
        hitSoundSource.pitch = (red ? 0.94f : 1.06f) + UnityEngine.Random.Range(-0.025f, 0.025f);
        hitSoundSource.PlayOneShot(hitSoundClip, 0.92f);
    }

    private void PlayLongScratchStepSound(NoteKind kind, int stepIndex)
    {
        if (longScratchSoundSource == null || longScratchSoundClip == null)
        {
            return;
        }

        int step = Mathf.Clamp(stepIndex, 0, 3);
        bool red = kind == NoteKind.BadWheelDown;
        float colorPitch = red ? 0.91f : 1.07f;
        longScratchSoundSource.pitch = colorPitch + step * 0.075f + UnityEngine.Random.Range(-0.018f, 0.018f);
        float volume = step == 3 ? 0.62f : 0.34f + step * 0.075f;
        longScratchSoundSource.PlayOneShot(longScratchSoundClip, volume);
    }

    private void SpawnHitFx(NoteKind kind, float laneY)
    {
        SpawnHitFxAt(kind, new Vector3(HitX, laneY, 0f), 1.48f);
    }

    private void SpawnHitFxAt(NoteKind kind, Vector3 position, float scale)
    {
        bool red = kind == NoteKind.BadTap || kind == NoteKind.BadWheelDown;
        SkeletonDataAsset dataAsset = red ? redHitFxData : blueHitFxData;
        if (dataAsset == null)
        {
            return;
        }

        SkeletonData skeletonData = dataAsset.GetSkeletonData(true);
        string animationName = red ? "animation" : "fx";
        Spine.Animation animation = skeletonData == null ? null : skeletonData.FindAnimation(animationName);
        if (animation == null)
        {
            return;
        }

        SkeletonAnimation hitFx = SkeletonAnimation.NewSkeletonAnimationGameObject(dataAsset);
        hitFx.name = red ? "Red Note Hit FX" : "Blue Note Hit FX";
        hitFx.transform.position = position;
        hitFx.transform.localScale = Vector3.one * scale;
        hitFx.Initialize(false);

        MeshRenderer meshRenderer = hitFx.GetComponent<MeshRenderer>();
        if (meshRenderer != null)
        {
            ConfigureSpineRenderer(meshRenderer, 30);
        }

        hitFx.AnimationState.SetAnimation(0, animationName, false);
        Destroy(hitFx.gameObject, Mathf.Max(HitFxLifetime, animation.Duration + 0.06f));
    }

    private static void ConfigureSpineRenderer(MeshRenderer renderer, int sortingOrder)
    {
        renderer.sortingOrder = sortingOrder;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
        renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
        renderer.allowOcclusionWhenDynamic = false;
    }

    private void SetupAudio()
    {
        musicSource = GetComponent<AudioSource>();
        if (musicSource == null)
        {
            musicSource = gameObject.AddComponent<AudioSource>();
        }

        musicSource.playOnAwake = false;
        musicSource.loop = false;
        musicSource.spatialBlend = 0f;
        musicSource.volume = 0.72f;

        AudioSettings.GetDSPBufferSize(out int bufferLength, out int bufferCount);
        int outputRate = Mathf.Max(1, AudioSettings.outputSampleRate);
        audioVisualLatency = bufferLength * Mathf.Max(1, bufferCount - 1) / (double)outputRate;

        hitSoundSource = gameObject.AddComponent<AudioSource>();
        hitSoundSource.playOnAwake = false;
        hitSoundSource.loop = false;
        hitSoundSource.spatialBlend = 0f;
        hitSoundSource.volume = 1f;
        hitSoundClip = CreateHitSoundClip();

        longScratchSoundSource = gameObject.AddComponent<AudioSource>();
        longScratchSoundSource.playOnAwake = false;
        longScratchSoundSource.loop = false;
        longScratchSoundSource.spatialBlend = 0f;
        longScratchSoundSource.volume = 1f;
        longScratchSoundClip = CreateLongScratchSoundClip();
    }

    private static AudioClip CreateHitSoundClip()
    {
        const int sampleRate = 48000;
        const float duration = 0.115f;
        int sampleCount = Mathf.CeilToInt(sampleRate * duration);
        float[] samples = new float[sampleCount];
        var random = new System.Random(7319);

        for (int i = 0; i < sampleCount; i++)
        {
            float time = i / (float)sampleRate;
            float bodyEnvelope = Mathf.Exp(-time * 31f);
            float clickEnvelope = Mathf.Exp(-time * 92f);
            float frequency = Mathf.Lerp(175f, 82f, time / duration);
            float thump = Mathf.Sin(2f * Mathf.PI * frequency * time) * 0.72f * bodyEnvelope;
            float snap = Mathf.Sin(2f * Mathf.PI * 470f * time) * 0.18f * Mathf.Exp(-time * 48f);
            float noise = ((float)random.NextDouble() * 2f - 1f) * 0.32f * clickEnvelope;
            samples[i] = (float)Math.Tanh((thump + snap + noise) * 1.35f) * 0.88f;
        }

        AudioClip clip = AudioClip.Create("Short Punchy Note Hit", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private static AudioClip CreateLongScratchSoundClip()
    {
        const int sampleRate = 48000;
        const float duration = 0.082f;
        int sampleCount = Mathf.CeilToInt(sampleRate * duration);
        float[] samples = new float[sampleCount];
        var random = new System.Random(42817);
        float heldNoise = 0f;

        for (int i = 0; i < sampleCount; i++)
        {
            float time = i / (float)sampleRate;
            float normalizedTime = time / duration;
            float envelope = Mathf.Exp(-time * 37f) * Mathf.SmoothStep(0f, 1f, Mathf.Min(1f, time * 900f));
            if ((i & 7) == 0)
            {
                heldNoise = (float)random.NextDouble() * 2f - 1f;
            }

            float grit = heldNoise * 0.44f;
            float scrapeFrequency = Mathf.Lerp(1380f, 410f, normalizedTime);
            float scrape = Mathf.Sin(2f * Mathf.PI * scrapeFrequency * time + Mathf.Sin(time * 760f) * 1.6f) * 0.34f;
            float crack = Mathf.Sin(2f * Mathf.PI * 2250f * time) * Mathf.Exp(-time * 105f) * 0.28f;
            samples[i] = (float)Math.Tanh((grit + scrape + crack) * envelope * 1.55f) * 0.72f;
        }

        AudioClip clip = AudioClip.Create("Long Note Scratch Step", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private void LoadLocalSongs()
    {
        localSongs.Clear();

        AudioClip[] resourceClips = Resources.LoadAll<AudioClip>(MusicResourcesPath);
        for (int i = 0; i < resourceClips.Length; i++)
        {
            AudioClip clip = resourceClips[i];
            if (clip != null)
            {
                AddLocalSong(clip.name, string.Empty, clip);
            }
        }

        string musicFolder = Path.Combine(Application.dataPath, "Resources", MusicResourcesPath);
        if (Directory.Exists(musicFolder))
        {
            string[] musicFiles = Directory.GetFiles(musicFolder, "*.mp3", SearchOption.TopDirectoryOnly);
            for (int i = 0; i < musicFiles.Length; i++)
            {
                string filePath = musicFiles[i];
                AddLocalSong(Path.GetFileNameWithoutExtension(filePath), filePath, null);
            }
        }

        localSongs.Sort((left, right) =>
            string.Compare(
                left == null ? string.Empty : left.Name,
                right == null ? string.Empty : right.Name,
                StringComparison.CurrentCultureIgnoreCase));

        selectedLocalSongIndex = -1;
        generatedSongLabel = localSongs.Count > 0 ? "SELECT SONG" : "NO LOCAL SONG";
        generatedSongProvider = "LOCAL";
        generatedSongWarning = string.Empty;
        murekaStatus = localSongs.Count > 0
            ? "Select a local song to play."
            : "No local songs found. Add MP3 files to Assets/Resources/Music.";
    }

    private void ShowLoadingScreen()
    {
        if (loadingScreenRoot == null)
        {
            BuildLoadingScreen();
        }

        if (loadingScreenRoot == null)
        {
            return;
        }

        loadingScreenShownAt = Time.realtimeSinceStartup;
        isLoadingScreenVisible = true;
        if (loadingScreenFadeRoutine != null)
        {
            StopCoroutine(loadingScreenFadeRoutine);
            loadingScreenFadeRoutine = null;
        }

        loadingScreenRoot.SetActive(true);
        if (loadingScreenCharacter != null && !string.IsNullOrEmpty(loadingScreenAnimationName))
        {
            loadingScreenCharacter.AnimationState.SetAnimation(0, loadingScreenAnimationName, true);
            StartCoroutine(AlignLoadingCharacterRoutine());
        }

        loadingScreenFadeRoutine = StartCoroutine(FadeLoadingScreenRoutine(0f, 1f, 0.3f, false));
    }

    private void HideLoadingScreenSmoothly()
    {
        if (loadingScreenRoot == null || !loadingScreenRoot.activeSelf)
        {
            HideLoadingScreen();
            return;
        }

        // The HUD returns immediately while the dark overlay fades away on top of it.
        isLoadingScreenVisible = false;
        if (loadingScreenFadeRoutine != null)
        {
            StopCoroutine(loadingScreenFadeRoutine);
        }

        loadingScreenFadeRoutine = StartCoroutine(FadeLoadingScreenRoutine(1f, 0f, 0.75f, true));
    }

    private IEnumerator FadeLoadingScreenRoutine(float fromAlpha, float toAlpha, float duration, bool deactivateAtEnd)
    {
        Vector3 characterStart = loadingScreenCharacter != null
            ? loadingScreenCharacter.transform.position
            : Vector3.zero;
        SetLoadingScreenAlpha(fromAlpha);

        float elapsed = 0f;
        while (elapsed < duration && loadingScreenRoot != null)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float eased = t * t * (3f - 2f * t);
            SetLoadingScreenAlpha(Mathf.Lerp(fromAlpha, toAlpha, eased));
            if (deactivateAtEnd && loadingScreenCharacter != null)
            {
                // The character slips away toward the lower-right as the overlay clears.
                loadingScreenCharacter.transform.position =
                    characterStart + new Vector3(1.7f, -1.2f, 0f) * eased;
            }

            yield return null;
        }

        loadingScreenFadeRoutine = null;
        if (deactivateAtEnd)
        {
            HideLoadingScreen();
            if (loadingScreenCharacter != null)
            {
                loadingScreenCharacter.transform.position = characterStart;
            }

            SetLoadingScreenAlpha(1f);
        }
    }

    private void SetLoadingScreenAlpha(float alpha)
    {
        if (loadingScreenRoot == null)
        {
            return;
        }

        alpha = Mathf.Clamp01(alpha);
        SpriteRenderer[] sprites = loadingScreenRoot.GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < sprites.Length; i++)
        {
            Color color = sprites[i].color;
            color.a = alpha;
            sprites[i].color = color;
        }

        if (loadingScreenCharacter != null && loadingScreenCharacter.Skeleton != null)
        {
            loadingScreenCharacter.Skeleton.A = alpha;
        }
    }

    private IEnumerator AlignLoadingCharacterRoutine()
    {
        // Spine rebuilds the mesh in LateUpdate, so real bounds exist one frame later.
        yield return null;
        if (!isLoadingScreenVisible || loadingScreenCharacter == null)
        {
            yield break;
        }

        MeshRenderer renderer = loadingScreenCharacter.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            yield break;
        }

        Bounds bounds = renderer.bounds;
        if (bounds.size.sqrMagnitude < 0.0001f)
        {
            yield break;
        }

        float halfHeight = gameCamera != null ? gameCamera.orthographicSize : 5f;
        float halfWidth = halfHeight * (gameCamera != null ? gameCamera.aspect : 16f / 9f);
        Vector3 viewCenter = gameCamera != null ? gameCamera.transform.position : Vector3.zero;
        Vector3 target = new Vector3(
            viewCenter.x + halfWidth - bounds.extents.x + 0.25f,
            viewCenter.y - halfHeight + bounds.extents.y + 0.3f,
            0f);
        Vector3 delta = target - bounds.center;
        delta.z = 0f;
        loadingScreenCharacter.transform.position += delta;
    }

    private void HideLoadingScreen()
    {
        isLoadingScreenVisible = false;
        if (loadingScreenRoot != null)
        {
            loadingScreenRoot.SetActive(false);
        }
    }

    private IEnumerator WaitForMinimumLoadingScreen()
    {
        if (!isLoadingScreenVisible)
        {
            yield break;
        }

        while (Time.realtimeSinceStartup - loadingScreenShownAt < MinimumLoadingScreenDuration)
        {
            yield return null;
        }
    }

    private void BuildLoadingScreen()
    {
        EnsureSharedAssets();
        if (loadingGradientSprite == null)
        {
            return;
        }

        float halfHeight = gameCamera != null ? gameCamera.orthographicSize : 5f;
        float halfWidth = halfHeight * (gameCamera != null ? gameCamera.aspect : 16f / 9f);

        loadingScreenRoot = new GameObject("Loading Screen");
        if (gameCamera != null)
        {
            // Parented to the camera so shake offsets never reveal the stage behind.
            loadingScreenRoot.transform.SetParent(gameCamera.transform, false);
            loadingScreenRoot.transform.localPosition = new Vector3(0f, 0f, -gameCamera.transform.position.z);
        }

        GameObject gradient = new GameObject("Loading Gradient");
        gradient.transform.SetParent(loadingScreenRoot.transform, false);
        SpriteRenderer gradientRenderer = gradient.AddComponent<SpriteRenderer>();
        gradientRenderer.sprite = loadingGradientSprite;
        gradientRenderer.sortingOrder = 200;
        gradient.transform.localScale = new Vector3(halfWidth * 2.3f / 4f, halfHeight * 2.2f / 256f, 1f);

        SkeletonDataAsset[] dataAssets = Resources.LoadAll<SkeletonDataAsset>("Spine/Character03");
        if (dataAssets != null && dataAssets.Length > 0)
        {
            SkeletonDataAsset dataAsset = dataAssets[0];
            SkeletonData skeletonData = dataAsset.GetSkeletonData(true);
            if (skeletonData != null)
            {
                loadingScreenCharacter = SkeletonAnimation.NewSkeletonAnimationGameObject(dataAsset);
                loadingScreenCharacter.name = "Loading Screen Character";
                loadingScreenCharacter.transform.SetParent(loadingScreenRoot.transform, false);
                loadingScreenCharacter.Initialize(false);

                // The character skeleton keeps its body attachments in the SkinN skins,
                // so without an explicit skin nothing is rendered.
                Skeleton loadingSkeleton = loadingScreenCharacter.Skeleton;
                if (loadingSkeleton != null)
                {
                    Skin loadingSkin = loadingSkeleton.Data.FindSkin("Skin70");
                    if (loadingSkin == null)
                    {
                        loadingSkin = loadingSkeleton.Data.FindSkin("Skin0");
                    }

                    if (loadingSkin != null)
                    {
                        loadingSkeleton.SetSkin(loadingSkin);
                        loadingSkeleton.SetSlotsToSetupPose();
                    }
                }

                float dataScale = Mathf.Max(0.0001f, dataAsset.scale);
                float importedHeight = skeletonData.Height * dataScale;
                float importedCenterX = (skeletonData.X + skeletonData.Width * 0.5f) * dataScale;
                float importedCenterY = (skeletonData.Y + skeletonData.Height * 0.5f) * dataScale;
                float characterScale = importedHeight > 0.01f ? 3.3f / importedHeight : 0.12f;
                loadingScreenCharacter.transform.localScale = Vector3.one * characterScale;
                // Place the setup-pose bounds center near the lower-right corner of the view.
                loadingScreenCharacter.transform.localPosition = new Vector3(
                    halfWidth - 0.7f - importedCenterX * characterScale,
                    -halfHeight + 2.4f - importedCenterY * characterScale,
                    0f);

                MeshRenderer characterRenderer = loadingScreenCharacter.GetComponent<MeshRenderer>();
                if (characterRenderer != null)
                {
                    ConfigureSpineRenderer(characterRenderer, 210);
                }

                loadingScreenAnimationName = FindLoadingAnimationName(skeletonData);
            }
        }

        loadingScreenRoot.SetActive(false);
    }

    private static string FindLoadingAnimationName(SkeletonData skeletonData)
    {
        foreach (Spine.Animation animation in skeletonData.Animations)
        {
            if (animation.Name.IndexOf("load", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return animation.Name;
            }
        }

        if (skeletonData.FindAnimation("Walk") != null)
        {
            return "Walk";
        }

        return skeletonData.FindAnimation("Idle") != null ? "Idle" : null;
    }

    private void PlayLocalSong(int index)
    {
        if (isLoadingLocalSong)
        {
            return;
        }

        if (index < 0 || index >= localSongs.Count || localSongs[index] == null)
        {
            murekaStatus = "Selected local song could not be loaded.";
            return;
        }

        StartCoroutine(PlayLocalSongRoutine(index));
    }

    private IEnumerator PlayLocalSongRoutine(int index)
    {
        isLoadingLocalSong = true;
        ShowLoadingScreen();
        LocalSongEntry song = localSongs[index];
        AudioClip clip = song.ResourceClip != null ? song.ResourceClip : song.LoadedClip;

        if (clip == null)
        {
            if (string.IsNullOrWhiteSpace(song.FilePath) || !File.Exists(song.FilePath))
            {
                murekaStatus = "Local song file was not found: " + song.Name;
                isLoadingLocalSong = false;
                HideLoadingScreen();
                yield break;
            }

            murekaStatus = "Loading local song: " + song.Name;
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(song.FilePath).AbsoluteUri, AudioType.MPEG))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    murekaStatus = "Local song load failed: " + request.error;
                    isLoadingLocalSong = false;
                    HideLoadingScreen();
                    yield break;
                }

                clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    murekaStatus = "Local song could not be decoded by Unity.";
                    isLoadingLocalSong = false;
                    HideLoadingScreen();
                    yield break;
                }

                clip.name = song.Name;
                song.LoadedClip = clip;
            }
        }

        ClearCurrentSong();
        if (clip.loadState != AudioDataLoadState.Loaded)
        {
            murekaStatus = "Preparing audio data: " + song.Name;
            clip.LoadAudioData();
            while (clip.loadState == AudioDataLoadState.Loading)
            {
                yield return null;
            }

            if (clip.loadState == AudioDataLoadState.Failed)
            {
                murekaStatus = "Audio data could not be prepared: " + song.Name;
                isLoadingLocalSong = false;
                HideLoadingScreen();
                yield break;
            }
        }

        SongAnalysis analysis = null;
        string analysisKey = GetAnalysisCacheKey(clip);
        if (!songAnalysisCache.TryGetValue(analysisKey, out analysis))
        {
            murekaStatus = "Analyzing waveform, BPM and beats: " + song.Name;
            yield return AnalyzeSongRoutine(clip, value => analysis = value);
            if (analysis != null)
            {
                songAnalysisCache[analysisKey] = analysis;
            }
        }

        currentSongClip = clip;
        selectedLocalSongIndex = index;
        generatedSongSeed = CreateStableSeed(song.Name);
        generatedSongLabel = song.Name;
        generatedSongProvider = "LOCAL";
        generatedSongWarning = string.Empty;
        generatedSongLength = Mathf.Max(4f, clip.length);
        currentSongAnalysis = analysis;

        if (analysis != null)
        {
            generatedBpm = analysis.Bpm;
            BuildChartFromAnalysis(analysis, CreateChartRandom());
        }
        else
        {
            generatedBpm = 128f;
            BuildChartForMurekaSong(CreateChartRandom());
            generatedSongWarning = "Beat analysis failed; using 128 BPM fallback.";
        }

        yield return WaitForMinimumLoadingScreen();
        RestartChart();
        songSelectionVisible = false;
        isLoadingLocalSong = false;
        HideLoadingScreenSmoothly();
        murekaStatus = "Playing " + song.Name + " — " + GetDifficultyPreset().Label + " — detected " + generatedBpm.ToString("0.0") + " BPM";
    }

    private void AddLocalSong(string songName, string filePath, AudioClip resourceClip)
    {
        if (string.IsNullOrWhiteSpace(songName))
        {
            return;
        }

        for (int i = 0; i < localSongs.Count; i++)
        {
            if (string.Equals(localSongs[i].Name, songName, StringComparison.CurrentCultureIgnoreCase))
            {
                if (localSongs[i].ResourceClip == null && resourceClip != null)
                {
                    localSongs[i].ResourceClip = resourceClip;
                }

                if (string.IsNullOrWhiteSpace(localSongs[i].FilePath) && !string.IsNullOrWhiteSpace(filePath))
                {
                    localSongs[i].FilePath = filePath;
                }

                return;
            }
        }

        localSongs.Add(new LocalSongEntry
        {
            Name = songName,
            FilePath = filePath,
            ResourceClip = resourceClip
        });
    }

    private void SetDifficulty(RhythmDifficulty difficulty)
    {
        if (selectedDifficulty == difficulty)
        {
            return;
        }

        selectedDifficulty = difficulty;
        if (currentSongClip != null && !songSelectionVisible && !isLoadingLocalSong && !isRequestingMurekaSong)
        {
            RebuildCurrentChartForDifficulty();
            murekaStatus = "Difficulty changed to " + GetDifficultyPreset().Label + ". Restarting current song.";
        }
        else
        {
            murekaStatus = "Difficulty set to " + GetDifficultyPreset().Label + ".";
        }
    }

    private void RebuildCurrentChartForDifficulty()
    {
        if (currentSongAnalysis != null)
        {
            BuildChartFromAnalysis(currentSongAnalysis, CreateChartRandom());
        }
        else
        {
            BuildChartForMurekaSong(CreateChartRandom());
        }

        RestartChart();
    }

    private System.Random CreateChartRandom()
    {
        unchecked
        {
            int seed = generatedSongSeed ^ (((int)selectedDifficulty + 1) * 7919);
            return new System.Random(seed == int.MinValue ? int.MaxValue : Mathf.Abs(seed));
        }
    }

    private DifficultyPreset GetDifficultyPreset()
    {
        return GetDifficultyPreset(selectedDifficulty);
    }

    private static DifficultyPreset GetDifficultyPreset(RhythmDifficulty difficulty)
    {
        switch (difficulty)
        {
            case RhythmDifficulty.Easy:
                return EasyDifficulty;
            case RhythmDifficulty.Hard:
                return HardDifficulty;
            default:
                return NormalDifficulty;
        }
    }

    private void GenerateNewSong()
    {
        if (isRequestingMurekaSong)
        {
            murekaStatus = "MUREKA request is already running.";
            return;
        }

        if (string.IsNullOrWhiteSpace(GetCurrentMurekaPrompt()))
        {
            murekaStatus = "Write a MUREKA prompt first.";
            return;
        }

        StartCoroutine(RequestMurekaSong());
    }

    private void StartMurekaBackendOnly()
    {
        if (isStartingMurekaBackend || isRequestingMurekaSong)
        {
            return;
        }

        StartCoroutine(StartMurekaBackendRoutine());
    }

    private IEnumerator StartMurekaBackendRoutine()
    {
        isStartingMurekaBackend = true;
        murekaStatus = "Starting project MUREKA backend...";
        bool backendReady = false;
        yield return EnsureMurekaBackendReady(value => backendReady = value);
        murekaStatus = backendReady
            ? "MUREKA website backend ready."
            : "Project backend did not start. Check My project (1)/MurekaBackend/start_mureka_backend.ps1.";
        isStartingMurekaBackend = false;
    }

    private IEnumerator RequestMurekaSong()
    {
        isRequestingMurekaSong = true;
        ClearCurrentSong();
        string prompt = GetCurrentMurekaPrompt();

        generatedSongSeed = UnityEngine.Random.Range(10000, 999999);
        generatedSongLabel = "MUREKA GENERATING";
        generatedSongProvider = MurekaProvider;
        generatedSongWarning = "";
        generatedBpm = 128f;
        murekaStatus = "Checking MUREKA website backend...";

        bool backendReady = false;
        yield return EnsureMurekaBackendReady(value => backendReady = value);
        if (!backendReady)
        {
            murekaStatus = "Project backend is not ready. Press START BACKEND, then GENERATE.";
            isRequestingMurekaSong = false;
            yield break;
        }

        murekaStatus = "Creating song on the MUREKA website...";
        var requestBody = JsonUtility.ToJson(new MurekaGenerateRequest
        {
            prompt = prompt,
            provider = MurekaProvider
        });

        using (UnityWebRequest request = new UnityWebRequest(MurekaBackendUrl + "/generate", "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(requestBody);
            request.uploadHandler = new UploadHandlerRaw(body);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            request.timeout = 900;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                string backendMessage = request.downloadHandler == null ? "" : request.downloadHandler.text;
                murekaStatus = string.IsNullOrWhiteSpace(backendMessage)
                    ? "MUREKA generation failed: " + request.error
                    : "MUREKA generation failed: " + backendMessage;
                isRequestingMurekaSong = false;
                yield break;
            }

            MurekaGenerateResponse response = JsonUtility.FromJson<MurekaGenerateResponse>(request.downloadHandler.text);
            if (response == null || string.IsNullOrWhiteSpace(response.audioUrl))
            {
                murekaStatus = "MUREKA response did not include an audio URL.";
                isRequestingMurekaSong = false;
                yield break;
            }

            generatedSongLabel = string.IsNullOrWhiteSpace(response.title)
                ? "MUREKA SONG " + (generatedSongSeed % 1000).ToString("000")
                : response.title;
            generatedSongProvider = string.IsNullOrWhiteSpace(response.provider) ? MurekaProvider : response.provider;
            generatedSongWarning = response.warning;
            generatedBpm = response.bpm > 1f ? Mathf.Clamp(response.bpm, 80f, 180f) : 128f;

            yield return DownloadMurekaAudio(response.audioUrl);
        }

        isRequestingMurekaSong = false;
    }

    private void ClearCurrentSong()
    {
        if (musicSource != null)
        {
            musicSource.Stop();
            musicSource.clip = null;
        }

        currentSongClip = null;
        currentSongAnalysis = null;
        selectedLocalSongIndex = -1;

        if (generatedSongClip != null)
        {
            Destroy(generatedSongClip);
            generatedSongClip = null;
        }

        for (int i = 0; i < notes.Count; i++)
        {
            ClearNote(notes[i]);
        }

        notes.Clear();
        chart.Clear();
        chartDuration = 0f;
        chartFinished = false;
        judgementKind = JudgementKind.None;
        judgementVisibleUntil = 0f;
    }

    private string GetCurrentMurekaPrompt()
    {
        string prompt = string.IsNullOrWhiteSpace(murekaPrompt) ? DefaultMurekaPrompt : murekaPrompt;
        return prompt.Trim();
    }

    private IEnumerator EnsureMurekaBackendReady(Action<bool> onDone)
    {
        bool ready = false;
        yield return CheckMurekaBackendHealth(value => ready = value);
        if (ready)
        {
            onDone(true);
            yield break;
        }

        if (!LaunchMurekaBackend())
        {
            onDone(false);
            yield break;
        }

        murekaStatus = "Starting MUREKA website backend...";
        float deadline = Time.realtimeSinceStartup + MurekaBackendStartupTimeout;
        while (Time.realtimeSinceStartup < deadline)
        {
            yield return new WaitForSeconds(2f);
            yield return CheckMurekaBackendHealth(value => ready = value);
            if (ready)
            {
                onDone(true);
                yield break;
            }
        }

        onDone(false);
    }

    private IEnumerator CheckMurekaBackendHealth(Action<bool> onDone)
    {
        using (UnityWebRequest request = UnityWebRequest.Get(MurekaBackendUrl + "/health"))
        {
            request.timeout = 2;
            yield return request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                onDone(false);
                yield break;
            }

            bool ready = false;
            try
            {
                MurekaHealthResponse health = JsonUtility.FromJson<MurekaHealthResponse>(request.downloadHandler.text);
                ready = health != null && health.ok && health.pythonReady;
            }
            catch (ArgumentException)
            {
                ready = false;
            }

            if (!ready)
            {
                murekaStatus = "MUREKA backend needs setup. Run MurekaBackend/setup_mureka_backend.ps1.";
            }

            onDone(ready);
        }
    }

    private bool LaunchMurekaBackend()
    {
        if (Time.realtimeSinceStartup - lastMurekaBackendStartAttempt < 10f)
        {
            return true;
        }

        lastMurekaBackendStartAttempt = Time.realtimeSinceStartup;

        try
        {
            string scriptPath = GetMurekaBackendScriptPath();
            if (!File.Exists(scriptPath))
            {
                murekaStatus = "Project backend script was not found: " + scriptPath;
                return false;
            }

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\"",
                WorkingDirectory = Path.GetDirectoryName(scriptPath),
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Minimized
            };
            System.Diagnostics.Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            murekaStatus = "Failed to start MUREKA bridge: " + ex.Message;
            return false;
        }
    }

    private static string GetMurekaBackendScriptPath()
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        return Path.Combine(projectRoot, MurekaBackendFolderName, MurekaBackendScriptName);
    }

    private IEnumerator DownloadMurekaAudio(string audioUrl)
    {
        murekaStatus = "Downloading MUREKA audio...";
        ShowLoadingScreen();
        using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(audioUrl, GuessAudioType(audioUrl)))
        {
            request.timeout = 90;
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                murekaStatus = "MUREKA audio download failed: " + request.error;
                HideLoadingScreen();
                yield break;
            }

            generatedSongClip = DownloadHandlerAudioClip.GetContent(request);
            if (generatedSongClip == null)
            {
                murekaStatus = "MUREKA audio could not be decoded by Unity.";
                HideLoadingScreen();
                yield break;
            }

            generatedSongClip.name = generatedSongLabel;
        }

        currentSongClip = generatedSongClip;
        generatedSongLength = Mathf.Max(4f, generatedSongClip.length);
        if (generatedSongClip.loadState != AudioDataLoadState.Loaded)
        {
            generatedSongClip.LoadAudioData();
            while (generatedSongClip.loadState == AudioDataLoadState.Loading)
            {
                yield return null;
            }
        }

        SongAnalysis analysis = null;
        if (generatedSongClip.loadState == AudioDataLoadState.Loaded)
        {
            murekaStatus = "Analyzing generated song beats...";
            yield return AnalyzeSongRoutine(generatedSongClip, value => analysis = value);
        }

        if (analysis != null)
        {
            generatedBpm = analysis.Bpm;
            currentSongAnalysis = analysis;
            BuildChartFromAnalysis(analysis, CreateChartRandom());
        }
        else
        {
            currentSongAnalysis = null;
            BuildChartForMurekaSong(CreateChartRandom());
        }

        yield return WaitForMinimumLoadingScreen();
        RestartChart();
        songSelectionVisible = false;
        HideLoadingScreenSmoothly();
        murekaStatus = string.IsNullOrWhiteSpace(generatedSongWarning)
            ? "MUREKA song ready."
            : "MUREKA song ready. " + generatedSongWarning;
    }

    private static AudioType GuessAudioType(string url)
    {
        string path = url.Split('?')[0].ToLowerInvariant();
        if (path.EndsWith(".mp3")) return AudioType.MPEG;
        if (path.EndsWith(".ogg")) return AudioType.OGGVORBIS;
        if (path.EndsWith(".wav")) return AudioType.WAV;
        return AudioType.MPEG;
    }

    private static int CreateStableSeed(string value)
    {
        unchecked
        {
            int hash = 17;
            for (int i = 0; i < value.Length; i++)
            {
                hash = hash * 31 + value[i];
            }

            return hash == int.MinValue ? int.MaxValue : Mathf.Abs(hash);
        }
    }

    private static string GetAnalysisCacheKey(AudioClip clip)
    {
        return clip.name + "|" + clip.samples + "|" + clip.frequency + "|" + clip.channels;
    }

    private IEnumerator AnalyzeSongRoutine(AudioClip clip, Action<SongAnalysis> onDone)
    {
        if (clip == null || clip.samples <= 0 || clip.frequency <= 0 || clip.channels <= 0)
        {
            onDone(null);
            yield break;
        }

        int hopFrames = Mathf.Max(128, Mathf.RoundToInt(clip.frequency / AnalysisTargetRate));
        int totalHops = Mathf.Max(1, clip.samples / hopFrames);
        int channels = clip.channels;
        float envelopeRate = clip.frequency / (float)hopFrames;
        float[] energy = new float[totalHops];
        float[] transientEnergy = new float[totalHops];
        const int hopsPerBatch = 128;

        for (int startHop = 0; startHop < totalHops; startHop += hopsPerBatch)
        {
            int batchHops = Mathf.Min(hopsPerBatch, totalHops - startHop);
            int batchFrames = batchHops * hopFrames;
            float[] samples = new float[batchFrames * channels];
            if (!clip.GetData(samples, startHop * hopFrames))
            {
                onDone(null);
                yield break;
            }

            for (int localHop = 0; localHop < batchHops; localHop++)
            {
                int sampleStart = localHop * hopFrames * channels;
                double sumSquares = 0.0;
                double differenceSum = 0.0;
                float previousMono = 0f;
                for (int frame = 0; frame < hopFrames; frame++)
                {
                    int frameStart = sampleStart + frame * channels;
                    float mono = 0f;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        mono += samples[frameStart + channel];
                    }

                    mono /= channels;
                    sumSquares += mono * mono;
                    if (frame > 0)
                    {
                        differenceSum += Mathf.Abs(mono - previousMono);
                    }

                    previousMono = mono;
                }

                energy[startHop + localHop] = Mathf.Sqrt((float)(sumSquares / hopFrames));
                transientEnergy[startHop + localHop] = (float)(differenceSum / Mathf.Max(1, hopFrames - 1));
            }

            yield return null;
        }

        float[] normalizedEnergy = NormalizeEnvelope(energy, 0.92f);
        float[] normalizedTransient = NormalizeEnvelope(transientEnergy, 0.92f);

        float[] onset = new float[energy.Length];
        float maximumOnset = 0f;
        const int meanWindow = 8;
        for (int i = 1; i < energy.Length; i++)
        {
            int first = Mathf.Max(0, i - meanWindow);
            float energyMean = 0f;
            float transientMean = 0f;
            for (int j = first; j < i; j++)
            {
                energyMean += normalizedEnergy[j];
                transientMean += normalizedTransient[j];
            }

            energyMean /= Mathf.Max(1, i - first);
            transientMean /= Mathf.Max(1, i - first);
            float energyFlux = Mathf.Max(0f, normalizedEnergy[i] - energyMean * 0.92f);
            float transientFlux = Mathf.Max(0f, normalizedTransient[i] - transientMean * 0.9f);
            float positiveFlux = energyFlux * 0.68f + transientFlux * 0.32f;
            onset[i] = positiveFlux;
            maximumOnset = Mathf.Max(maximumOnset, positiveFlux);
        }

        if (maximumOnset <= 0.000001f)
        {
            onDone(null);
            yield break;
        }

        onset = NormalizeEnvelope(onset, 0.94f);

        int minimumLag = Mathf.Max(2, Mathf.RoundToInt(envelopeRate * 60f / MaximumAnalyzedBpm));
        int maximumLag = Mathf.Min(onset.Length / 3, Mathf.RoundToInt(envelopeRate * 60f / MinimumAnalyzedBpm));
        int bestLag = minimumLag;
        float bestScore = float.MinValue;
        float[] lagScores = new float[Mathf.Max(maximumLag + 2, minimumLag + 2)];

        for (int lag = minimumLag; lag <= maximumLag; lag++)
        {
            float score = NormalizedOnsetCorrelation(onset, lag);
            if (lag * 2 < onset.Length / 2)
            {
                score += NormalizedOnsetCorrelation(onset, lag * 2) * 0.28f;
            }

            float candidateBpm = 60f * envelopeRate / lag;
            score *= Mathf.Lerp(0.96f, 1.04f, 1f - Mathf.Clamp01(Mathf.Abs(candidateBpm - 124f) / 60f));
            lagScores[lag] = score;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        float refinedLag = bestLag;
        if (bestLag > minimumLag && bestLag < maximumLag)
        {
            float previous = lagScores[bestLag - 1];
            float center = lagScores[bestLag];
            float next = lagScores[bestLag + 1];
            float denominator = previous - 2f * center + next;
            if (Mathf.Abs(denominator) > 0.000001f)
            {
                float offset = 0.5f * (previous - next) / denominator;
                refinedLag += Mathf.Clamp(offset, -0.5f, 0.5f);
            }
        }

        float bestPhase = FindBestBeatPhase(onset, refinedLag);

        SongAnalysis analysis = new SongAnalysis
        {
            Bpm = Mathf.Clamp(60f * envelopeRate / refinedLag, MinimumAnalyzedBpm, MaximumAnalyzedBpm),
            BeatOffset = bestPhase / envelopeRate,
            BeatDuration = refinedLag / envelopeRate,
            EnvelopeRate = envelopeRate,
            OnsetStrength = onset,
            EnergyEnvelope = normalizedEnergy
        };

        for (float time = analysis.BeatOffset; time < clip.length; time += analysis.BeatDuration)
        {
            analysis.BeatTimes.Add(time);
        }

        onDone(analysis);
    }

    private static float[] NormalizeEnvelope(float[] source, float percentile)
    {
        float[] sorted = (float[])source.Clone();
        Array.Sort(sorted);
        int percentileIndex = Mathf.Clamp(Mathf.RoundToInt((sorted.Length - 1) * percentile), 0, sorted.Length - 1);
        float reference = Mathf.Max(0.000001f, sorted[percentileIndex]);
        float[] normalized = new float[source.Length];
        for (int i = 0; i < source.Length; i++)
        {
            normalized[i] = Mathf.Clamp01(source[i] / reference);
        }

        return normalized;
    }

    private static float FindBestBeatPhase(float[] onset, float period)
    {
        float bestPhase = 0f;
        float bestScore = float.MinValue;
        const float phaseStep = 0.25f;
        for (float phase = 0f; phase < period; phase += phaseStep)
        {
            float score = 0f;
            int beatCount = 0;
            for (float position = phase; position < onset.Length; position += period)
            {
                float strength = SampleEnvelopeLinear(onset, position);
                score += strength * 0.35f + strength * strength * 0.65f;
                beatCount++;
            }

            score /= Mathf.Max(1, beatCount);
            if (score > bestScore)
            {
                bestScore = score;
                bestPhase = phase;
            }
        }

        return bestPhase;
    }

    private static float SampleEnvelopeLinear(float[] envelope, float position)
    {
        int first = Mathf.Clamp(Mathf.FloorToInt(position), 0, envelope.Length - 1);
        int second = Mathf.Min(first + 1, envelope.Length - 1);
        return Mathf.Lerp(envelope[first], envelope[second], position - first);
    }

    private static float NormalizedOnsetCorrelation(float[] onset, int lag)
    {
        double product = 0.0;
        double leftEnergy = 0.0;
        double rightEnergy = 0.0;
        for (int i = lag; i < onset.Length; i++)
        {
            float left = onset[i];
            float right = onset[i - lag];
            product += left * right;
            leftEnergy += left * left;
            rightEnergy += right * right;
        }

        double denominator = Math.Sqrt(leftEnergy * rightEnergy);
        return denominator > 0.0000001 ? (float)(product / denominator) : 0f;
    }

    private void BuildChartFromAnalysis(SongAnalysis analysis, System.Random rng)
    {
        chart.Clear();
        DifficultyPreset difficulty = GetDifficultyPreset();
        int laneCursor = 1;
        int playableBeatIndex = 0;
        bool lastIsGood = true;
        float firstPlayableTime = Mathf.Max(0.85f, analysis.BeatOffset);
        float finalPlayableTime = Mathf.Max(firstPlayableTime, generatedSongLength - 1.1f);

        for (int i = 0; i < analysis.BeatTimes.Count; i++)
        {
            float beatTime = analysis.BeatTimes[i];
            if (beatTime < firstPlayableTime || beatTime > finalPlayableTime)
            {
                continue;
            }

            int beatInBar = playableBeatIndex % BeatsPerBar;
            bool strongBeat = beatInBar == 0 || beatInBar == 2;
            float searchRadius = Mathf.Min(difficulty.PeakSearchRadius, analysis.BeatDuration * 0.3f);
            float peakTime = FindEnvelopePeakTime(analysis.OnsetStrength, analysis.EnvelopeRate, beatTime, searchRadius);
            float noteTime = Mathf.Lerp(beatTime, peakTime, difficulty.PeakTimeInfluence);
            float onsetStrength = SampleEnvelope(analysis.OnsetStrength, analysis.EnvelopeRate, peakTime);
            float energyStrength = SampleEnvelope(analysis.EnergyEnvelope, analysis.EnvelopeRate, peakTime);
            float noteChance = strongBeat
                ? difficulty.AnalysisStrongBase + onsetStrength * difficulty.AnalysisStrongOnset + energyStrength * difficulty.AnalysisStrongEnergy
                : difficulty.AnalysisWeakBase + onsetStrength * difficulty.AnalysisWeakOnset + energyStrength * difficulty.AnalysisWeakEnergy;
            if (onsetStrength >= difficulty.AnalysisHighOnsetThreshold)
            {
                noteChance = Mathf.Max(noteChance, difficulty.AnalysisHighOnsetMinChance);
            }

            if (rng.NextDouble() <= Mathf.Clamp01(noteChance))
            {
                laneCursor = ChooseNextLaneIndex(laneCursor, rng, difficulty);
                bool isBlue = PickGoodNote(rng, difficulty, ref lastIsGood);
                float sustainedEnergy = AverageEnvelope(
                    analysis.EnergyEnvelope,
                    analysis.EnvelopeRate,
                    noteTime,
                    noteTime + analysis.BeatDuration * 1.5f);
                bool longNoteSlot = strongBeat || onsetStrength >= 0.82f;
                bool isLong = longNoteSlot
                    && sustainedEnergy >= difficulty.LongEnergyThreshold
                    && rng.NextDouble() <= difficulty.LongNoteChance;
                NoteKind kind = isLong
                    ? (isBlue ? NoteKind.GoodWheelUp : NoteKind.BadWheelDown)
                    : (isBlue ? NoteKind.GoodTap : NoteKind.BadTap);

                TryAddGeneratedNote(noteTime, kind, laneCursor);
            }

            if (difficulty.AnalysisExtraNoteChance > 0f && beatInBar < BeatsPerBar - 1)
            {
                float extraCenterTime = beatTime + analysis.BeatDuration * 0.5f;
                if (extraCenterTime <= finalPlayableTime)
                {
                    float extraPeakTime = FindEnvelopePeakTime(analysis.OnsetStrength, analysis.EnvelopeRate, extraCenterTime, searchRadius);
                    float extraTime = Mathf.Lerp(extraCenterTime, extraPeakTime, difficulty.PeakTimeInfluence);
                    float extraOnset = SampleEnvelope(analysis.OnsetStrength, analysis.EnvelopeRate, extraPeakTime);
                    float extraChance = difficulty.AnalysisExtraNoteChance * Mathf.Clamp01(0.55f + extraOnset * 0.9f);
                    if (rng.NextDouble() <= extraChance)
                    {
                        laneCursor = ChooseNextLaneIndex(laneCursor, rng, difficulty);
                        bool isBlue = PickGoodNote(rng, difficulty, ref lastIsGood);
                        TryAddGeneratedNote(extraTime, isBlue ? NoteKind.GoodTap : NoteKind.BadTap, laneCursor);
                    }
                }
            }

            playableBeatIndex++;
        }

        if (chart.Count == 0)
        {
            chart.Add(new NoteSpec(firstPlayableTime, NoteKind.GoodTap, 1));
        }
    }

    private static float FindEnvelopePeakTime(float[] envelope, float rate, float centerTime, float radiusSeconds)
    {
        int center = Mathf.RoundToInt(centerTime * rate);
        int radius = Mathf.Max(1, Mathf.RoundToInt(radiusSeconds * rate));
        int first = Mathf.Clamp(center - radius, 0, envelope.Length - 1);
        int last = Mathf.Clamp(center + radius, 0, envelope.Length - 1);
        int bestIndex = center;
        float bestValue = float.MinValue;
        for (int i = first; i <= last; i++)
        {
            if (envelope[i] > bestValue)
            {
                bestValue = envelope[i];
                bestIndex = i;
            }
        }

        return bestIndex / rate;
    }

    private static float SampleEnvelope(float[] envelope, float rate, float time)
    {
        int index = Mathf.Clamp(Mathf.RoundToInt(time * rate), 0, envelope.Length - 1);
        return envelope[index];
    }

    private static float AverageEnvelope(float[] envelope, float rate, float startTime, float endTime)
    {
        int first = Mathf.Clamp(Mathf.FloorToInt(startTime * rate), 0, envelope.Length - 1);
        int last = Mathf.Clamp(Mathf.CeilToInt(endTime * rate), first, envelope.Length - 1);
        float total = 0f;
        for (int i = first; i <= last; i++)
        {
            total += envelope[i];
        }

        return total / Mathf.Max(1, last - first + 1);
    }

    private void BuildChartForMurekaSong(System.Random rng)
    {
        chart.Clear();
        DifficultyPreset difficulty = GetDifficultyPreset();

        float bpm = Mathf.Clamp(generatedBpm, 80f, 180f);
        float beatDuration = 60f / bpm;
        float firstNoteTime = Mathf.Max(1.1f, beatDuration * 2f);
        float finalNoteTime = Mathf.Max(firstNoteTime, generatedSongLength - 1.25f);
        int laneCursor = 1;
        int beatIndex = 0;
        bool lastIsGood = true;

        for (float noteTime = firstNoteTime; noteTime <= finalNoteTime; noteTime += beatDuration)
        {
            int beatInBar = beatIndex % BeatsPerBar;
            bool strongBeat = beatInBar == 0 || beatInBar == 2;
            double noteChance = strongBeat ? difficulty.FallbackStrongChance : difficulty.FallbackWeakChance;

            if (rng.NextDouble() <= noteChance)
            {
                laneCursor = ChooseNextLaneIndex(laneCursor, rng, difficulty);
                bool isGood = PickGoodNote(rng, difficulty, ref lastIsGood);
                bool isWheel = strongBeat && rng.NextDouble() <= difficulty.FallbackLongChance;
                NoteKind kind = isWheel
                    ? (isGood ? NoteKind.GoodWheelUp : NoteKind.BadWheelDown)
                    : (isGood ? NoteKind.GoodTap : NoteKind.BadTap);

                TryAddGeneratedNote(noteTime, kind, laneCursor);
            }

            if (beatInBar < BeatsPerBar - 1 && rng.NextDouble() <= difficulty.FallbackExtraNoteChance)
            {
                float extraTime = noteTime + beatDuration * 0.5f;
                laneCursor = ChooseNextLaneIndex(laneCursor, rng, difficulty);
                bool isGood = PickGoodNote(rng, difficulty, ref lastIsGood);
                TryAddGeneratedNote(extraTime, isGood ? NoteKind.GoodTap : NoteKind.BadTap, laneCursor);
            }

            beatIndex++;
        }

        if (chart.Count == 0)
        {
            chart.Add(new NoteSpec(firstNoteTime, NoteKind.GoodTap, 1));
        }
    }

    private bool TryAddGeneratedNote(float noteTime, NoteKind kind, int laneIndex)
    {
        if (chart.Count > 0)
        {
            NoteSpec previous = chart[chart.Count - 1];
            float requiredGap = Mathf.Max(GetGeneratedNoteGap(previous.Kind), GetGeneratedNoteGap(kind));
            if (noteTime - previous.Time < requiredGap)
            {
                return false;
            }
        }

        chart.Add(new NoteSpec(noteTime, kind, Mathf.Clamp(laneIndex, 0, NoteYs.Length - 1)));
        return true;
    }

    private static int ChooseNextLaneIndex(int currentLane, System.Random rng, DifficultyPreset difficulty)
    {
        int clampedLane = Mathf.Clamp(currentLane, 0, NoteYs.Length - 1);
        if (rng.NextDouble() > difficulty.LaneChangeChance)
        {
            return clampedLane;
        }

        bool wideJump = rng.NextDouble() <= difficulty.WideLaneJumpChance;
        if (wideJump)
        {
            if (clampedLane == 0)
            {
                return NoteYs.Length - 1;
            }

            if (clampedLane == NoteYs.Length - 1)
            {
                return 0;
            }
        }

        if (clampedLane == 0)
        {
            return 1;
        }

        if (clampedLane == NoteYs.Length - 1)
        {
            return NoteYs.Length - 2;
        }

        return clampedLane + (rng.Next(2) == 0 ? -1 : 1);
    }

    private static bool PickGoodNote(System.Random rng, DifficultyPreset difficulty, ref bool lastIsGood)
    {
        bool isGood = rng.NextDouble() <= difficulty.ColorSwitchChance
            ? !lastIsGood
            : lastIsGood;

        if (rng.NextDouble() <= 0.14f)
        {
            isGood = rng.NextDouble() <= difficulty.GoodNoteChance;
        }

        lastIsGood = isGood;
        return isGood;
    }

    private float GetGeneratedNoteGap(NoteKind kind)
    {
        DifficultyPreset difficulty = GetDifficultyPreset();
        return IsWheelNote(kind) ? difficulty.LongGap : difficulty.TapGap;
    }

    private void PlayCurrentSong()
    {
        if (musicSource == null || currentSongClip == null)
        {
            return;
        }

        musicSource.Stop();
        musicSource.clip = currentSongClip;
        musicSource.PlayScheduled(songStartDspTime);
    }

    private static GameObject CreateBlock(Transform parent, string name, Vector2 position, Vector2 size, Color color, int sortingOrder)
    {
        GameObject block = new GameObject(name);
        block.transform.SetParent(parent, false);
        block.transform.position = new Vector3(position.x, position.y, 0f);
        block.transform.localScale = new Vector3(size.x, size.y, 1f);

        SpriteRenderer renderer = block.AddComponent<SpriteRenderer>();
        renderer.sprite = whiteSprite;
        renderer.color = color;
        renderer.sortingOrder = sortingOrder;
        return block;
    }

    private static GameObject CreateCircleBlock(Transform parent, string name, Vector2 position, float size, Color color, int sortingOrder)
    {
        GameObject block = new GameObject(name);
        block.transform.SetParent(parent, false);
        block.transform.position = new Vector3(position.x, position.y, 0f);
        block.transform.localScale = Vector3.one * size;

        SpriteRenderer renderer = block.AddComponent<SpriteRenderer>();
        renderer.sprite = softCircleSprite;
        renderer.color = color;
        renderer.sortingOrder = sortingOrder;
        return block;
    }

    private static void LoadUiSheetAssets()
    {
        if (uiSheetTexture != null)
        {
            return;
        }

        uiSheetTexture = Resources.Load<Texture2D>("UI/art_ui_seet");
        if (uiSheetTexture == null)
        {
            Debug.LogWarning("UI sheet could not be loaded from Resources/UI/art_ui_seet.");
            return;
        }

        blueTapSprites = new[]
        {
            CreateUiSheetSprite(1956, 647, 434, 426, new Vector2(0.5f, 0.5f), 335f),
            CreateUiSheetSprite(2456, 647, 434, 426, new Vector2(0.5f, 0.5f), 335f),
            CreateUiSheetSprite(2976, 647, 434, 426, new Vector2(0.5f, 0.5f), 335f)
        };
        redTapSprites = new[]
        {
            CreateUiSheetSprite(2131, 1277, 463, 460, new Vector2(0.5f, 0.5f), 355f),
            CreateUiSheetSprite(2598, 1277, 463, 460, new Vector2(0.5f, 0.5f), 355f),
            CreateUiSheetSprite(3087, 1277, 461, 460, new Vector2(0.5f, 0.5f), 355f)
        };
        redLongSprite = CreateUiSheetSprite(38, 519, 403, 1171, new Vector2(0.5f, 0.13f), 300f);
        blueLongSprite = CreateUiSheetSprite(442, 519, 402, 1171, new Vector2(0.5f, 0.13f), 300f);
        qGuideSprite = CreateUiSheetSprite(1843, 41, 498, 498, new Vector2(0.5f, 0.5f), 390f);
        eGuideSprite = CreateUiSheetSprite(2516, 31, 498, 498, new Vector2(0.5f, 0.5f), 390f);
        mouseDownGuideSprite = CreateUiSheetSprite(959, 1204, 436, 500, new Vector2(0.5f, 0.5f), 390f);
        mouseUpGuideSprite = CreateUiSheetSprite(1517, 1204, 436, 500, new Vector2(0.5f, 0.5f), 390f);
        judgeRingSprite = CreateUiSheetSprite(40, 0, 500, 500, new Vector2(0.5f, 0.5f), 300f);
        hitLineSprite = CreateUiSheetSprite(1180, 30, 120, 500, new Vector2(0.5f, 0.5f), 150f);
    }

    private static Sprite CreateUiSheetSprite(int x, int topY, int width, int height, Vector2 pivot, float sourcePixelsPerUnit)
    {
        float scaleX = uiSheetTexture.width / (float)SourceUiSheetWidth;
        float scaleY = uiSheetTexture.height / (float)SourceUiSheetHeight;
        Rect textureRect = new Rect(
            x * scaleX,
            uiSheetTexture.height - (topY + height) * scaleY,
            width * scaleX,
            height * scaleY);
        float pixelsPerUnit = sourcePixelsPerUnit * Mathf.Min(scaleX, scaleY);
        return Sprite.Create(uiSheetTexture, textureRect, pivot, pixelsPerUnit, 0, SpriteMeshType.FullRect);
    }

    private void LoadJudgementTextures()
    {
        missJudgementTexture = Resources.Load<Texture2D>("Judgements/01_MISS");
        goodJudgementTexture = Resources.Load<Texture2D>("Judgements/02_GOOD");
        greatJudgementTexture = Resources.Load<Texture2D>("Judgements/03_GREAT");
        perfectJudgementTexture = Resources.Load<Texture2D>("Judgements/04_PERFECT");
        ConfigureJudgementTexture(missJudgementTexture);
        ConfigureJudgementTexture(goodJudgementTexture);
        ConfigureJudgementTexture(greatJudgementTexture);
        ConfigureJudgementTexture(perfectJudgementTexture);
    }

    private static void ConfigureJudgementTexture(Texture2D texture)
    {
        if (texture == null)
        {
            return;
        }

        texture.filterMode = FilterMode.Bilinear;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.anisoLevel = 1;
    }

    private void DrawJudgementImage(float scale)
    {
        Texture2D texture = GetJudgementTexture(judgementKind);
        if (texture == null)
        {
            return;
        }

        float remaining = Mathf.Max(0f, judgementVisibleUntil - Time.time);
        float elapsed = 0.55f - remaining;
        float pop = 1f + Mathf.Sin(Mathf.Clamp01(elapsed / 0.18f) * Mathf.PI) * 0.1f;
        float alpha = Mathf.Clamp01(remaining / 0.18f);
        if (remaining > 0.18f)
        {
            alpha = 1f;
        }

        float width = Mathf.Min(Screen.width * 0.52f, 590f * scale) * pop;
        float height = width * texture.height / texture.width;
        float maxHeight = 165f * scale * pop;
        if (height > maxHeight)
        {
            height = maxHeight;
            width = height * texture.width / texture.height;
        }

        Rect rect = new Rect((Screen.width - width) * 0.5f, Screen.height * 0.28f, width, height);
        Color previous = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUI.DrawTexture(rect, texture, ScaleMode.ScaleToFit, true);
        GUI.color = previous;
    }

    private Texture2D GetJudgementTexture(JudgementKind kind)
    {
        switch (kind)
        {
            case JudgementKind.Perfect:
                return perfectJudgementTexture;
            case JudgementKind.Great:
                return greatJudgementTexture;
            case JudgementKind.Good:
                return goodJudgementTexture;
            case JudgementKind.Miss:
                return missJudgementTexture;
            default:
                return null;
        }
    }

    private void FlashJudgement(JudgementKind kind)
    {
        judgementKind = kind;
        judgementVisibleUntil = Time.time + 0.55f;
    }

    private void EnsureGuiStyles()
    {
        if (gameFont == null)
        {
            gameFont = Resources.Load<Font>("Fonts/Hakgyoansim_Dunggeunmiso_B");
        }

        ApplyGameFontToSkin();
        float scale = Mathf.Clamp(Screen.height / 720f, 0.72f, 1.35f);
        if (titleStyle != null && Mathf.Abs(guiStyleScale - scale) < 0.001f)
        {
            return;
        }

        guiStyleScale = scale;

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(18f * scale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        titleStyle.normal.textColor = new Color(0.8f, 0.94f, 1f, 1f);

        numberStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(36f * scale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        numberStyle.normal.textColor = WhiteColor;

        comboNumberStyle = new GUIStyle(numberStyle)
        {
            fontSize = Mathf.RoundToInt(58f * scale),
            alignment = TextAnchor.MiddleCenter
        };
        comboNumberStyle.normal.textColor = WhiteColor;

        comboLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(22f * scale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        comboLabelStyle.normal.textColor = new Color(0.38f, 1f, 0.96f, 1f);

        smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(16f * scale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        smallStyle.normal.textColor = WhiteColor;

        guideStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(15f * scale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        guideStyle.normal.textColor = WhiteColor;

    }

    private void ApplyGameFontToSkin()
    {
        if (gameFont == null || GUI.skin == null)
        {
            return;
        }

        GUI.skin.font = gameFont;
        GUI.skin.label.font = gameFont;
        GUI.skin.button.font = gameFont;
        GUI.skin.box.font = gameFont;
        GUI.skin.textField.font = gameFont;
        GUI.skin.textArea.font = gameFont;
        GUI.skin.toggle.font = gameFont;
        GUI.skin.window.font = gameFont;
    }

    private static void DrawControlCard(Rect rect, string input, string label, Color accent, float scale)
    {
        DrawPanel(rect, new Color(0.02f, 0.09f, 0.18f, 0.88f));

        GUIStyle inputStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(17f * scale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        inputStyle.normal.textColor = WhiteColor;

        GUIStyle labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(14f * scale),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        labelStyle.normal.textColor = accent;

        GUI.Label(new Rect(rect.x + 6f * scale, rect.y + 7f * scale, rect.width - 12f * scale, 22f * scale), input, inputStyle);
        GUI.Label(new Rect(rect.x + 6f * scale, rect.y + 30f * scale, rect.width - 12f * scale, 22f * scale), label, labelStyle);
    }

    private static void DrawPanel(Rect rect, Color color)
    {
        DrawRect(rect, new Color(0.72f, 0.9f, 1f, 0.9f));
        DrawRect(new Rect(rect.x + 3f, rect.y + 3f, rect.width - 6f, rect.height - 6f), color);
    }

    private static void DrawOutlinedLabel(Rect rect, string text, GUIStyle style, Color outlineColor, float offset)
    {
        Color previousTextColor = style.normal.textColor;
        style.normal.textColor = outlineColor;
        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                if (x == 0 && y == 0)
                {
                    continue;
                }

                GUI.Label(new Rect(rect.x + x * offset, rect.y + y * offset, rect.width, rect.height), text, style);
            }
        }

        style.normal.textColor = previousTextColor;
        GUI.Label(rect, text, style);
    }

    private static void DrawComboNumber(
        Rect rect,
        int value,
        GUIStyle style,
        Color outlineColor,
        float outlineOffset)
    {
        string text = Mathf.Max(0, value).ToString("0000");
        int firstActiveDigit = value <= 0 ? text.Length : text.Length - Mathf.Max(1, value.ToString().Length);
        Color previousTextColor = style.normal.textColor;
        style.normal.textColor = new Color(0.46f, 0.46f, 0.50f, 1f);
        DrawOutlinedLabel(rect, text, style, outlineColor, outlineOffset);

        if (firstActiveDigit < text.Length)
        {
            float textWidth = style.CalcSize(new GUIContent(text)).x;
            float characterWidth = textWidth / text.Length;
            float textStartX = rect.center.x - textWidth * 0.5f;
            Rect activeClip = new Rect(
                textStartX + firstActiveDigit * characterWidth,
                rect.y,
                characterWidth * (text.Length - firstActiveDigit),
                rect.height);
            DrawGradientTextFill(
                rect,
                activeClip,
                text,
                style,
                new Color(1f, 0.20f, 0.70f, 1f),
                new Color(0.05f, 0.95f, 1f, 1f));
        }

        style.normal.textColor = previousTextColor;
    }

    private static void DrawContinuousGradientOutlinedLabel(
        Rect rect,
        string text,
        GUIStyle style,
        Color outlineColor,
        float outlineOffset,
        Color topColor,
        Color bottomColor)
    {
        Color previousTextColor = style.normal.textColor;
        style.normal.textColor = Color.clear;
        DrawOutlinedLabel(rect, text, style, outlineColor, outlineOffset);
        DrawGradientTextFill(rect, rect, text, style, topColor, bottomColor);
        style.normal.textColor = previousTextColor;
    }

    private static void DrawGradientTextFill(
        Rect textRect,
        Rect clipRect,
        string text,
        GUIStyle style,
        Color topColor,
        Color bottomColor)
    {
        const int slices = 16;
        float sliceHeight = textRect.height / slices;
        for (int i = 0; i < slices; i++)
        {
            Rect sliceRect = new Rect(
                clipRect.x,
                textRect.y + i * sliceHeight,
                clipRect.width,
                sliceHeight + 1f);
            Rect clippedSlice = IntersectRects(sliceRect, clipRect);
            if (clippedSlice.width <= 0f || clippedSlice.height <= 0f)
            {
                continue;
            }

            GUI.BeginGroup(clippedSlice);
            style.normal.textColor = Color.Lerp(topColor, bottomColor, i / (float)(slices - 1));
            GUI.Label(
                new Rect(
                    textRect.x - clippedSlice.x,
                    textRect.y - clippedSlice.y,
                    textRect.width,
                    textRect.height),
                text,
                style);
            GUI.EndGroup();
        }
    }

    private static Rect IntersectRects(Rect first, Rect second)
    {
        float xMin = Mathf.Max(first.xMin, second.xMin);
        float yMin = Mathf.Max(first.yMin, second.yMin);
        float xMax = Mathf.Min(first.xMax, second.xMax);
        float yMax = Mathf.Min(first.yMax, second.yMax);
        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    private static void DrawUiSheetRegion(Rect destination, int sourceX, int sourceTopY, int sourceWidth, int sourceHeight)
    {
        if (uiSheetTexture == null)
        {
            return;
        }

        Rect uv = new Rect(
            sourceX / (float)SourceUiSheetWidth,
            1f - (sourceTopY + sourceHeight) / (float)SourceUiSheetHeight,
            sourceWidth / (float)SourceUiSheetWidth,
            sourceHeight / (float)SourceUiSheetHeight);
        GUI.DrawTextureWithTexCoords(destination, uiSheetTexture, uv, true);
    }

    private static void DrawUiSheetRegionBottomFill(
        Rect fullDestination,
        int sourceX,
        int sourceTopY,
        int sourceWidth,
        int sourceHeight,
        float fill)
    {
        if (uiSheetTexture == null || fill <= 0f)
        {
            return;
        }

        fill = Mathf.Clamp01(fill);
        float filledHeight = fullDestination.height * fill;
        Rect destination = new Rect(
            fullDestination.x,
            fullDestination.yMax - filledHeight,
            fullDestination.width,
            filledHeight);
        Rect uv = new Rect(
            sourceX / (float)SourceUiSheetWidth,
            1f - (sourceTopY + sourceHeight) / (float)SourceUiSheetHeight,
            sourceWidth / (float)SourceUiSheetWidth,
            sourceHeight / (float)SourceUiSheetHeight * fill);
        GUI.DrawTextureWithTexCoords(destination, uiSheetTexture, uv, true);
    }

    private static void DrawRect(Rect rect, Color color)
    {
        Color previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, whiteTexture);
        GUI.color = previous;
    }

    private static void EnsureSharedAssets()
    {
        if (characterAfterimageShader == null)
        {
            characterAfterimageShader = Resources.Load<Shader>("Shaders/RhythmSpineAfterimage");
        }

        if (backgroundColorRecoveryMaterial == null)
        {
            Shader recoveryShader = Resources.Load<Shader>("Shaders/RhythmSpriteColorRecovery");
            if (recoveryShader != null)
            {
                backgroundColorRecoveryMaterial = new Material(recoveryShader);
            }
        }

        if (musicNoteSprite == null)
        {
            musicNoteSprite = CreateMusicNoteSprite(false);
            musicNoteDoubleSprite = CreateMusicNoteSprite(true);
        }

        if (loadingGradientSprite == null)
        {
            loadingGradientSprite = CreateLoadingGradientSprite();
        }

        if (whiteSprite != null)
        {
            return;
        }

        whiteTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        whiteTexture.SetPixel(0, 0, Color.white);
        whiteTexture.Apply();

        whiteSprite = Sprite.Create(whiteTexture, new Rect(0f, 0f, 1f, 1f), new Vector2(0.5f, 0.5f), 1f);
        badTapSprite = CreateCircleNoteSprite(BadColor, WhiteColor, true);
        goodTapSprite = CreateDiamondNoteSprite(GoodColor, WhiteColor);
        badLongHeadSprite = CreateCircleNoteSprite(BadColor, WhiteColor, true);
        goodLongHeadSprite = CreateCircleNoteSprite(GoodColor, WhiteColor, true);
        softCircleSprite = CreateSoftCircleSprite();
    }

    private static Sprite CreateLoadingGradientSprite()
    {
        const int height = 256;
        Texture2D texture = new Texture2D(4, height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        Color top = new Color(0.11f, 0.11f, 0.12f, 1f);
        Color bottom = new Color(0.012f, 0.012f, 0.016f, 1f);
        for (int y = 0; y < height; y++)
        {
            Color color = Color.Lerp(bottom, top, Mathf.Pow(y / (float)(height - 1), 1.35f));
            for (int x = 0; x < 4; x++)
            {
                texture.SetPixel(x, y, color);
            }
        }

        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, 4f, height), new Vector2(0.5f, 0.5f), 1f);
    }

    private static Sprite CreateMusicNoteSprite(bool beamed)
    {
        // Drawn as white fill with a gray outline so a runtime tint produces a
        // pastel note body with a darker border of the same hue.
        const int size = 128;
        const float outlineWidth = 6f;
        Texture2D texture = CreateTransparentTexture(size, size);
        Color outlineShade = new Color(0.38f, 0.38f, 0.46f, 1f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2(x, y);
                float distance = beamed
                    ? DoubleMusicNoteDistance(p)
                    : SingleMusicNoteDistance(p);

                float fillAlpha = 1f - SmoothEdge(-1.5f, 1.5f, distance);
                float outlineAlpha = 1f - SmoothEdge(outlineWidth - 1.5f, outlineWidth + 1.5f, distance);
                if (outlineAlpha <= 0f)
                {
                    continue;
                }

                Color color = Color.Lerp(outlineShade, Color.white, fillAlpha);
                color.a = outlineAlpha;
                texture.SetPixel(x, y, color);
            }
        }

        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
    }

    // GLSL-style smoothstep: 0 below edge0, 1 above edge1. Unity's Mathf.SmoothStep
    // interpolates from..to by t instead, so it cannot be used for edge thresholds.
    private static float SmoothEdge(float edge0, float edge1, float x)
    {
        float t = Mathf.Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }

    private static float SingleMusicNoteDistance(Vector2 p)
    {
        // Eighth note: tilted round head, stem, and a curved flag.
        float head = TiltedEllipseDistance(p, new Vector2(46f, 30f), 21f, 15f, -22f);
        float stem = SegmentDistance(p, new Vector2(63f, 36f), new Vector2(63f, 102f)) - 5f;
        float flag = BezierBandDistance(
            p,
            new Vector2(63f, 102f),
            new Vector2(92f, 94f),
            new Vector2(86f, 62f),
            5f);
        return Mathf.Min(head, Mathf.Min(stem, flag));
    }

    private static float DoubleMusicNoteDistance(Vector2 p)
    {
        // Beamed pair: two heads, two stems, and a thick slanted beam on top.
        float leftHead = TiltedEllipseDistance(p, new Vector2(30f, 30f), 17f, 13f, -20f);
        float rightHead = TiltedEllipseDistance(p, new Vector2(86f, 38f), 17f, 13f, -20f);
        float leftStem = SegmentDistance(p, new Vector2(44f, 34f), new Vector2(44f, 96f)) - 4.5f;
        float rightStem = SegmentDistance(p, new Vector2(100f, 42f), new Vector2(100f, 104f)) - 4.5f;
        float beam = SegmentDistance(p, new Vector2(43f, 94f), new Vector2(101f, 102f)) - 7f;
        float distance = Mathf.Min(leftHead, rightHead);
        distance = Mathf.Min(distance, Mathf.Min(leftStem, rightStem));
        return Mathf.Min(distance, beam);
    }

    private static float TiltedEllipseDistance(Vector2 p, Vector2 center, float radiusX, float radiusY, float tiltDegrees)
    {
        float radians = tiltDegrees * Mathf.Deg2Rad;
        float cos = Mathf.Cos(radians);
        float sin = Mathf.Sin(radians);
        Vector2 local = p - center;
        Vector2 rotated = new Vector2(local.x * cos + local.y * sin, -local.x * sin + local.y * cos);
        Vector2 scaled = new Vector2(rotated.x / radiusX, rotated.y / radiusY);
        return (scaled.magnitude - 1f) * Mathf.Min(radiusX, radiusY);
    }

    private static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude));
        return Vector2.Distance(p, a + ab * t);
    }

    private static float BezierBandDistance(Vector2 p, Vector2 start, Vector2 control, Vector2 end, float halfWidth)
    {
        const int samples = 14;
        float best = float.MaxValue;
        Vector2 previous = start;
        for (int i = 1; i <= samples; i++)
        {
            float t = i / (float)samples;
            Vector2 point = Vector2.LerpUnclamped(
                Vector2.LerpUnclamped(start, control, t),
                Vector2.LerpUnclamped(control, end, t),
                t);
            best = Mathf.Min(best, SegmentDistance(p, previous, point));
            previous = point;
        }

        return best - halfWidth;
    }

    private static Sprite CreateSoftCircleSprite()
    {
        Texture2D texture = CreateTransparentTexture(128, 128);
        Vector2 center = new Vector2(63.5f, 63.5f);
        for (int y = 0; y < texture.height; y++)
        {
            for (int x = 0; x < texture.width; x++)
            {
                float distance = Vector2.Distance(new Vector2(x, y), center);
                float alpha = 1f - Mathf.SmoothStep(54f, 63f, distance);
                if (alpha > 0f)
                {
                    texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
        }

        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, 128f, 128f), new Vector2(0.5f, 0.5f), 100f);
    }

    private static Sprite CreateCircleNoteSprite(Color fill, Color outline, bool doubleChevron)
    {
        Texture2D texture = CreateTransparentTexture(128, 128);
        Vector2 center = new Vector2(63.5f, 63.5f);
        const float outerRadius = 58f;
        const float innerRadius = 46f;

        for (int y = 0; y < texture.height; y++)
        {
            for (int x = 0; x < texture.width; x++)
            {
                float distance = Vector2.Distance(new Vector2(x, y), center);
                if (distance <= outerRadius)
                {
                    texture.SetPixel(x, y, outline);
                }

                if (distance <= innerRadius)
                {
                    float shine = Mathf.InverseLerp(innerRadius, 0f, distance) * 0.2f;
                    texture.SetPixel(x, y, Color.Lerp(fill, Color.white, shine));
                }
            }
        }

        if (doubleChevron)
        {
            DrawDoubleLeftChevrons(texture, new Color(0.35f, 0.02f, 0.05f, 0.85f), new Vector2(2f, -2f), 8);
            DrawDoubleLeftChevrons(texture, WhiteColor, Vector2.zero, 6);
        }

        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, 128f, 128f), new Vector2(0.5f, 0.5f), 100f);
    }

    private static Sprite CreateDiamondNoteSprite(Color fill, Color outline)
    {
        Texture2D texture = CreateTransparentTexture(128, 128);
        const float center = 63.5f;
        const float outer = 60f;
        const float inner = 47f;

        for (int y = 0; y < texture.height; y++)
        {
            for (int x = 0; x < texture.width; x++)
            {
                float distance = Mathf.Abs(x - center) + Mathf.Abs(y - center);
                if (distance <= outer)
                {
                    texture.SetPixel(x, y, outline);
                }

                if (distance <= inner)
                {
                    float shine = Mathf.InverseLerp(inner, 0f, distance) * 0.2f;
                    texture.SetPixel(x, y, Color.Lerp(fill, Color.white, shine));
                }
            }
        }

        DrawDoubleLeftChevrons(texture, new Color(0.01f, 0.04f, 0.22f, 0.85f), new Vector2(2f, -2f), 8);
        DrawDoubleLeftChevrons(texture, WhiteColor, Vector2.zero, 6);
        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, 128f, 128f), new Vector2(0.5f, 0.5f), 100f);
    }

    private static Texture2D CreateTransparentTexture(int width, int height)
    {
        Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        Color clear = new Color(0f, 0f, 0f, 0f);
        Color[] pixels = new Color[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = clear;
        }

        texture.SetPixels(pixels);
        return texture;
    }

    private static void DrawDoubleLeftChevrons(Texture2D texture, Color color, Vector2 offset, int thickness)
    {
        DrawLeftChevron(texture, new Vector2(57f, 64f) + offset, color, thickness);
        DrawLeftChevron(texture, new Vector2(78f, 64f) + offset, color, thickness);
    }

    private static void DrawLeftChevron(Texture2D texture, Vector2 center, Color color, int thickness)
    {
        DrawLine(texture, center + new Vector2(11f, 19f), center + new Vector2(-10f, 0f), color, thickness);
        DrawLine(texture, center + new Vector2(-10f, 0f), center + new Vector2(11f, -19f), color, thickness);
    }

    private static void DrawLine(Texture2D texture, Vector2 start, Vector2 end, Color color, int thickness)
    {
        int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(start, end) * 2f));
        float radius = thickness * 0.5f;
        float radiusSqr = radius * radius;

        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            Vector2 point = Vector2.Lerp(start, end, t);
            int px = Mathf.RoundToInt(point.x);
            int py = Mathf.RoundToInt(point.y);
            int range = Mathf.CeilToInt(radius);

            for (int y = -range; y <= range; y++)
            {
                for (int x = -range; x <= range; x++)
                {
                    if (x * x + y * y > radiusSqr)
                    {
                        continue;
                    }

                    int drawX = px + x;
                    int drawY = py + y;
                    if (drawX >= 0 && drawX < texture.width && drawY >= 0 && drawY < texture.height)
                    {
                        texture.SetPixel(drawX, drawY, color);
                    }
                }
            }
        }
    }
}
