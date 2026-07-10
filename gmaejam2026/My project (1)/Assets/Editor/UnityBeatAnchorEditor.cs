using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

public sealed class UnityBeatAnchorEditor : EditorWindow
{
    [Serializable]
    private sealed class ChartHeader
    {
        public string songName = string.Empty;
        public int sampleRate = 0;
        public long totalSamples = 0;
        public float bpm = 0f;
        public long firstBeatSample = 0;
        public long[] beatSamples = Array.Empty<long>();
    }

    [Serializable]
    private sealed class BeatAnchor
    {
        public int beatIndex;
        public long sample;

        public BeatAnchor Clone()
        {
            return new BeatAnchor { beatIndex = beatIndex, sample = sample };
        }
    }

    [Serializable]
    private sealed class BeatAnchorMap
    {
        public int version = 1;
        public string chartKey;
        public string songName;
        public string audioAssetPath;
        public string audioSha256;
        public int sampleRate;
        public long totalSamples;
        public int beatsPerBar = 4;
        public BeatAnchor[] anchors;
    }

    private sealed class SongEntry
    {
        public string ChartKey;
        public string ChartAssetPath;
        public string AudioAssetPath;
        public ChartHeader Chart;
        public AudioClip Clip;
    }

    private const string MusicFolder = "Assets/Resources/Music";
    private const string ChartFolder = "Assets/Resources/NoteCharts";
    private const string AnchorFolder = "Assets/BeatAnchorMaps";
    private const int BeatsPerBar = 4;
    private const int WaveformBins = 131072;
    private const int AudioChunkFrames = 65536;
    private const string SelectedSongPreference = "RhythmBeatAnchorEditor.SelectedSong";

    private readonly List<SongEntry> songs = new List<SongEntry>();
    private readonly List<BeatAnchor> anchors = new List<BeatAnchor>();
    private readonly Queue<string> pendingGeneratorLines = new Queue<string>();
    private readonly List<string> generatorLines = new List<string>();
    private readonly object generatorOutputLock = new object();

    private int selectedSongIndex = -1;
    private int selectedAnchorIndex = -1;
    private int nextAnchorBeat = BeatsPerBar;
    private long playheadSample;
    private float zoomExponent;
    private float scrollNormalized;
    private bool anchorsDirty;
    private bool previewActive;
    private bool previewPaused;
    private double previewStartedAt;
    private string status = "Ready.";
    private Vector2 anchorScroll;
    private Vector2 generatorScroll;

    private float[] monoSamples;
    private float[] overviewMinimum;
    private float[] overviewMaximum;
    private int overviewBlockSize = 1;
    private Process generatorProcess;

    [MenuItem("Tools/Rhythm/Beat Anchor Editor %#k")]
    private static void OpenWindow()
    {
        UnityBeatAnchorEditor window = GetWindow<UnityBeatAnchorEditor>();
        window.titleContent = new GUIContent("Beat Anchors");
        window.minSize = new Vector2(840f, 620f);
        window.Show();
    }

    private SongEntry CurrentSong
    {
        get
        {
            return selectedSongIndex >= 0 && selectedSongIndex < songs.Count
                ? songs[selectedSongIndex]
                : null;
        }
    }

    private void OnEnable()
    {
        titleContent = new GUIContent("Beat Anchors");
        minSize = new Vector2(840f, 620f);
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.update += OnEditorUpdate;
        ReloadSongList();
    }

    private void OnDisable()
    {
        EditorApplication.update -= OnEditorUpdate;
        StopPreview();
        monoSamples = null;
        overviewMinimum = null;
        overviewMaximum = null;
        if (generatorProcess != null && generatorProcess.HasExited)
        {
            generatorProcess.Dispose();
            generatorProcess = null;
        }
    }

    private void OnEditorUpdate()
    {
        if (previewActive && !previewPaused)
        {
            bool isPlaying = AudioPreview.IsPlaying();
            if (isPlaying)
            {
                int sample = AudioPreview.GetSamplePosition();
                SongEntry song = CurrentSong;
                if (sample >= 0 && song != null && song.Clip != null)
                {
                    playheadSample = ClampSample(sample, song.Clip.samples);
                    KeepPlayheadVisible();
                    Repaint();
                }
            }
            else if (EditorApplication.timeSinceStartup - previewStartedAt >= 0.35)
            {
                previewActive = false;
                previewPaused = false;
                Repaint();
            }
        }

        DrainGeneratorOutput();
        if (generatorProcess != null && generatorProcess.HasExited)
        {
            int exitCode = generatorProcess.ExitCode;
            generatorProcess.Dispose();
            generatorProcess = null;
            AssetDatabase.Refresh();
            status = exitCode == 0
                ? "Chart generation completed."
                : "Chart generation failed with exit code " + exitCode + ".";
            Repaint();
        }
    }

    private void OnGUI()
    {
        HandleKeyboard();
        DrawSongToolbar();

        SongEntry song = CurrentSong;
        if (song == null || song.Clip == null)
        {
            EditorGUILayout.HelpBox("No matching Music AudioClip and NoteChart JSON were found.", MessageType.Warning);
            return;
        }

        DrawPlaybackControls(song);
        DrawViewControls(song);
        DrawWaveform(song);
        DrawAnchorControls(song);
        DrawAnchorTable(song);
        DrawFooter(song);
    }

    private void DrawSongToolbar()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            string[] names = new string[songs.Count];
            for (int i = 0; i < songs.Count; i++)
            {
                names[i] = songs[i].Chart.songName;
            }

