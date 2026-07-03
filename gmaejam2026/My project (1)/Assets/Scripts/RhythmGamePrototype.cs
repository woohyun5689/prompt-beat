using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

[DefaultExecutionOrder(-100)]
public sealed class RhythmGamePrototype : MonoBehaviour
{
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
    private const int SongSampleRate = 44100;
    private const int SongBars = 8;
    private const int BeatsPerBar = 4;

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
    private string generatedSongLabel = "AI SONG";
    private int score;
    private int combo;
    private int bestCombo;
    private string judgementText = "READY";
    private Color judgementColor = WhiteColor;
    private float judgementVisibleUntil;
    private GUIStyle titleStyle;
    private GUIStyle numberStyle;
    private GUIStyle smallStyle;
    private GUIStyle judgementStyle;

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
        SetupCamera();
        SetupStage();
        SetupAudio();
        GenerateNewSong();
        RestartChart();
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
            judgementStyle.normal.textColor = judgementColor;
            GUI.Label(new Rect(0f, Screen.height * 0.35f, Screen.width, 82f * scale), judgementText, judgementStyle);
        }

        smallStyle.normal.textColor = new Color(1f, 1f, 1f, 0.75f);
        GUI.Label(new Rect(18f * scale, Screen.height - 64f * scale, 360f * scale, 28f * scale), generatedSongLabel + "  " + Mathf.RoundToInt(generatedBpm) + " BPM", smallStyle);
        GUI.Label(new Rect(18f * scale, Screen.height - 38f * scale, 250f * scale, 28f * scale), "BEST " + bestCombo + "x", smallStyle);
    }

    private void ReadInput()
    {
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

            if (keyboard.rKey.wasPressedThisFrame)
            {
                GenerateNewSong();
                RestartChart();
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
            ApplyMiss(wrongTarget, "WRONG");
            return;
        }

        FlashJudgement("EMPTY", new Color(0.8f, 0.86f, 0.95f, 1f));
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
        string label;
        Color color;
        int points;

        if (delta <= 0.075f)
        {
            label = "PERFECT";
            color = new Color(1f, 0.86f, 0.18f, 1f);
            points = 1000;
        }
        else if (delta <= 0.17f)
        {
            label = "GREAT";
            color = new Color(0.5f, 1f, 0.9f, 1f);
            points = 650;
        }
        else
        {
            label = "HIT";
            color = WhiteColor;
            points = 350;
        }

        combo++;
        bestCombo = Mathf.Max(bestCombo, combo);
        score += points + combo * 12;
        note.Judged = true;
        FlashJudgement(label, color);
        ClearNote(note);
    }

    private void ApplyMiss(Note note, string label)
    {
        combo = 0;
        note.Judged = true;
        FlashJudgement(label, new Color(1f, 0.25f, 0.34f, 1f));
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
                ApplyMiss(note, "MISS");
            }
        }

        if (allJudged && now > songStartTime + chartDuration + 1.1f)
        {
            GenerateNewSong();
            RestartChart();
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
            GenerateNewSong();
        }

        songStartTime = Time.time + MusicLeadIn;
        chartDuration = Mathf.Max(generatedSongLength, chart[chart.Count - 1].Time);

        for (int i = 0; i < chart.Count; i++)
        {
            NoteSpec spec = chart[i];
            CreateNote(spec.Kind, spec.LaneIndex, songStartTime + spec.Time);
        }

        PlayGeneratedSong();
        FlashJudgement("READY", WhiteColor);
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
        if (musicSource != null)
        {
            musicSource.Stop();
        }

        if (generatedSongClip != null)
        {
            Destroy(generatedSongClip);
            generatedSongClip = null;
        }

        generatedSongSeed = UnityEngine.Random.Range(10000, 999999);
        var rng = new System.Random(generatedSongSeed);
        int[] bpmChoices = { 116, 124, 132, 140 };
        string[] songNames = { "AI POP", "AI DASH", "AI SKY", "AI SPARK" };

        generatedBpm = bpmChoices[rng.Next(bpmChoices.Length)];
        generatedSongLabel = songNames[rng.Next(songNames.Length)] + " " + (generatedSongSeed % 1000).ToString("000");
        generatedSongClip = BuildProceduralSong(rng);
    }

    private AudioClip BuildProceduralSong(System.Random rng)
    {
        chart.Clear();

        float beatDuration = 60f / generatedBpm;
        generatedSongLength = SongBars * BeatsPerBar * beatDuration;
        int sampleCount = Mathf.CeilToInt((generatedSongLength + 1.2f) * SongSampleRate);
        float[] samples = new float[sampleCount];

        int[] rootOptions = { 55, 57, 60, 62, 64 };
        int[] melodyOffsets = { 0, 2, 4, 7, 9, 12, 14, 16 };
        int[][] progressions =
        {
            new[] { 0, 7, 9, 5 },
            new[] { 0, 5, 7, 0 },
            new[] { 0, 9, 5, 7 },
        };

        int rootMidi = rootOptions[rng.Next(rootOptions.Length)];
        int[] progression = progressions[rng.Next(progressions.Length)];
        int laneCursor = 1;

        for (int bar = 0; bar < SongBars; bar++)
        {
            float barStart = bar * BeatsPerBar * beatDuration;
            int chordOffset = progression[bar % progression.Length];

            AddTone(samples, barStart, beatDuration * 3.85f, MidiToFrequency(rootMidi + chordOffset - 12), 0.055f, 0);
            AddTone(samples, barStart, beatDuration * 3.85f, MidiToFrequency(rootMidi + chordOffset), 0.035f, 0);
            AddTone(samples, barStart, beatDuration * 3.85f, MidiToFrequency(rootMidi + chordOffset + 7), 0.03f, 0);

            for (int beat = 0; beat < BeatsPerBar; beat++)
            {
                float noteTime = barStart + beat * beatDuration;

                if (beat == 0 || beat == 2)
                {
                    AddKick(samples, noteTime, 0.55f);
                    AddTone(samples, noteTime, beatDuration * 0.42f, MidiToFrequency(rootMidi + chordOffset - 24), 0.11f, 1);
                }
                else
                {
                    AddSnare(samples, noteTime, rng, 0.28f);
                }

                AddHat(samples, noteTime, rng, 0.12f);
                AddHat(samples, noteTime + beatDuration * 0.5f, rng, 0.08f);

                if (bar == 0 && beat == 0)
                {
                    continue;
                }

                if (rng.NextDouble() <= 0.88f)
                {
                    laneCursor = (laneCursor + (rng.Next(2) == 0 ? 1 : 2)) % NoteYs.Length;
                    bool isGood = rng.NextDouble() >= 0.44f;
                    bool isWheel = beat == 3 && rng.NextDouble() <= 0.38f;
                    NoteKind kind = isWheel
                        ? (isGood ? NoteKind.GoodWheelUp : NoteKind.BadWheelDown)
                        : (isGood ? NoteKind.GoodTap : NoteKind.BadTap);

                    if (TryAddGeneratedNote(noteTime, kind, laneCursor))
                    {
                        int melodyMidi = rootMidi + 12 + chordOffset + melodyOffsets[rng.Next(melodyOffsets.Length)];
                        float melodyFrequency = MidiToFrequency(melodyMidi);
                        if (isWheel)
                        {
                            float fromFrequency = isGood ? melodyFrequency * 0.72f : melodyFrequency * 1.3f;
                            float toFrequency = isGood ? melodyFrequency * 1.34f : melodyFrequency * 0.62f;
                            AddSweep(samples, noteTime, beatDuration * 1.08f, fromFrequency, toFrequency, isGood ? 0.2f : 0.18f);
                        }
                        else
                        {
                            AddTone(samples, noteTime, beatDuration * 0.44f, melodyFrequency, isGood ? 0.2f : 0.17f, isGood ? 0 : 1);
                        }
                    }
                }

                if (beat < BeatsPerBar - 1 && rng.NextDouble() <= 0.22f)
                {
                    float extraTime = noteTime + beatDuration * 0.5f;
                    laneCursor = (laneCursor + 1) % NoteYs.Length;
                    bool isGood = rng.NextDouble() >= 0.5f;
                    NoteKind kind = isGood ? NoteKind.GoodTap : NoteKind.BadTap;
                    if (TryAddGeneratedNote(extraTime, kind, laneCursor))
                    {
                        int melodyMidi = rootMidi + 19 + chordOffset + melodyOffsets[rng.Next(melodyOffsets.Length)];
                        AddTone(samples, extraTime, beatDuration * 0.28f, MidiToFrequency(melodyMidi), isGood ? 0.16f : 0.14f, isGood ? 0 : 1);
                    }
                }
            }
        }

        if (chart.Count == 0)
        {
            chart.Add(new NoteSpec(beatDuration, NoteKind.GoodTap, 1));
        }

        NormalizeSong(samples);

        AudioClip clip = AudioClip.Create(generatedSongLabel, samples.Length, 1, SongSampleRate, false);
        clip.SetData(samples, 0);
        return clip;
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

        chart.Add(new NoteSpec(noteTime, kind, laneIndex));
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

    private static void AddTone(float[] samples, float startTime, float duration, float frequency, float volume, int waveMode)
    {
        int start = Mathf.Clamp(Mathf.FloorToInt(startTime * SongSampleRate), 0, samples.Length - 1);
        int end = Mathf.Clamp(Mathf.CeilToInt((startTime + duration) * SongSampleRate), start + 1, samples.Length);

        for (int i = start; i < end; i++)
        {
            float time = (i - start) / (float)SongSampleRate;
            float phase = time * frequency * Mathf.PI * 2f;
            float wave = Mathf.Sin(phase);
            if (waveMode == 1)
            {
                wave = wave * 0.65f + Mathf.Sign(wave) * 0.35f;
            }

            samples[i] += wave * GetEnvelope(time, duration, 0.012f, 0.08f) * volume;
        }
    }

    private static void AddSweep(float[] samples, float startTime, float duration, float fromFrequency, float toFrequency, float volume)
    {
        int start = Mathf.Clamp(Mathf.FloorToInt(startTime * SongSampleRate), 0, samples.Length - 1);
        int end = Mathf.Clamp(Mathf.CeilToInt((startTime + duration) * SongSampleRate), start + 1, samples.Length);

        for (int i = start; i < end; i++)
        {
            float time = (i - start) / (float)SongSampleRate;
            float t = Mathf.Clamp01(time / duration);
            float frequency = Mathf.Lerp(fromFrequency, toFrequency, t);
            float wave = Mathf.Sin(time * frequency * Mathf.PI * 2f);
            samples[i] += wave * GetEnvelope(time, duration, 0.02f, 0.12f) * volume;
        }
    }

    private static void AddKick(float[] samples, float startTime, float volume)
    {
        float duration = 0.34f;
        int start = Mathf.Clamp(Mathf.FloorToInt(startTime * SongSampleRate), 0, samples.Length - 1);
        int end = Mathf.Clamp(Mathf.CeilToInt((startTime + duration) * SongSampleRate), start + 1, samples.Length);

        for (int i = start; i < end; i++)
        {
            float time = (i - start) / (float)SongSampleRate;
            float envelope = Mathf.Exp(-time * 8.5f);
            float frequency = Mathf.Lerp(42f, 96f, Mathf.Exp(-time * 11f));
            samples[i] += Mathf.Sin(time * frequency * Mathf.PI * 2f) * envelope * volume;
        }
    }

    private static void AddSnare(float[] samples, float startTime, System.Random rng, float volume)
    {
        float duration = 0.18f;
        int start = Mathf.Clamp(Mathf.FloorToInt(startTime * SongSampleRate), 0, samples.Length - 1);
        int end = Mathf.Clamp(Mathf.CeilToInt((startTime + duration) * SongSampleRate), start + 1, samples.Length);

        for (int i = start; i < end; i++)
        {
            float time = (i - start) / (float)SongSampleRate;
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            float tone = Mathf.Sin(time * 185f * Mathf.PI * 2f) * 0.35f;
            samples[i] += (noise * 0.65f + tone) * Mathf.Exp(-time * 18f) * volume;
        }
    }

    private static void AddHat(float[] samples, float startTime, System.Random rng, float volume)
    {
        float duration = 0.075f;
        int start = Mathf.Clamp(Mathf.FloorToInt(startTime * SongSampleRate), 0, samples.Length - 1);
        int end = Mathf.Clamp(Mathf.CeilToInt((startTime + duration) * SongSampleRate), start + 1, samples.Length);

        for (int i = start; i < end; i++)
        {
            float time = (i - start) / (float)SongSampleRate;
            float noise = (float)(rng.NextDouble() * 2.0 - 1.0);
            samples[i] += noise * Mathf.Exp(-time * 35f) * volume;
        }
    }

    private static void NormalizeSong(float[] samples)
    {
        float peak = 0f;
        for (int i = 0; i < samples.Length; i++)
        {
            peak = Mathf.Max(peak, Mathf.Abs(samples[i]));
        }

        if (peak <= 0.01f)
        {
            return;
        }

        float gain = Mathf.Min(0.92f / peak, 1.35f);
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Mathf.Clamp(samples[i] * gain, -0.98f, 0.98f);
        }
    }

    private static float GetEnvelope(float time, float duration, float attack, float release)
    {
        float attackGain = attack > 0f ? Mathf.Clamp01(time / attack) : 1f;
        float releaseGain = release > 0f ? Mathf.Clamp01((duration - time) / release) : 1f;
        return Mathf.Min(attackGain, releaseGain);
    }

    private static float MidiToFrequency(int midiNote)
    {
        return 440f * Mathf.Pow(2f, (midiNote - 69) / 12f);
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

    private void FlashJudgement(string label, Color color)
    {
        judgementText = label;
        judgementColor = color;
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

        judgementStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(56f * Mathf.Clamp(Screen.height / 720f, 0.72f, 1.35f)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter
        };
        judgementStyle.normal.textColor = WhiteColor;
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
