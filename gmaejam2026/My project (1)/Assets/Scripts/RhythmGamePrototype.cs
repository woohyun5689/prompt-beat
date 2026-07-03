using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Networking;

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
        public float HitTime;
        public float LaneY;
        public GameObject Root;
        public bool Judged;
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

    private const float LaneY = -2.55f;
    private const float HitX = -5.75f;
    private const float SpawnX = 8.75f;
    private const float DespawnX = -8.5f;
    private const float NoteSpeed = 4.35f;
    private const float HitWindow = 0.34f;
    private const float MissWindow = 0.42f;
    private const float AutoFollowLookAhead = 1.85f;
    private const float AutoFollowSpeed = 12f;
    private const float MinTapNoteGap = 0.38f;
    private const float MinLongNoteGap = 0.95f;
    private const float MusicLeadIn = 2.1f;
    private const int BeatsPerBar = 4;
    private const string MurekaBackendUrl = "http://127.0.0.1:8067";
    private const string MurekaProvider = "mureka_web_automation_imported";
    private const string MurekaBackendFolderName = "MurekaBackend";
    private const string MurekaBackendScriptName = "start_mureka_backend.ps1";
    private const float MurekaBackendStartupTimeout = 45f;
    private const string PromptControlName = "MurekaPromptField";
    private const string DefaultMurekaPrompt =
        "Bright energetic K-pop rhythm game song, clean strong beat, cute arcade mood, 128 bpm, catchy synth hook, short intro, no long silence";

    private static readonly Color BadColor = new Color(1f, 0.12f, 0.12f, 1f);
    private static readonly Color BadDarkColor = new Color(0.55f, 0.02f, 0.04f, 1f);
    private static readonly Color GoodColor = new Color(0.08f, 0.42f, 1f, 1f);
    private static readonly Color GoodDarkColor = new Color(0.02f, 0.13f, 0.46f, 1f);
    private static readonly Color WhiteColor = new Color(0.97f, 0.99f, 1f, 1f);
    private static readonly Color LaneColor = new Color(0.85f, 0.97f, 1f, 0.92f);
    private static readonly float[] NoteYs = { -3.35f, LaneY, -1.75f };

    private static Sprite badTapSprite;
    private static Sprite goodTapSprite;
    private static Sprite badLongHeadSprite;
    private static Sprite goodLongHeadSprite;
    private static Sprite rightChevronSprite;
    private static Sprite judgeRingSprite;
    private static Sprite whiteSprite;
    private static Material lineMaterial;
    private static Texture2D whiteTexture;

    private readonly List<Note> notes = new List<Note>();
    private readonly List<NoteSpec> chart = new List<NoteSpec>();

    private Transform notesRoot;
    private Transform judgeRing;
    private SpriteRenderer judgeRingRenderer;
    private AudioSource musicSource;
    private AudioClip generatedSongClip;
    private float songStartTime;
    private float chartDuration;
    private float generatedBpm = 128f;
    private float generatedSongLength = 14f;
    private int generatedSongSeed;
    private string generatedSongLabel = "MUREKA SONG";
    private string generatedSongProvider = "MUREKA";
    private string generatedSongWarning = "";
    private string murekaPrompt = DefaultMurekaPrompt;
    private string murekaStatus = "MUREKA website backend idle. Press START BACKEND or GENERATE.";
    private bool isRequestingMurekaSong;
    private bool isStartingMurekaBackend;
    private bool isEditingPrompt;
    private bool chartFinished;
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
    private GUIStyle smallStyle;

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
        LoadJudgementTextures();
        SetupCamera();
        SetupStage();
        SetupAudio();
    }

    private void Update()
    {
        ReadInput();
        UpdateNotes();
        UpdateJudgeRing();
    }

    private void OnDestroy()
    {
        if (generatedSongClip != null)
        {
            Destroy(generatedSongClip);
            generatedSongClip = null;
        }
    }

    private void OnGUI()
    {
        EnsureGuiStyles();

        float scale = Mathf.Clamp(Screen.height / 720f, 0.72f, 1.35f);
        DrawPanel(new Rect(18f * scale, 16f * scale, 210f * scale, 78f * scale), new Color(0.02f, 0.09f, 0.18f, 0.88f));
        GUI.Label(new Rect(34f * scale, 24f * scale, 180f * scale, 25f * scale), "SCORE", titleStyle);
        GUI.Label(new Rect(34f * scale, 48f * scale, 180f * scale, 42f * scale), score.ToString("000000"), numberStyle);

        DrawPanel(new Rect(250f * scale, 16f * scale, 155f * scale, 78f * scale), new Color(0.02f, 0.09f, 0.18f, 0.88f));
        GUI.Label(new Rect(266f * scale, 24f * scale, 130f * scale, 25f * scale), "COMBO", titleStyle);
        GUI.Label(new Rect(266f * scale, 48f * scale, 130f * scale, 42f * scale), combo + " x", numberStyle);

        float right = Screen.width - 538f * scale;
        if (right > 430f * scale)
        {
            DrawControlCard(new Rect(right, 16f * scale, 118f * scale, 58f * scale), "Q", "GOOD", GoodColor, scale);
            DrawControlCard(new Rect(right + 130f * scale, 16f * scale, 118f * scale, 58f * scale), "E", "BAD", BadColor, scale);
            DrawControlCard(new Rect(right + 260f * scale, 16f * scale, 132f * scale, 58f * scale), "WHEEL v", "BAD LONG", BadColor, scale);
            DrawControlCard(new Rect(right + 404f * scale, 16f * scale, 132f * scale, 58f * scale), "WHEEL ^", "GOOD LONG", GoodColor, scale);
        }

        if (Time.time <= judgementVisibleUntil)
        {
            DrawJudgementImage(scale);
        }

        DrawMurekaControls(scale);

        smallStyle.normal.textColor = new Color(1f, 1f, 1f, 0.75f);
        GUI.Label(new Rect(18f * scale, Screen.height - 90f * scale, 620f * scale, 28f * scale), murekaStatus, smallStyle);
        GUI.Label(new Rect(18f * scale, Screen.height - 64f * scale, 520f * scale, 28f * scale), generatedSongLabel + "  " + Mathf.RoundToInt(generatedBpm) + " BPM  " + generatedSongProvider, smallStyle);
        GUI.Label(new Rect(18f * scale, Screen.height - 38f * scale, 250f * scale, 28f * scale), "BEST " + bestCombo + "x", smallStyle);
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

        float scrollY = mouse.scroll.ReadValue().y;
        if (scrollY < -0.01f)
        {
            TryHit(NoteKind.BadWheelDown);
        }
        else if (scrollY > 0.01f)
        {
            TryHit(NoteKind.GoodWheelUp);
        }
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
        float bestDelta = HitWindow;
        float now = Time.time;

        for (int i = 0; i < notes.Count; i++)
        {
            Note note = notes[i];
            if (note.Judged || note.Kind != kind)
            {
                continue;
            }

            float delta = Mathf.Abs(now - note.HitTime);
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
        float bestDelta = HitWindow;
        float now = Time.time;

        for (int i = 0; i < notes.Count; i++)
        {
            Note note = notes[i];
            if (note.Judged || !UsesSameInputFamily(inputKind, note.Kind))
            {
                continue;
            }

            float delta = Mathf.Abs(now - note.HitTime);
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

    private static bool IsWheelNote(NoteKind kind)
    {
        return kind == NoteKind.BadWheelDown || kind == NoteKind.GoodWheelUp;
    }

    private void ApplyHit(Note note)
    {
        float delta = Mathf.Abs(Time.time - note.HitTime);
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
        FlashJudgement(judgement);
        ClearNote(note);
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

    private void UpdateNotes()
    {
        if (notes.Count == 0)
        {
            return;
        }

        bool allJudged = true;
        float now = Time.time;

        for (int i = 0; i < notes.Count; i++)
        {
            Note note = notes[i];
            if (note.Judged)
            {
                continue;
            }

            allJudged = false;
            float x = HitX + (note.HitTime - now) * NoteSpeed;

            if (note.Root != null)
            {
                note.Root.transform.position = new Vector3(x, note.LaneY, 0f);
                bool visible = x <= SpawnX && x >= DespawnX;
                if (note.Root.activeSelf != visible)
                {
                    note.Root.SetActive(visible);
                }
            }

            if (now - note.HitTime > MissWindow)
            {
                ApplyMiss(note);
            }
        }

        if (allJudged && !chartFinished && now > songStartTime + chartDuration + 1.1f)
        {
            chartFinished = true;
            murekaStatus = "Song finished. Edit the prompt, then press GENERATE.";
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

    private float GetAutoFollowJudgeY()
    {
        Note target = null;
        float bestDistance = AutoFollowLookAhead;
        float now = Time.time;

        for (int i = 0; i < notes.Count; i++)
        {
            Note note = notes[i];
            if (note.Judged)
            {
                continue;
            }

            float timeUntilHit = note.HitTime - now;
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
        if (chart.Count == 0)
        {
            murekaStatus = isRequestingMurekaSong ? murekaStatus : "No MUREKA chart ready. Press GENERATE.";
            return;
        }

        songStartTime = Time.time + MusicLeadIn;
        chartDuration = Mathf.Max(generatedSongLength, chart[chart.Count - 1].Time);
        chartFinished = false;

        for (int i = 0; i < chart.Count; i++)
        {
            NoteSpec spec = chart[i];
            CreateNote(spec.Kind, spec.LaneIndex, songStartTime + spec.Time);
        }

        PlayGeneratedSong();
        judgementKind = JudgementKind.None;
        judgementVisibleUntil = 0f;
    }

    private void CreateNote(NoteKind kind, int laneIndex, float hitTime)
    {
        float laneY = GetNoteY(kind, laneIndex);
        GameObject root = new GameObject(kind.ToString());
        root.transform.SetParent(notesRoot, false);
        root.transform.position = new Vector3(SpawnX, laneY, 0f);
        root.SetActive(false);

        if (kind == NoteKind.BadWheelDown || kind == NoteKind.GoodWheelUp)
        {
            CreateLongBody(root.transform, kind);
        }

        Sprite headSprite = GetHeadSprite(kind);
        GameObject head = new GameObject("Head");
        head.transform.SetParent(root.transform, false);
        head.transform.localScale = Vector3.one * 0.96f;

        SpriteRenderer headRenderer = head.AddComponent<SpriteRenderer>();
        headRenderer.sprite = headSprite;
        headRenderer.sortingOrder = 12;

        notes.Add(new Note
        {
            Kind = kind,
            HitTime = hitTime,
            LaneY = laneY,
            Root = root,
            Judged = false
        });
    }

    private static float GetLaneY(int laneIndex)
    {
        int clampedIndex = Mathf.Clamp(laneIndex, 0, NoteYs.Length - 1);
        return NoteYs[clampedIndex];
    }

    private static float GetNoteY(NoteKind kind, int laneIndex)
    {
        if (kind == NoteKind.GoodWheelUp)
        {
            return NoteYs[0];
        }

        if (kind == NoteKind.BadWheelDown)
        {
            return NoteYs[NoteYs.Length - 1];
        }

        return GetLaneY(laneIndex);
    }

    private void CreateLongBody(Transform root, NoteKind kind)
    {
        bool bad = kind == NoteKind.BadWheelDown;
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
                return goodTapSprite;
            case NoteKind.GoodWheelUp:
                return goodLongHeadSprite;
            case NoteKind.BadWheelDown:
                return badLongHeadSprite;
            default:
                return badTapSprite;
        }
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
        camera.backgroundColor = new Color(0.12f, 0.66f, 1f, 1f);
    }

    private void SetupStage()
    {
        GameObject stage = new GameObject("Prototype Stage");

        CreateBlock(stage.transform, "Sky", new Vector2(0f, 0.8f), new Vector2(19f, 9.6f), new Color(0.15f, 0.68f, 1f, 1f), -20);
        CreateBlock(stage.transform, "Distant City", new Vector2(1.7f, -1.25f), new Vector2(16.5f, 2.15f), new Color(0.23f, 0.53f, 0.91f, 0.38f), -18);
        CreateBlock(stage.transform, "Park Hill", new Vector2(0f, -3.65f), new Vector2(19f, 1.45f), new Color(0.38f, 0.82f, 0.25f, 1f), -15);
        CreateBlock(stage.transform, "Ground", new Vector2(0f, -4.45f), new Vector2(19f, 1.1f), new Color(0.93f, 0.58f, 0.09f, 1f), -14);
        CreateBlock(stage.transform, "Lane", new Vector2(0f, LaneY), new Vector2(19f, 0.075f), LaneColor, -2);

        float hitLineHeight = Mathf.Abs(NoteYs[NoteYs.Length - 1] - NoteYs[0]) + 1.1f;
        CreateBlock(stage.transform, "Auto Hit Beat Line", new Vector2(HitX, LaneY), new Vector2(0.065f, hitLineHeight), new Color(0.77f, 1f, 1f, 0.82f), 1);

        GameObject ring = new GameObject("Judge Ring");
        ring.transform.SetParent(stage.transform, false);
        ring.transform.position = new Vector3(HitX, LaneY, 0f);
        judgeRing = ring.transform;
        judgeRingRenderer = ring.AddComponent<SpriteRenderer>();
        judgeRingRenderer.sprite = judgeRingSprite;
        judgeRingRenderer.sortingOrder = 10;

        notesRoot = new GameObject("Rhythm Notes").transform;
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
            onDone(request.result == UnityWebRequest.Result.Success);
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

        generatedSongLength = Mathf.Max(4f, generatedSongClip.length);
        BuildChartForMurekaSong(new System.Random(generatedSongSeed));
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

    private void BuildChartForMurekaSong(System.Random rng)
    {
        chart.Clear();

        float bpm = Mathf.Clamp(generatedBpm, 80f, 180f);
        float beatDuration = 60f / bpm;
        float firstNoteTime = Mathf.Max(1.1f, beatDuration * 2f);
        float finalNoteTime = Mathf.Max(firstNoteTime, generatedSongLength - 1.25f);
        int laneCursor = 1;
        int beatIndex = 0;

        for (float noteTime = firstNoteTime; noteTime <= finalNoteTime; noteTime += beatDuration)
        {
            int beatInBar = beatIndex % BeatsPerBar;
            bool strongBeat = beatInBar == 0 || beatInBar == 2;
            double noteChance = strongBeat ? 0.95 : 0.76;

            if (rng.NextDouble() <= noteChance)
            {
                laneCursor = (laneCursor + (rng.Next(2) == 0 ? 1 : 2)) % NoteYs.Length;
                bool isGood = rng.NextDouble() >= 0.45;
                bool isWheel = beatInBar == 3 && rng.NextDouble() <= 0.34;
                NoteKind kind = isWheel
                    ? (isGood ? NoteKind.GoodWheelUp : NoteKind.BadWheelDown)
                    : (isGood ? NoteKind.GoodTap : NoteKind.BadTap);

                TryAddGeneratedNote(noteTime, kind, laneCursor);
            }

            if (beatInBar < BeatsPerBar - 1 && rng.NextDouble() <= 0.18)
            {
                float extraTime = noteTime + beatDuration * 0.5f;
                laneCursor = (laneCursor + 1) % NoteYs.Length;
                bool isGood = rng.NextDouble() >= 0.5;
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

    private static float GetGeneratedNoteGap(NoteKind kind)
    {
        return IsWheelNote(kind) ? MinLongNoteGap : MinTapNoteGap;
    }

    private void PlayGeneratedSong()
    {
        if (musicSource == null || generatedSongClip == null)
        {
            return;
        }

        musicSource.Stop();
        musicSource.clip = generatedSongClip;
        musicSource.PlayDelayed(MusicLeadIn);
    }

    private static void CreateBlock(Transform parent, string name, Vector2 position, Vector2 size, Color color, int sortingOrder)
    {
        GameObject block = new GameObject(name);
        block.transform.SetParent(parent, false);
        block.transform.position = new Vector3(position.x, position.y, 0f);
        block.transform.localScale = new Vector3(size.x, size.y, 1f);

        SpriteRenderer renderer = block.AddComponent<SpriteRenderer>();
        renderer.sprite = whiteSprite;
        renderer.color = color;
        renderer.sortingOrder = sortingOrder;
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

        smallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(16f * Mathf.Clamp(Screen.height / 720f, 0.72f, 1.35f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };
        smallStyle.normal.textColor = WhiteColor;

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

    private static void DrawRect(Rect rect, Color color)
    {
        Color previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, whiteTexture);
        GUI.color = previous;
    }

    private static void EnsureSharedAssets()
    {
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
        rightChevronSprite = CreateRightChevronSprite();
        judgeRingSprite = CreateRingSprite();

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