            EditorGUI.BeginChangeCheck();
            int newIndex = EditorGUILayout.Popup(selectedSongIndex, names, EditorStyles.toolbarPopup);
            if (EditorGUI.EndChangeCheck())
            {
                SelectSong(newIndex);
                GUIUtility.ExitGUI();
            }

            GUILayout.FlexibleSpace();
            GUI.enabled = generatorProcess == null;
            if (GUILayout.Button(new GUIContent("Reload", "Reload songs and anchor sidecars."), EditorStyles.toolbarButton, GUILayout.Width(62f)))
            {
                ReloadSongList();
                GUIUtility.ExitGUI();
            }
            GUI.enabled = true;
        }
    }

    private void DrawPlaybackControls(SongEntry song)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button(new GUIContent("Play", "Play from the current sample."), GUILayout.Width(54f)))
            {
                PlayPreview(song);
            }
            if (GUILayout.Button(new GUIContent(previewPaused ? "Resume" : "Pause", "Pause or resume preview audio."), GUILayout.Width(62f)))
            {
                TogglePause();
            }
            if (GUILayout.Button(new GUIContent("Stop", "Stop preview audio."), GUILayout.Width(54f)))
            {
                StopPreview();
            }

            EditorGUI.BeginChangeCheck();
            long sample = EditorGUILayout.LongField("Sample", playheadSample, GUILayout.MinWidth(220f));
            if (EditorGUI.EndChangeCheck())
            {
                SetPlayhead(sample, true);
            }

            EditorGUI.BeginChangeCheck();
            double seconds = EditorGUILayout.DoubleField(
                "Seconds",
                playheadSample / (double)Mathf.Max(1, song.Clip.frequency),
                GUILayout.MinWidth(180f));
            if (EditorGUI.EndChangeCheck())
            {
                SetPlayhead((long)Math.Round(seconds * song.Clip.frequency), true);
            }
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label("Nudge", GUILayout.Width(44f));
            int[] amounts = { -100, -10, -1, 1, 10, 100 };
            for (int i = 0; i < amounts.Length; i++)
            {
                int amount = amounts[i];
                string label = amount > 0 ? "+" + amount : amount.ToString();
                if (GUILayout.Button(new GUIContent(label, "Move the playhead by samples."), GUILayout.Width(46f)))
                {
                    SetPlayhead(playheadSample + amount, true);
                }
            }
            if (GUILayout.Button(new GUIContent("Center", "Center the waveform on the playhead."), GUILayout.Width(62f)))
            {
                CenterViewOnPlayhead();
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                song.Clip.frequency + " Hz  |  " + song.Clip.samples + " samples  |  " +
                song.Chart.bpm.ToString("0.######") + " BPM",
                EditorStyles.miniLabel);
        }
    }

    private void DrawViewControls(SongEntry song)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUI.BeginChangeCheck();
            float newZoom = EditorGUILayout.Slider("Zoom", zoomExponent, 0f, 16f);
            if (EditorGUI.EndChangeCheck())
            {
                SetZoomAroundSample(newZoom, playheadSample);
            }

            long visibleSamples = GetVisibleSampleCount(song);
            long maximumStart = Math.Max(0L, song.Clip.samples - visibleSamples);
            GUI.enabled = maximumStart > 0;
            scrollNormalized = GUILayout.HorizontalSlider(scrollNormalized, 0f, 1f, GUILayout.MinWidth(180f));
            GUI.enabled = true;
        }
    }

    private void DrawWaveform(SongEntry song)
    {
        Rect rect = GUILayoutUtility.GetRect(100f, 258f, GUILayout.ExpandWidth(true));
        EditorGUI.DrawRect(rect, new Color(0.055f, 0.065f, 0.08f, 1f));
        if (monoSamples == null || monoSamples.Length == 0)
        {
            GUI.Label(rect, "Waveform unavailable.", EditorStyles.centeredGreyMiniLabel);
            return;
        }

        long visibleSamples = GetVisibleSampleCount(song);
        long viewStart = GetViewStartSample(song, visibleSamples);
        long viewEnd = Math.Min(song.Clip.samples, viewStart + visibleSamples);
        DrawBeatGrid(rect, song, viewStart, viewEnd);
        DrawWaveformLines(rect, viewStart, viewEnd);
        DrawAnchorMarkers(rect, song, viewStart, viewEnd);
        DrawPlayhead(rect, viewStart, viewEnd);

        GUI.Label(
            new Rect(rect.x + 6f, rect.y + 4f, 240f, 18f),
            FormatTime(viewStart, song.Clip.frequency),
            EditorStyles.miniLabel);
        GUI.Label(
            new Rect(rect.xMax - 246f, rect.y + 4f, 240f, 18f),
            FormatTime(viewEnd, song.Clip.frequency),
            new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperRight });

        HandleWaveformInput(rect, song, viewStart, viewEnd);
    }

    private void DrawWaveformLines(Rect rect, long viewStart, long viewEnd)
    {
        int pixelCount = Mathf.Max(1, Mathf.FloorToInt(rect.width));
        float middle = rect.center.y;
        float amplitude = rect.height * 0.43f;
        Handles.BeginGUI();
        Handles.color = new Color(0.24f, 0.82f, 1f, 0.92f);
        for (int pixel = 0; pixel < pixelCount; pixel++)
        {
            double firstT = pixel / (double)pixelCount;
            double lastT = (pixel + 1) / (double)pixelCount;
            long first = viewStart + (long)Math.Floor((viewEnd - viewStart) * firstT);
            long last = viewStart + (long)Math.Ceiling((viewEnd - viewStart) * lastT);
            GetWaveRange(first, Math.Max(first + 1, last), out float minimum, out float maximum);
            float x = rect.x + pixel + 0.5f;
            Handles.DrawLine(
                new Vector3(x, middle - maximum * amplitude),
                new Vector3(x, middle - minimum * amplitude));
        }
        Handles.EndGUI();
    }

    private void DrawBeatGrid(Rect rect, SongEntry song, long viewStart, long viewEnd)
    {
        int subdivisions = zoomExponent >= 10f ? 4 : zoomExponent >= 6f ? 2 : 1;
        int maximumBeats = EstimateBeatCount(song);
        Handles.BeginGUI();
        for (int tick = 0; tick <= maximumBeats * subdivisions; tick++)
        {
            double beat = tick / (double)subdivisions;
            long sample = (long)Math.Round(EvaluateBeatSample(song, beat));
            if (sample < viewStart || sample > viewEnd)
            {
                continue;
            }

            bool isBeat = tick % subdivisions == 0;
            bool isBar = tick % (BeatsPerBar * subdivisions) == 0;
            Handles.color = isBar
                ? new Color(1f, 0.32f, 0.72f, 0.58f)
                : isBeat
                    ? new Color(0.48f, 0.56f, 0.72f, 0.34f)
                    : new Color(0.40f, 0.46f, 0.56f, 0.18f);
            float x = SampleToX(rect, sample, viewStart, viewEnd);
            Handles.DrawLine(new Vector3(x, rect.y), new Vector3(x, rect.yMax));
        }
        Handles.EndGUI();
    }

    private void DrawAnchorMarkers(Rect rect, SongEntry song, long viewStart, long viewEnd)
    {
        Handles.BeginGUI();
        for (int i = 0; i < anchors.Count; i++)
        {
            BeatAnchor anchor = anchors[i];
            if (anchor.sample < viewStart || anchor.sample > viewEnd)
            {
                continue;
            }

            float x = SampleToX(rect, anchor.sample, viewStart, viewEnd);
            Handles.color = i == selectedAnchorIndex
                ? new Color(1f, 0.88f, 0.18f, 1f)
                : new Color(1f, 0.55f, 0.12f, 0.95f);
            Handles.DrawAAPolyLine(2.5f, new Vector3(x, rect.y), new Vector3(x, rect.yMax));
            GUI.Label(
                new Rect(x + 3f, rect.y + 18f, 88f, 18f),
                "B" + anchor.beatIndex,
                EditorStyles.miniBoldLabel);
        }
        Handles.EndGUI();
    }

    private void DrawPlayhead(Rect rect, long viewStart, long viewEnd)
    {
        if (playheadSample < viewStart || playheadSample > viewEnd)
        {
            return;
        }
        float x = SampleToX(rect, playheadSample, viewStart, viewEnd);
        Handles.BeginGUI();
        Handles.color = new Color(1f, 0.94f, 0.22f, 1f);
        Handles.DrawAAPolyLine(2f, new Vector3(x, rect.y), new Vector3(x, rect.yMax));
        Handles.EndGUI();
    }

    private void HandleWaveformInput(Rect rect, SongEntry song, long viewStart, long viewEnd)
    {
        Event current = Event.current;
        if (!rect.Contains(current.mousePosition))
        {
            return;
        }

        if (current.type == EventType.ScrollWheel)
        {
            float fraction = Mathf.Clamp01((current.mousePosition.x - rect.x) / rect.width);
            long focusSample = viewStart + (long)Math.Round((viewEnd - viewStart) * fraction);
            SetZoomAroundSample(zoomExponent - current.delta.y * 0.18f, focusSample, fraction);
            current.Use();
            return;
        }

        if ((current.type == EventType.MouseDown || current.type == EventType.MouseDrag) && current.button == 0)
        {
            float fraction = Mathf.Clamp01((current.mousePosition.x - rect.x) / rect.width);
            long sample = viewStart + (long)Math.Round((viewEnd - viewStart) * fraction);
            SetPlayhead(sample, true);
            nextAnchorBeat = SuggestBarBeat(song, playheadSample);
            if (current.shift && current.type == EventType.MouseDown)
            {
                AddOrReplaceAnchor(nextAnchorBeat, playheadSample);
            }
            current.Use();
        }
    }

    private void DrawAnchorControls(SongEntry song)
    {
        EditorGUILayout.Space(4f);
        using (new EditorGUILayout.HorizontalScope())
        {
            nextAnchorBeat = Mathf.Max(0, EditorGUILayout.IntField("Beat Index", nextAnchorBeat, GUILayout.Width(180f)));
            if (GUILayout.Button(new GUIContent("Set First Beat", "Set beat 0 to the current sample."), GUILayout.Width(104f)))
            {
                AddOrReplaceAnchor(0, playheadSample);
            }
            if (GUILayout.Button(new GUIContent("Add Bar Anchor", "Add or replace the selected bar beat at the current sample."), GUILayout.Width(112f)))
            {
                nextAnchorBeat = Mathf.Max(0, Mathf.RoundToInt(nextAnchorBeat / (float)BeatsPerBar) * BeatsPerBar);
                AddOrReplaceAnchor(nextAnchorBeat, playheadSample);
                nextAnchorBeat += BeatsPerBar;
            }
            if (GUILayout.Button(new GUIContent("Seed First/Last", "Reset to the current chart's first and last complete bar anchors."), GUILayout.Width(108f)))
            {
                if (!anchorsDirty || EditorUtility.DisplayDialog("Reset anchors", "Replace unsaved anchors with the current chart grid?", "Replace", "Cancel"))
                {
                    SeedAnchorsFromChart(song);
                }
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label("Bar " + (nextAnchorBeat / BeatsPerBar + 1), EditorStyles.miniBoldLabel);
        }
    }

    private void DrawAnchorTable(SongEntry song)
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.Label("", GUILayout.Width(20f));
            GUILayout.Label("Beat", GUILayout.Width(64f));
            GUILayout.Label("Sample", GUILayout.Width(145f));
            GUILayout.Label("Time", GUILayout.Width(88f));
            GUILayout.Label("BPM to next", GUILayout.Width(92f));
            GUILayout.FlexibleSpace();
        }

        anchorScroll = EditorGUILayout.BeginScrollView(anchorScroll, GUILayout.MinHeight(120f), GUILayout.MaxHeight(205f));
        for (int i = 0; i < anchors.Count; i++)
        {
            BeatAnchor anchor = anchors[i];
            using (new EditorGUILayout.HorizontalScope())
            {
                bool selected = GUILayout.Toggle(selectedAnchorIndex == i, GUIContent.none, GUILayout.Width(20f));
                if (selected)
                {
                    selectedAnchorIndex = i;
                }

                EditorGUI.BeginChangeCheck();
                int beatIndex = EditorGUILayout.IntField(anchor.beatIndex, GUILayout.Width(64f));
                long sample = EditorGUILayout.LongField(anchor.sample, GUILayout.Width(145f));
                if (EditorGUI.EndChangeCheck())
                {
                    anchor.beatIndex = Mathf.Max(0, beatIndex);
                    anchor.sample = ClampSample(sample, song.Clip.samples);
                    anchorsDirty = true;
                    SortAnchors(anchor);
                }

                GUILayout.Label(FormatTime(anchor.sample, song.Clip.frequency), GUILayout.Width(88f));
                string segmentBpm = i + 1 < anchors.Count
                    ? CalculateSegmentBpm(anchor, anchors[i + 1], song.Clip.frequency).ToString("0.######")
                    : "-";
                GUILayout.Label(segmentBpm, GUILayout.Width(92f));
                GUILayout.FlexibleSpace();

                if (GUILayout.Button(new GUIContent("Go", "Move the playhead to this anchor."), GUILayout.Width(36f)))
                {
                    selectedAnchorIndex = i;
                    SetPlayhead(anchor.sample, true);
                    CenterViewOnPlayhead();
                }
                if (GUILayout.Button(new GUIContent("Set", "Set this anchor to the current sample."), GUILayout.Width(38f)))
                {
                    anchor.sample = playheadSample;
                    selectedAnchorIndex = i;
                    anchorsDirty = true;
                    SortAnchors(anchor);
                }
                if (GUILayout.Button(new GUIContent("X", "Delete this anchor."), GUILayout.Width(24f)))
                {
                    anchors.RemoveAt(i);
                    selectedAnchorIndex = -1;
                    anchorsDirty = true;
                    GUIUtility.ExitGUI();
                }
            }
        }
        EditorGUILayout.EndScrollView();
    }

    private void DrawFooter(SongEntry song)
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.enabled = generatorProcess == null;
            if (GUILayout.Button(new GUIContent("Save Anchors", "Validate and save the sidecar JSON."), GUILayout.Width(104f)))
            {
                SaveAnchors(song);
            }
            if (GUILayout.Button(new GUIContent("Save & Rebuild Charts", "Export Unity PCM and regenerate all note charts in the background."), GUILayout.Width(154f)))
            {
                StartChartGeneration(song);
            }
            GUI.enabled = true;
            GUILayout.FlexibleSpace();
            GUILayout.Label(anchorsDirty ? "Unsaved" : "Saved", anchorsDirty ? EditorStyles.boldLabel : EditorStyles.miniLabel);
        }

        MessageType messageType = status.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  status.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0
            ? MessageType.Error
            : MessageType.Info;
        EditorGUILayout.HelpBox(status, messageType);

        if (generatorLines.Count > 0)
        {
            generatorScroll = EditorGUILayout.BeginScrollView(generatorScroll, GUILayout.Height(70f));
            int first = Mathf.Max(0, generatorLines.Count - 8);
            for (int i = first; i < generatorLines.Count; i++)
            {
                GUILayout.Label(generatorLines[i], EditorStyles.miniLabel);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void HandleKeyboard()
    {
        Event current = Event.current;
        if (current.type != EventType.KeyDown || EditorGUIUtility.editingTextField)
        {
            return;
        }

        if (current.keyCode == KeyCode.Space)
        {
            if (previewActive)
            {
                TogglePause();
            }
            else if (CurrentSong != null)
            {
                PlayPreview(CurrentSong);
            }
            current.Use();
        }
        else if (current.keyCode == KeyCode.LeftArrow)
        {
            SetPlayhead(playheadSample - (current.shift ? 10 : 1), true);
            current.Use();
        }
        else if (current.keyCode == KeyCode.RightArrow)
        {
            SetPlayhead(playheadSample + (current.shift ? 10 : 1), true);
            current.Use();
        }
    }

    private void ReloadSongList()
    {
        string previousKey = CurrentSong != null ? CurrentSong.ChartKey : string.Empty;
        songs.Clear();

        Dictionary<string, AudioClip> clipsByName = new Dictionary<string, AudioClip>(StringComparer.Ordinal);
        string[] audioGuids = AssetDatabase.FindAssets("t:AudioClip", new[] { MusicFolder });
        for (int i = 0; i < audioGuids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(audioGuids[i]);
            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path);
            if (clip != null)
            {
                clipsByName[clip.name] = clip;
            }
        }

        string[] chartGuids = AssetDatabase.FindAssets("t:TextAsset", new[] { ChartFolder });
        for (int i = 0; i < chartGuids.Length; i++)
        {
            string path = AssetDatabase.GUIDToAssetPath(chartGuids[i]);
            if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetDirectoryName(path).Replace('\\', '/'), ChartFolder, StringComparison.Ordinal))
            {
                continue;
            }

            TextAsset asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
            ChartHeader chart = asset != null ? JsonUtility.FromJson<ChartHeader>(asset.text) : null;
            if (chart == null || string.IsNullOrWhiteSpace(chart.songName) || !clipsByName.TryGetValue(chart.songName, out AudioClip clip))
            {
                continue;
            }

            songs.Add(new SongEntry
            {
                ChartKey = Path.GetFileNameWithoutExtension(path),
                ChartAssetPath = path,
                AudioAssetPath = AssetDatabase.GetAssetPath(clip),
                Chart = chart,
                Clip = clip
            });
        }

        songs.Sort((left, right) => string.Compare(left.ChartKey, right.ChartKey, StringComparison.Ordinal));
        int preferredIndex = EditorPrefs.GetInt(SelectedSongPreference, 0);
        int index = songs.Count > 0 ? Mathf.Clamp(preferredIndex, 0, songs.Count - 1) : -1;
        if (!string.IsNullOrEmpty(previousKey))
        {
            int previousIndex = songs.FindIndex(song => song.ChartKey == previousKey);
            if (previousIndex >= 0)
            {
                index = previousIndex;
            }
        }
        SelectSong(index);
    }

    private void SelectSong(int index)
    {
        StopPreview();
        selectedSongIndex = index >= 0 && index < songs.Count ? index : -1;
        selectedAnchorIndex = -1;
        anchors.Clear();
        monoSamples = null;
        overviewMinimum = null;
        overviewMaximum = null;
        zoomExponent = 0f;
        scrollNormalized = 0f;

        SongEntry song = CurrentSong;
        if (song == null)
        {
            status = "No songs found.";
            return;
        }

        EditorPrefs.SetInt(SelectedSongPreference, selectedSongIndex);
        LoadWaveform(song.Clip);
        if (!LoadAnchorSidecar(song))
        {
            SeedAnchorsFromChart(song);
            anchorsDirty = false;
            status = "Loaded chart-derived first and last bar anchors.";
        }
        playheadSample = anchors.Count > 0 ? anchors[0].sample : song.Chart.firstBeatSample;
        nextAnchorBeat = BeatsPerBar;
        CenterViewOnPlayhead();
        Repaint();
    }

    private void LoadWaveform(AudioClip clip)
    {
        try
        {
            EditorUtility.DisplayProgressBar("Beat Anchor Editor", "Reading " + clip.name, 0f);
            if (clip.loadState != AudioDataLoadState.Loaded && !clip.LoadAudioData())
            {
                throw new InvalidOperationException("Could not load AudioClip data: " + clip.name);
            }

            int channels = Mathf.Max(1, clip.channels);
            monoSamples = new float[clip.samples];
            for (int offset = 0; offset < clip.samples; offset += AudioChunkFrames)
            {
                int frameCount = Mathf.Min(AudioChunkFrames, clip.samples - offset);
                float[] interleaved = new float[frameCount * channels];
                if (!clip.GetData(interleaved, offset))
                {
                    throw new InvalidOperationException("Could not read AudioClip samples: " + clip.name);
                }

                for (int frame = 0; frame < frameCount; frame++)
                {
                    float mono = 0f;
                    int frameStart = frame * channels;
                    for (int channel = 0; channel < channels; channel++)
                    {
                        mono += interleaved[frameStart + channel];
                    }
                    monoSamples[offset + frame] = mono / channels;
                }
                EditorUtility.DisplayProgressBar(
                    "Beat Anchor Editor",
                    "Reading " + clip.name,
                    (offset + frameCount) / (float)Mathf.Max(1, clip.samples));
            }
            BuildWaveformOverview();
        }
        catch (Exception exception)
        {
            monoSamples = null;
            overviewMinimum = null;
            overviewMaximum = null;
            status = exception.Message;
            UnityEngine.Debug.LogException(exception);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private void BuildWaveformOverview()
    {
        if (monoSamples == null || monoSamples.Length == 0)
        {
            return;
        }

        overviewBlockSize = Mathf.Max(1, Mathf.CeilToInt(monoSamples.Length / (float)WaveformBins));
        int binCount = Mathf.CeilToInt(monoSamples.Length / (float)overviewBlockSize);
        overviewMinimum = new float[binCount];
        overviewMaximum = new float[binCount];
        for (int bin = 0; bin < binCount; bin++)
        {
            int first = bin * overviewBlockSize;
            int last = Mathf.Min(monoSamples.Length, first + overviewBlockSize);
            float minimum = 0f;
            float maximum = 0f;
            for (int sample = first; sample < last; sample++)
            {
                float value = monoSamples[sample];
                minimum = Mathf.Min(minimum, value);
                maximum = Mathf.Max(maximum, value);
            }
            overviewMinimum[bin] = minimum;
            overviewMaximum[bin] = maximum;
        }
    }

    private void GetWaveRange(long firstSample, long lastSample, out float minimum, out float maximum)
    {
        minimum = 0f;
        maximum = 0f;
        int first = Mathf.Clamp((int)firstSample, 0, monoSamples.Length - 1);
        int last = Mathf.Clamp((int)lastSample, first + 1, monoSamples.Length);
        if (last - first <= overviewBlockSize * 2 || overviewMinimum == null)
        {
            for (int sample = first; sample < last; sample++)
            {
                float value = monoSamples[sample];
                minimum = Mathf.Min(minimum, value);
                maximum = Mathf.Max(maximum, value);
            }
            return;
        }

        int firstBin = Mathf.Clamp(first / overviewBlockSize, 0, overviewMinimum.Length - 1);
        int lastBin = Mathf.Clamp((last - 1) / overviewBlockSize, firstBin, overviewMinimum.Length - 1);
        for (int bin = firstBin; bin <= lastBin; bin++)
        {
            minimum = Mathf.Min(minimum, overviewMinimum[bin]);
            maximum = Mathf.Max(maximum, overviewMaximum[bin]);
        }
    }

    private bool LoadAnchorSidecar(SongEntry song)
    {
        string path = GetAnchorAbsolutePath(song.ChartKey);
        if (!File.Exists(path))
        {
            return false;
        }

        BeatAnchorMap map = JsonUtility.FromJson<BeatAnchorMap>(File.ReadAllText(path, Encoding.UTF8));
        if (map == null || map.anchors == null || map.anchors.Length == 0)
        {
            status = "Anchor sidecar is empty: " + path;
            return false;
        }

        anchors.Clear();
        for (int i = 0; i < map.anchors.Length; i++)
        {
            anchors.Add(map.anchors[i].Clone());
        }
        SortAnchors(null);
        anchorsDirty = false;
        status = map.audioSha256 == CalculateAudioHash(song)
            ? "Loaded saved manual anchors."
            : "Loaded anchors, but the source audio hash has changed.";
        return true;
    }

    private void SeedAnchorsFromChart(SongEntry song)
    {
        anchors.Clear();
        long[] beats = song.Chart.beatSamples;
        if (beats != null && beats.Length > 0)
        {
            anchors.Add(new BeatAnchor { beatIndex = 0, sample = beats[0] });
            int lastBarBeat = ((beats.Length - 1) / BeatsPerBar) * BeatsPerBar;
            if (lastBarBeat > 0)
            {
                anchors.Add(new BeatAnchor { beatIndex = lastBarBeat, sample = beats[lastBarBeat] });
            }
        }
        else
        {
            anchors.Add(new BeatAnchor { beatIndex = 0, sample = song.Chart.firstBeatSample });
        }
        selectedAnchorIndex = anchors.Count > 0 ? 0 : -1;
        anchorsDirty = true;
        nextAnchorBeat = BeatsPerBar;
    }

    private void AddOrReplaceAnchor(int beatIndex, long sample)
    {
        SongEntry song = CurrentSong;
        if (song == null)
        {
            return;
        }
        beatIndex = Mathf.Max(0, beatIndex);
        sample = ClampSample(sample, song.Clip.samples);
        for (int i = 0; i < anchors.Count; i++)
        {
            if (anchors[i].beatIndex == beatIndex)
            {
                anchors[i].sample = sample;
                selectedAnchorIndex = i;
                anchorsDirty = true;
                SortAnchors(anchors[i]);
                return;
            }
        }

        BeatAnchor anchor = new BeatAnchor { beatIndex = beatIndex, sample = sample };
        anchors.Add(anchor);
        anchorsDirty = true;
        SortAnchors(anchor);
    }

    private void SortAnchors(BeatAnchor selected)
    {
        anchors.Sort((left, right) => left.beatIndex.CompareTo(right.beatIndex));
        selectedAnchorIndex = selected != null ? anchors.IndexOf(selected) : Mathf.Clamp(selectedAnchorIndex, -1, anchors.Count - 1);
    }

    private bool SaveAnchors(SongEntry song)
    {
        if (!ValidateAnchors(song, out string error))
        {
            status = "Invalid anchors: " + error;
            return false;
        }

        string absoluteFolder = Path.Combine(GetProjectRoot(), AnchorFolder.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(absoluteFolder);
        BeatAnchorMap map = new BeatAnchorMap
        {
            chartKey = song.ChartKey,
            songName = song.Chart.songName,
            audioAssetPath = song.AudioAssetPath,
            audioSha256 = CalculateAudioHash(song),
            sampleRate = song.Clip.frequency,
            totalSamples = song.Clip.samples,
            beatsPerBar = BeatsPerBar,
            anchors = anchors.ConvertAll(anchor => anchor.Clone()).ToArray()
        };
        File.WriteAllText(
            GetAnchorAbsolutePath(song.ChartKey),
            JsonUtility.ToJson(map, true) + Environment.NewLine,
            new UTF8Encoding(false));
        AssetDatabase.Refresh();
        anchorsDirty = false;
        status = "Saved " + map.anchors.Length + " manual anchors for " + song.Chart.songName + ".";
        return true;
    }

    private bool ValidateAnchors(SongEntry song, out string error)
    {
        error = string.Empty;
        SortAnchors(null);
        if (anchors.Count == 0 || anchors[0].beatIndex != 0)
        {
            error = "beat 0 is required.";
            return false;
        }

        int previousBeat = -1;
        long previousSample = -1;
        for (int i = 0; i < anchors.Count; i++)
        {
            BeatAnchor anchor = anchors[i];
            if (anchor.beatIndex % BeatsPerBar != 0)
            {
                error = "beat " + anchor.beatIndex + " is not a bar boundary.";
                return false;
            }
            if (anchor.beatIndex <= previousBeat)
            {
                error = "beat indices must be unique and increasing.";
                return false;
            }
            if (anchor.sample <= previousSample || anchor.sample < 0 || anchor.sample >= song.Clip.samples)
            {
                error = "anchor samples must be unique, increasing, and inside the clip.";
                return false;
            }
            previousBeat = anchor.beatIndex;
            previousSample = anchor.sample;
        }
        return true;
    }

    private void StartChartGeneration(SongEntry song)
    {
        if (generatorProcess != null)
        {
            status = "Chart generation is already running.";
            return;
        }
        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            status = "Stop Play Mode before rebuilding charts.";
            return;
        }
        if (!SaveAnchors(song))
        {
            return;
        }

        try
        {
            UnityNoteChartPcmExporter.ExportImportedPcm();
            string projectRoot = GetProjectRoot();
            string repositoryRoot = Path.GetFullPath(Path.Combine(projectRoot, "..", ".."));
            string scriptPath = Path.Combine(repositoryRoot, "tools", "generate_unity_note_charts.py");
            if (!File.Exists(scriptPath))
            {
                throw new FileNotFoundException("Chart generator not found", scriptPath);
            }

            generatorLines.Clear();
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "python",
                Arguments = QuoteArgument(scriptPath) + " --project " + QuoteArgument(projectRoot),
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            generatorProcess = new Process { StartInfo = startInfo };
            generatorProcess.OutputDataReceived += OnGeneratorOutput;
            generatorProcess.ErrorDataReceived += OnGeneratorOutput;
            generatorProcess.Start();
            generatorProcess.BeginOutputReadLine();
            generatorProcess.BeginErrorReadLine();
            status = "Generating charts in the background...";
        }
        catch (Exception exception)
        {
            if (generatorProcess != null)
            {
                generatorProcess.Dispose();
                generatorProcess = null;
            }
            status = "Chart generation failed: " + exception.Message;
            UnityEngine.Debug.LogException(exception);
        }
    }

    private void OnGeneratorOutput(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data))
        {
            return;
        }
        lock (generatorOutputLock)
        {
            pendingGeneratorLines.Enqueue(args.Data);
        }
    }

    private void DrainGeneratorOutput()
    {
        bool changed = false;
        lock (generatorOutputLock)
        {
            while (pendingGeneratorLines.Count > 0)
            {
                generatorLines.Add(pendingGeneratorLines.Dequeue());
                changed = true;
            }
        }
        if (generatorLines.Count > 120)
        {
            generatorLines.RemoveRange(0, generatorLines.Count - 120);
        }
        if (changed)
        {
            generatorScroll.y = float.MaxValue;
            Repaint();
        }
    }

    private void PlayPreview(SongEntry song)
    {
        AudioPreview.Stop();
        AudioPreview.Play(song.Clip, (int)ClampSample(playheadSample, song.Clip.samples));
        previewActive = true;
        previewPaused = false;
        previewStartedAt = EditorApplication.timeSinceStartup;
    }

    private void TogglePause()
    {
        if (!previewActive)
        {
            if (CurrentSong != null)
            {
                PlayPreview(CurrentSong);
            }
            return;
        }
        if (previewPaused)
        {
            AudioPreview.Resume();
            previewPaused = false;
        }
        else
        {
            playheadSample = Math.Max(0, AudioPreview.GetSamplePosition());
            AudioPreview.Pause();
            previewPaused = true;
        }
    }

    private void StopPreview()
    {
        if (previewActive && AudioPreview.IsPlaying())
        {
            int sample = AudioPreview.GetSamplePosition();
            if (sample >= 0)
            {
                playheadSample = sample;
            }
        }
        AudioPreview.Stop();
        previewActive = false;
        previewPaused = false;
    }

    private void SetPlayhead(long sample, bool updatePreview)
    {
        SongEntry song = CurrentSong;
        if (song == null || song.Clip == null)
        {
            return;
        }
        playheadSample = ClampSample(sample, song.Clip.samples);
        if (updatePreview && previewActive)
        {
            AudioPreview.SetSample(song.Clip, (int)playheadSample);
        }
        Repaint();
    }

    private void SetZoomAroundSample(float newZoom, long focusSample, float focusFraction = 0.5f)
    {
        SongEntry song = CurrentSong;
        if (song == null)
        {
            return;
        }
        zoomExponent = Mathf.Clamp(newZoom, 0f, 16f);
        long visible = GetVisibleSampleCount(song);
        long maximumStart = Math.Max(0L, song.Clip.samples - visible);
        long desiredStart = focusSample - (long)Math.Round(visible * Mathf.Clamp01(focusFraction));
        desiredStart = Math.Max(0L, Math.Min(maximumStart, desiredStart));
        scrollNormalized = maximumStart > 0 ? desiredStart / (float)maximumStart : 0f;
        Repaint();
    }

    private void CenterViewOnPlayhead()
    {
        SetZoomAroundSample(zoomExponent, playheadSample, 0.5f);
    }

    private void KeepPlayheadVisible()
    {
        SongEntry song = CurrentSong;
        if (song == null)
        {
            return;
        }
        long visible = GetVisibleSampleCount(song);
        long start = GetViewStartSample(song, visible);
        long end = start + visible;
        if (playheadSample < start || playheadSample > end)
        {
            CenterViewOnPlayhead();
        }
    }

    private long GetVisibleSampleCount(SongEntry song)
    {
        double divisor = Math.Pow(2.0, zoomExponent);
        return Math.Max(32L, Math.Min(song.Clip.samples, (long)Math.Round(song.Clip.samples / divisor)));
    }

    private long GetViewStartSample(SongEntry song, long visibleSamples)
    {
        long maximumStart = Math.Max(0L, song.Clip.samples - visibleSamples);
        return (long)Math.Round(maximumStart * Mathf.Clamp01(scrollNormalized));
    }

    private int SuggestBarBeat(SongEntry song, long sample)
    {
        double beat = EstimateBeatAtSample(song, sample);
        return Mathf.Max(0, Mathf.RoundToInt((float)(beat / BeatsPerBar)) * BeatsPerBar);
    }

    private double EstimateBeatAtSample(SongEntry song, long sample)
    {
        if (anchors.Count >= 2)
        {
            int segment = anchors.Count - 2;
            for (int i = 0; i < anchors.Count - 1; i++)
            {
                if (sample <= anchors[i + 1].sample)
                {
                    segment = i;
                    break;
                }
            }
            BeatAnchor first = anchors[segment];
            BeatAnchor last = anchors[segment + 1];
            return first.beatIndex + (sample - first.sample) *
                (last.beatIndex - first.beatIndex) / (double)(last.sample - first.sample);
        }

        long firstSample = anchors.Count > 0 ? anchors[0].sample : song.Chart.firstBeatSample;
        double period = song.Clip.frequency * 60.0 / Math.Max(1e-6, song.Chart.bpm);
        return (sample - firstSample) / period;
    }

    private double EvaluateBeatSample(SongEntry song, double beat)
    {
        if (anchors.Count >= 2)
        {
            int segment = anchors.Count - 2;
            for (int i = 0; i < anchors.Count - 1; i++)
            {
                if (beat <= anchors[i + 1].beatIndex)
                {
                    segment = i;
                    break;
                }
            }
            BeatAnchor first = anchors[segment];
            BeatAnchor last = anchors[segment + 1];
            double fraction = (beat - first.beatIndex) / (last.beatIndex - first.beatIndex);
            return first.sample + (last.sample - first.sample) * fraction;
        }

        long firstSample = anchors.Count > 0 ? anchors[0].sample : song.Chart.firstBeatSample;
        double period = song.Clip.frequency * 60.0 / Math.Max(1e-6, song.Chart.bpm);
        return firstSample + beat * period;
    }

    private int EstimateBeatCount(SongEntry song)
    {
        for (int beat = 0; beat < 100000; beat++)
        {
            if (EvaluateBeatSample(song, beat) >= song.Clip.samples)
            {
                return beat;
            }
        }
        return 0;
    }

    private static float CalculateSegmentBpm(BeatAnchor first, BeatAnchor last, int sampleRate)
    {
        long sampleDelta = last.sample - first.sample;
        int beatDelta = last.beatIndex - first.beatIndex;
        return sampleDelta > 0 && beatDelta > 0
            ? (float)(60.0 * sampleRate * beatDelta / sampleDelta)
            : 0f;
    }

    private static float SampleToX(Rect rect, long sample, long viewStart, long viewEnd)
    {
        double amount = (sample - viewStart) / (double)Math.Max(1L, viewEnd - viewStart);
        return rect.x + (float)amount * rect.width;
    }

    private static long ClampSample(long sample, int sampleCount)
    {
        return Math.Max(0L, Math.Min(Math.Max(0, sampleCount - 1), sample));
    }

    private static string FormatTime(long sample, int sampleRate)
    {
        TimeSpan time = TimeSpan.FromSeconds(sample / (double)Mathf.Max(1, sampleRate));
        return time.Minutes.ToString("00") + ":" + time.Seconds.ToString("00") + "." + time.Milliseconds.ToString("000");
    }

    private static string GetProjectRoot()
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    }

    private static string GetAnchorAbsolutePath(string chartKey)
    {
        return Path.Combine(
            GetProjectRoot(),
            AnchorFolder.Replace('/', Path.DirectorySeparatorChar),
            chartKey + ".anchors.json");
    }

    private static string CalculateAudioHash(SongEntry song)
    {
        string path = Path.Combine(GetProjectRoot(), song.AudioAssetPath.Replace('/', Path.DirectorySeparatorChar));
        using (SHA256 sha = SHA256.Create())
        using (FileStream stream = File.OpenRead(path))
        {
            byte[] hash = sha.ComputeHash(stream);
            StringBuilder builder = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }
            return builder.ToString();
        }
    }

    private static string QuoteArgument(string value)
    {
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private static class AudioPreview
    {
        private const BindingFlags Flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type AudioUtilType = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");
        private static readonly MethodInfo PlayMethod = GetMethod("PlayPreviewClip", 3);
        private static readonly MethodInfo PauseMethod = GetMethod("PausePreviewClip", 0);
        private static readonly MethodInfo ResumeMethod = GetMethod("ResumePreviewClip", 0);
        private static readonly MethodInfo StopMethod = GetMethod("StopAllPreviewClips", 0);
        private static readonly MethodInfo IsPlayingMethod = GetMethod("IsPreviewClipPlaying", 0);
        private static readonly MethodInfo GetSampleMethod = GetMethod("GetPreviewClipSamplePosition", 0);
        private static readonly MethodInfo SetSampleMethod = GetMethod("SetPreviewClipSamplePosition", 2);

        public static void Play(AudioClip clip, int sample)
        {
            PlayMethod.Invoke(null, new object[] { clip, sample, false });
        }

        public static void Pause()
        {
            PauseMethod.Invoke(null, null);
        }

        public static void Resume()
        {
            ResumeMethod.Invoke(null, null);
        }

        public static void Stop()
        {
            StopMethod.Invoke(null, null);
        }

        public static bool IsPlaying()
        {
            return (bool)IsPlayingMethod.Invoke(null, null);
        }

        public static int GetSamplePosition()
        {
            return (int)GetSampleMethod.Invoke(null, null);
        }

        public static void SetSample(AudioClip clip, int sample)
        {
            SetSampleMethod.Invoke(null, new object[] { clip, sample });
        }

        private static MethodInfo GetMethod(string name, int parameterCount)
        {
            if (AudioUtilType == null)
            {
                throw new InvalidOperationException("UnityEditor.AudioUtil was not found.");
            }
            MethodInfo[] methods = AudioUtilType.GetMethods(Flags);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == name && methods[i].GetParameters().Length == parameterCount)
                {
                    return methods[i];
                }
            }
            throw new MissingMethodException(AudioUtilType.FullName, name);
        }
    }
}
