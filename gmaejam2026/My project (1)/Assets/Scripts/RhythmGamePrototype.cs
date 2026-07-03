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
    private readonly NoteSpec[] chart =
    {
        new NoteSpec(0.85f, NoteKind.BadTap, 1),
        new NoteSpec(1.45f, NoteKind.GoodTap, 2),
        new NoteSpec(2.05f, NoteKind.BadTap, 0),
        new NoteSpec(2.65f, NoteKind.GoodTap, 1),
        new NoteSpec(3.35f, NoteKind.BadWheelDown, 2),
        new NoteSpec(4.35f, NoteKind.GoodTap, 0),
        new NoteSpec(5.05f, NoteKind.GoodWheelUp, 1),
        new NoteSpec(6.25f, NoteKind.BadTap, 2),
        new NoteSpec(6.9f, NoteKind.GoodTap, 0),
        new NoteSpec(7.55f, NoteKind.BadWheelDown, 1),
        new NoteSpec(8.7f, NoteKind.GoodWheelUp, 0),
        new NoteSpec(9.9f, NoteKind.BadTap, 2),
        new NoteSpec(10.55f, NoteKind.GoodTap, 1),
        new NoteSpec(11.2f, NoteKind.BadTap, 0),
        new NoteSpec(11.9f, NoteKind.GoodWheelUp, 2),
    };

    private Transform notesRoot;
    private Transform judgeRing;
    private SpriteRenderer judgeRingRenderer;
    private float songStartTime;
    private float chartDuration;
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
        RestartChart();
    }

    private void Update()
    {
        ReadInput();
        UpdateNotes();
        UpdateJudgeRing();
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
        songStartTime = Time.time + 2.1f;
        chartDuration = chart[chart.Length - 1].Time;

        for (int i = 0; i < chart.Length; i++)
        {
            NoteSpec spec = chart[i];
            CreateNote(spec.Kind, spec.LaneIndex, songStartTime + spec.Time);
        }

        FlashJudgement("READY", WhiteColor);
    }

    private void CreateNote(NoteKind kind, int laneIndex, float hitTime)
    {
        float laneY = GetLaneY(laneIndex);
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

    private void CreateLongBody(Transform root, NoteKind kind)
    {
        bool bad = kind == NoteKind.BadWheelDown;
        Vector2 direction = bad ? new Vector2(0.78f, -0.62f).normalized : new Vector2(0.66f, 0.75f).normalized;
        float length = 2.65f;
        Color bodyColor = bad ? BadColor : GoodColor;
        Color darkColor = bad ? BadDarkColor : GoodDarkColor;
        float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;

        CreateLine(root, "Long Outline", direction, length, 0.78f, WhiteColor, 5);
        CreateLine(root, "Long Shadow", direction, length, 0.58f, darkColor, 6);
        CreateLine(root, "Long Fill", direction, length, 0.48f, bodyColor, 7);

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
