using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class UnityNoteChartPcmExporter
{
    [Serializable]
    private sealed class ExportManifest
    {
        public ExportedClip[] clips;
    }

    [Serializable]
    private sealed class ExportedClip
    {
        public string name;
        public string file;
        public int sampleRate;
        public int samples;
        public int channels;
    }

    private const string MusicFolder = "Assets/Resources/Music";
    private const int ChunkFrames = 65536;

    [MenuItem("Tools/Rhythm/Export Imported PCM For Note Charts %#e")]
    public static void ExportImportedPcm()
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        string outputFolder = Path.Combine(projectRoot, "Temp", "NoteChartPcm");
        Directory.CreateDirectory(outputFolder);

        string[] guids = AssetDatabase.FindAssets("t:AudioClip", new[] { MusicFolder });
        List<AudioClip> clips = new List<AudioClip>();
        for (int i = 0; i < guids.Length; i++)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guids[i]);
            if (!assetPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
            if (clip != null)
            {
                clips.Add(clip);
            }
        }

        clips.Sort((left, right) => string.Compare(left.name, right.name, StringComparison.Ordinal));
        List<ExportedClip> exported = new List<ExportedClip>();
        try
        {
            for (int i = 0; i < clips.Count; i++)
            {
                AudioClip clip = clips[i];
                string fileName = "clip_" + i.ToString("00") + ".f32";
                string filePath = Path.Combine(outputFolder, fileName);
                EditorUtility.DisplayProgressBar(
                    "Exporting imported song PCM",
                    clip.name,
                    clips.Count <= 0 ? 1f : i / (float)clips.Count);

                if (clip.loadState != AudioDataLoadState.Loaded && !clip.LoadAudioData())
                {
                    throw new InvalidOperationException("Could not load AudioClip data: " + clip.name);
                }

                ExportClipMonoFloat(clip, filePath);
                exported.Add(new ExportedClip
                {
                    name = clip.name,
                    file = fileName,
                    sampleRate = clip.frequency,
                    samples = clip.samples,
                    channels = clip.channels
                });
                clip.UnloadAudioData();
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        ExportManifest manifest = new ExportManifest { clips = exported.ToArray() };
        File.WriteAllText(
            Path.Combine(outputFolder, "manifest.json"),
            JsonUtility.ToJson(manifest, true));
        Debug.Log("[PCM Export] Exported " + exported.Count + " imported AudioClip(s) to " + outputFolder + ".");
    }

    private static void ExportClipMonoFloat(AudioClip clip, string path)
    {
        using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            int channels = Mathf.Max(1, clip.channels);
            for (int offset = 0; offset < clip.samples; offset += ChunkFrames)
            {
                int frameCount = Mathf.Min(ChunkFrames, clip.samples - offset);
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

                    writer.Write(mono / channels);
                }
            }
        }
    }
}
