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
    private const float MissWindow = 0.42f;
    private const float AutoFollowLookAhead = 1.85f;
    private const float AutoFollowSpeed = 12f;
    private const float MinTapNoteGap = 0.38f;
    private const float MinLongNoteGap = 0.95f;
    private const float MusicLeadIn = 2.1f;
    private const float HitFxLifetime = 0.28f;
    private const float LongNoteScratchDuration = 0.22f;
    private const float LongNoteTailDistance = 2.74f;
    private const float BlueLongNoteVerticalOffset = 1.94f;
    private const float CharacterAfterimageDuration = 0.28f;
    private const float WheelGestureThreshold = 0.35f;
    private const float WheelGestureReleaseDelay = 0.065f;
    private const float CharacterAnimationMix = 0.045f;
    private const int TutorialGuideNoteCount = 4;
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
    private static Sprite badLongTailSprite;
    private static Sprite goodLongTailSprite;
    private static Sprite rightChevronSprite;
    private static Sprite judgeRingSprite;
    private static Sprite whiteSprite;
    private static Sprite softCircleSprite;
    private static Material lineMaterial;
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

    private readonly List<Note> notes = new List<Note>();
    private readonly List<NoteSpec> chart = new List<NoteSpec>();
    private readonly Dictionary<string, SongAnalysis> songAnalysisCache = new Dictionary<string, SongAnalysis>();

    private Transform notesRoot;
    private Transform judgeRing;
    private SpriteRenderer judgeRingRenderer;
    private SpriteRenderer hitLineRenderer;
    private Camera gameCamera;
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
    private bool chartFinished;
    private bool instructionShown;
    private float lastMurekaBackendStartAttempt = -999f;
    private int score;
    private int combo;
    private int bestCombo;
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
    private Font gameFont;
    private SkeletonAnimation characterAnimation;
    private string characterRunAnimation;
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

    private void Update()
    {
        ReadInput();
        UpdateNotes();
        UpdateJudgeRing();
        UpdateCameraShake();
        UpdateHitLineBlink();
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
        DrawPanel(new Rect(18f * scale, 16f * scale, 210f * scale, 78f * scale), new Color(0.02f, 0.09f, 0.18f, 0.88f));
        GUI.Label(new Rect(34f * scale, 24f * scale, 180f * scale, 25f * scale), "SCORE", titleStyle);
        GUI.Label(new Rect(34f * scale, 48f * scale, 180f * scale, 42f * scale), score.ToString("000000"), numberStyle);

        Rect comboNumberRect = new Rect((Screen.width - 300f * scale) * 0.5f, 4f * scale, 300f * scale, 68f * scale);
        Rect comboLabelRect = new Rect(comboNumberRect.x, 65f * scale, comboNumberRect.width, 32f * scale);
        DrawOutlinedLabel(comboNumberRect, combo.ToString("0000"), comboNumberStyle, new Color(0.12f, 0.04f, 0.16f, 1f), 4f * scale);
        DrawOutlinedLabel(comboLabelRect, "COMBO", comboLabelStyle, new Color(0.12f, 0.04f, 0.16f, 1f), 3f * scale);

        DrawLocalSongSelector(scale);

        if (Time.time <= judgementVisibleUntil)
        {
            DrawJudgementImage(scale);
        }

        if (ShowMurekaControls)
        {
            DrawMurekaControls(scale);
        }

        if (songSelectionVisible)
        {
            smallStyle.normal.textColor = new Color(1f, 1f, 1f, 0.78f);
            GUI.Label(new Rect(18f * scale, Screen.height - 64f * scale, 620f * scale, 28f * scale), murekaStatus, smallStyle);
            GUI.Label(new Rect(18f * scale, Screen.height - 38f * scale, 620f * scale, 28f * scale), generatedSongLabel + "  " + generatedBpm.ToString("0.0") + " BPM", smallStyle);
        }
        else
        {
            DrawGameplayHud(scale);
        }
    }

    private void DrawGameplayHud(float scale)
    {
        float heartSize = 104f * scale;
        DrawUiSheetRegion(
            new Rect(18f * scale, Screen.height - heartSize - 12f * scale, heartSize, heartSize),
            851, 606, 552, 527);

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
            return;
        }

        FlashJudgement(JudgementKind.Miss);
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
        float bestDelta = GetHitWindow(inputKind);
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
        judgeRingRenderer.color = Color.Lerp(new Color(0.72f, 0.92f, 1f, 0.75f), WhiteColor, Mathf.PingPong(Time.time * 1.8f, 1f));
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
        if (chart.Count == 0)
        {
            murekaStatus = isRequestingMurekaSong ? murekaStatus : "Select a local song to play.";
            return;
        }

        songStartDspTime = AudioSettings.dspTime + MusicLeadIn;
        chartDuration = Mathf.Max(generatedSongLength, chart[chart.Count - 1].Time);
        chartFinished = false;

        score = 0;
        combo = 0;

        bool showTutorialGuides = !instructionShown;
        for (int i = 0; i < chart.Count; i++)
        {
            NoteSpec spec = chart[i];
            CreateNote(
                spec.Kind,
                spec.LaneIndex,
                songStartDspTime + spec.Time + audioVisualLatency,
                showTutorialGuides && i < TutorialGuideNoteCount);
        }

        PlayCurrentSong();
        judgementKind = JudgementKind.None;
        judgementVisibleUntil = 0f;
        if (showTutorialGuides)
        {
            instructionShown = true;
        }
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

        Sprite headSprite = GetHeadSprite(kind);
        GameObject head = new GameObject("Head");
        head.transform.SetParent(root.transform, false);
        head.transform.localScale = Vector3.one * 1.22f;

        SpriteRenderer headRenderer = head.AddComponent<SpriteRenderer>();
        headRenderer.sprite = headSprite;
        headRenderer.sortingOrder = 12;

        Transform[] glitchLayers = null;
        if (kind == NoteKind.BadTap || kind == NoteKind.BadWheelDown)
        {
            glitchLayers = CreateRedNoteGlitchLayers(root.transform);
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
            GlitchSeed = UnityEngine.Random.Range(0.1f, 999f)
        });
    }

    private static Transform[] CreateRedNoteGlitchLayers(Transform noteRoot)
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
                    ? new Color(1f, 0.06f, 0.12f, 0.24f)
                    : new Color(0.08f, 0.92f, 1f, 0.16f);
                layerObject.SetActive(false);
                layers.Add(layerObject.transform);
            }
        }

        return layers.ToArray();
    }

    private static void UpdateRedNoteGlitch(Note note, ref Vector3 notePosition)
    {
        if (note.GlitchLayers == null || note.GlitchLayers.Length == 0)
        {
            return;
        }

        int glitchFrame = Mathf.FloorToInt(Time.unscaledTime * 28f);
        float noise = Mathf.PerlinNoise(note.GlitchSeed, glitchFrame * 0.173f);
        bool active = noise > 0.58f;
        float strength = noise > 0.82f ? 0.085f : 0.042f;
        if (active)
        {
            float horizontal = ((glitchFrame & 1) == 0 ? -1f : 1f) * strength;
            float vertical = Mathf.Sin(glitchFrame * 2.17f + note.GlitchSeed) * strength * 0.34f;
            notePosition += new Vector3(horizontal * 0.35f, vertical * 0.35f, 0f);
        }

        for (int i = 0; i < note.GlitchLayers.Length; i++)
        {
            Transform layer = note.GlitchLayers[i];
            if (layer == null)
            {
                continue;
            }

            if (layer.gameObject.activeSelf != active)
            {
                layer.gameObject.SetActive(active);
            }

            if (active)
            {
                float direction = (i & 1) == 0 ? -1f : 1f;
                layer.localPosition = new Vector3(direction * strength, -direction * strength * 0.22f, 0f);
            }
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
        if (sheetSprite != null)
        {
            Vector2 sheetDirection = bad
                ? new Vector2(0.72f, -0.72f).normalized
                : new Vector2(0.72f, 0.72f).normalized;
            GameObject sheetVisual = new GameObject(bad ? "Red Long Note" : "Blue Long Note");
            sheetVisual.transform.SetParent(root, false);
            sheetVisual.transform.localRotation = Quaternion.Euler(0f, 0f, bad ? -135f : -45f);
            sheetVisual.transform.localScale = Vector3.one * 0.94f;

            SpriteRenderer sheetRenderer = sheetVisual.AddComponent<SpriteRenderer>();
            sheetRenderer.sprite = sheetSprite;
            sheetRenderer.sortingOrder = 10;

            GameObject sheetTail = new GameObject("Long Tail Cap");
            sheetTail.transform.SetParent(root, false);
            sheetTail.transform.localPosition = new Vector3(
                sheetDirection.x * LongNoteTailDistance,
                sheetDirection.y * LongNoteTailDistance,
                0f);
            sheetTail.transform.localScale = Vector3.one * 0.64f;

            SpriteRenderer sheetTailRenderer = sheetTail.AddComponent<SpriteRenderer>();
            sheetTailRenderer.sprite = bad ? badLongTailSprite : goodLongTailSprite;
            sheetTailRenderer.sortingOrder = 11;
            return;
        }

        Vector2 direction = bad ? new Vector2(0.72f, -0.72f).normalized : new Vector2(0.72f, 0.72f).normalized;
        float length = 3.05f;
        Color bodyColor = bad ? BadColor : GoodColor;
        Color darkColor = bad ? BadDarkColor : GoodDarkColor;
        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;

        CreateLine(root, "Long Glow", direction, length, 1.05f, new Color(bodyColor.r, bodyColor.g, bodyColor.b, 0.24f), 4);
        CreateLine(root, "Long Outline", direction, length, 0.82f, WhiteColor, 5);
        CreateLine(root, "Long Shadow", direction, length, 0.62f, darkColor, 6);
        CreateLine(root, "Long Fill", direction, length, 0.50f, bodyColor, 7);

        GameObject tail = new GameObject("Tail Cap");
        tail.transform.SetParent(root, false);
        tail.transform.localPosition = new Vector3(direction.x * length, direction.y * length, 0f);
        tail.transform.localScale = Vector3.one * 0.55f;

        SpriteRenderer tailRenderer = tail.AddComponent<SpriteRenderer>();
        tailRenderer.sprite = bad ? badLongHeadSprite : goodLongHeadSprite;
        tailRenderer.sortingOrder = 8;

        for (int i = 0; i < 3; i++)
        {
            GameObject chevron = new GameObject("Long Chevron");
            chevron.transform.SetParent(root, false);
            float distance = 0.82f + i * 0.48f;
            chevron.transform.localPosition = new Vector3(direction.x * distance, direction.y * distance, 0f);
            chevron.transform.localRotation = Quaternion.Euler(0f, 0f, angle);
            chevron.transform.localScale = Vector3.one * 0.42f;

            SpriteRenderer renderer = chevron.AddComponent<SpriteRenderer>();
            renderer.sprite = rightChevronSprite;
            renderer.color = new Color(1f, 1f, 1f, 0.92f);
            renderer.sortingOrder = 9;
        }
    }

    private static void CreateLine(Transform root, string name, Vector2 direction, float length, float width, Color color, int sortingOrder)
    {
        GameObject lineObject = new GameObject(name);
        lineObject.transform.SetParent(root, false);

        LineRenderer line = lineObject.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.positionCount = 2;
        line.SetPosition(0, Vector3.zero);
        line.SetPosition(1, new Vector3(direction.x * length, direction.y * length, 0f));
        line.startWidth = width;
        line.endWidth = width;
        line.numCapVertices = 10;
        line.numCornerVertices = 10;
        line.material = lineMaterial;
        line.startColor = color;
        line.endColor = color;
        line.sortingOrder = sortingOrder;
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
        camera.allowMSAA = true;
        QualitySettings.antiAliasing = Mathf.Max(4, QualitySettings.antiAliasing);
        gameCamera = camera;
        cameraBasePosition = camera.transform.position;
    }

    private void SetupStage()
    {
        GameObject stage = new GameObject("Prototype Stage");

        CreateBlock(stage.transform, "Sky", new Vector2(0f, 0.8f), new Vector2(19f, 9.6f), new Color(0.24f, 0.07f, 0.67f, 1f), -20);
        CreateBlock(stage.transform, "Distant Glow", new Vector2(1.7f, -0.65f), new Vector2(16.5f, 3.15f), new Color(0.78f, 0.22f, 0.94f, 0.42f), -19);
        CreateBlock(stage.transform, "Distant City", new Vector2(1.7f, -1.55f), new Vector2(16.5f, 2.15f), new Color(0.17f, 0.62f, 0.96f, 0.38f), -18);
        CreateBlock(stage.transform, "Park Hill", new Vector2(0f, -3.65f), new Vector2(19f, 1.45f), new Color(0.86f, 0.28f, 0.82f, 1f), -15);
        CreateBlock(stage.transform, "Ground", new Vector2(0f, -4.45f), new Vector2(19f, 1.1f), new Color(1f, 0.33f, 0.74f, 1f), -14);
        CreateStageDecorations(stage.transform);
        CreateBlock(stage.transform, "Lane", new Vector2(0f, LaneY), new Vector2(19f, 0.075f), LaneColor, -2);

        float hitLineHeight = Mathf.Abs(NoteYs[NoteYs.Length - 1] - NoteYs[0]) + 1.1f;
        GameObject hitLine = CreateBlock(stage.transform, "Blinking Hit Line", new Vector2(HitX, LaneY), new Vector2(0.065f, hitLineHeight), new Color(0.9f, 1f, 1f, 0.92f), 1);
        hitLineRenderer = hitLine.GetComponent<SpriteRenderer>();

        GameObject ring = new GameObject("Judge Ring");
        ring.transform.SetParent(stage.transform, false);
        ring.transform.position = new Vector3(HitX, LaneY, 0f);
        judgeRing = ring.transform;
        judgeRingRenderer = ring.AddComponent<SpriteRenderer>();
        judgeRingRenderer.sprite = judgeRingSprite;
        judgeRingRenderer.sortingOrder = 10;

        notesRoot = new GameObject("Rhythm Notes").transform;
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
        TriggerCameraShake();
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
        LocalSongEntry song = localSongs[index];
        AudioClip clip = song.ResourceClip != null ? song.ResourceClip : song.LoadedClip;

        if (clip == null)
        {
            if (string.IsNullOrWhiteSpace(song.FilePath) || !File.Exists(song.FilePath))
            {
                murekaStatus = "Local song file was not found: " + song.Name;
                isLoadingLocalSong = false;
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
                    yield break;
                }

                clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                {
                    murekaStatus = "Local song could not be decoded by Unity.";
                    isLoadingLocalSong = false;
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

        RestartChart();
        songSelectionVisible = false;
        isLoadingLocalSong = false;
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
        if (currentSongClip != null && !isLoadingLocalSong && !isRequestingMurekaSong)
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
        using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(audioUrl, GuessAudioType(audioUrl)))
        {
            request.timeout = 90;
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                murekaStatus = "MUREKA audio download failed: " + request.error;
                yield break;
            }

            generatedSongClip = DownloadHandlerAudioClip.GetContent(request);
            if (generatedSongClip == null)
            {
                murekaStatus = "MUREKA audio could not be decoded by Unity.";
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

        RestartChart();
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
        if (titleStyle != null)
        {
            return;
        }

        titleStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(18f * Mathf.Clamp(Screen.height / 720f, 0.72f, 1.35f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        titleStyle.normal.textColor = new Color(0.8f, 0.94f, 1f, 1f);

        numberStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(36f * Mathf.Clamp(Screen.height / 720f, 0.72f, 1.35f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        numberStyle.normal.textColor = WhiteColor;

        comboNumberStyle = new GUIStyle(numberStyle)
        {
            fontSize = Mathf.RoundToInt(58f * Mathf.Clamp(Screen.height / 720f, 0.72f, 1.35f)),
            alignment = TextAnchor.MiddleCenter
        };
        comboNumberStyle.normal.textColor = WhiteColor;

        comboLabelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(22f * Mathf.Clamp(Screen.height / 720f, 0.72f, 1.35f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        comboLabelStyle.normal.textColor = new Color(0.38f, 1f, 0.96f, 1f);

        smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(16f * Mathf.Clamp(Screen.height / 720f, 0.72f, 1.35f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        smallStyle.normal.textColor = WhiteColor;

        guideStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(15f * Mathf.Clamp(Screen.height / 720f, 0.72f, 1.35f)),
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
        badLongTailSprite = CreateCircleNoteSprite(BadDarkColor, WhiteColor, false);
        goodLongTailSprite = CreateCircleNoteSprite(GoodColor, WhiteColor, false);
        rightChevronSprite = CreateRightChevronSprite();
        judgeRingSprite = CreateRingSprite();
        softCircleSprite = CreateSoftCircleSprite();

        Shader shader = Shader.Find("Sprites/Default");
        if (shader == null)
        {
            shader = Shader.Find("Universal Render Pipeline/Unlit");
        }

        lineMaterial = new Material(shader)
        {
            color = Color.white
        };
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

    private static Sprite CreateRightChevronSprite()
    {
        Texture2D texture = CreateTransparentTexture(64, 64);
        DrawLine(texture, new Vector2(20f, 14f), new Vector2(42f, 32f), WhiteColor, 8);
        DrawLine(texture, new Vector2(42f, 32f), new Vector2(20f, 50f), WhiteColor, 8);
        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, 64f, 64f), new Vector2(0.5f, 0.5f), 100f);
    }

    private static Sprite CreateRingSprite()
    {
        Texture2D texture = CreateTransparentTexture(160, 160);
        Vector2 center = new Vector2(79.5f, 79.5f);

        for (int y = 0; y < texture.height; y++)
        {
            for (int x = 0; x < texture.width; x++)
            {
                float distance = Vector2.Distance(new Vector2(x, y), center);
                if (distance <= 70f && distance >= 51f)
                {
                    texture.SetPixel(x, y, new Color(0.82f, 0.96f, 1f, 0.95f));
                }
                else if (distance <= 50f && distance >= 43f)
                {
                    texture.SetPixel(x, y, new Color(0.2f, 0.64f, 1f, 0.65f));
                }
            }
        }

        texture.Apply();
        return Sprite.Create(texture, new Rect(0f, 0f, 160f, 160f), new Vector2(0.5f, 0.5f), 100f);
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
