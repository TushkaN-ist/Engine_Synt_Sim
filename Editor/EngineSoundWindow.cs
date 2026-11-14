using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace RpmSynth.Editor
{
    public sealed class EngineSoundWindow : EditorWindow
    {
        private AudioClip recording, preview;
        private EngineSoundProfile savedProfile;
        private Texture2D spectrogram;
        private float from, to, minimum = 650, maximum = 8500;
        private int cylinders = 4, stroke, trackingHarmonic;
        private float rpm, load = 1, texture = 0.5f, duration = 1.5f;
        private bool livePreview;
#if UNITY_EDITOR_WIN
        private WindowsLiveAudio liveAudio;
#endif
        private int loopCount = 16;
        private string error;
        private Vector2 scroll;
        private CancellationTokenSource cancellation;
        private Task<SweepAnalyzer.Result> analysis;
        private SweepAnalyzer.Result result;
        private string directory = "Assets";
        private const BindingFlags AudioApi = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        private static readonly Type AudioUtil = typeof(AudioImporter).Assembly.GetType("UnityEditor.AudioUtil");

        [MenuItem("Tools/Engine Sound Designer")]
        private static void Open() => GetWindow<EngineSoundWindow>("Engine Sound Designer");

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.HelpBox("Choose one smooth sweep from idle to high RPM without gear changes or throttle lift.", MessageType.Info);
            using (new EditorGUI.DisabledScope(analysis != null))
            {
                EditorGUI.BeginChangeCheck();
                var profile = (EngineSoundProfile)EditorGUILayout.ObjectField("Saved Profile", savedProfile,
                    typeof(EngineSoundProfile), false);
                if (EditorGUI.EndChangeCheck())
                {
                    ClearResult();
                    savedProfile = profile;
                    if (profile != null) rpm = profile.MinimumRpm;
                }
                EditorGUI.BeginChangeCheck();
                var selected = (AudioClip)EditorGUILayout.ObjectField("Sweep Recording", recording, typeof(AudioClip), false);
                if (EditorGUI.EndChangeCheck()) SelectRecording(selected);
                if (GUILayout.Button("Import WAV…")) ImportWav();
                if (recording != null)
                {
                    EditorGUI.BeginChangeCheck();
                    from = EditorGUILayout.FloatField("Start Time (s)", from);
                    to = EditorGUILayout.FloatField("End Time (s)", to);
                    cylinders = EditorGUILayout.IntSlider("Cylinders", cylinders, 1, 16);
                    stroke = EditorGUILayout.Popup("Engine Cycle", stroke, new[] { "Four-Stroke", "Two-Stroke" });
                    trackingHarmonic = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Tracking Harmonic",
                        "Use 0 to track the firing frequency. Other values specify the number of periods per engine cycle."), trackingHarmonic), 0, 32);
                    minimum = EditorGUILayout.FloatField("Minimum Search RPM", minimum);
                    maximum = EditorGUILayout.FloatField("Maximum Search RPM", maximum);
                    if (EditorGUI.EndChangeCheck()) ClearResult();
                    if (GUILayout.Button("Analyze Recording")) Run(StartAnalysis);
                }
            }
            if (analysis != null)
            {
                EditorGUILayout.HelpBox("Tracking RPM and extracting harmonics…", MessageType.Info);
                if (GUILayout.Button("Cancel")) cancellation.Cancel();
            }
            if (result != null) DrawResult();
            else if (savedProfile != null && savedProfile.MaximumRpm > savedProfile.MinimumRpm)
                DrawPreviewControls(savedProfile.MinimumRpm, savedProfile.MaximumRpm);
            if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            EditorGUILayout.EndScrollView();
        }

        private void DrawResult()
        {
            var keys = result.Data.Rpm;
            float low = keys[0], high = keys[keys.Length - 1];
            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Detected RPM Range {low:F0}–{high:F0} RPM", EditorStyles.boldLabel);
            int order = trackingHarmonic == 0 ? cylinders : trackingHarmonic;
            EditorGUILayout.LabelField($"Spectrum up to {result.SpectrogramMaxHz:F0} Hz. Green line shows harmonic {order}.");
            Rect chart = GUILayoutUtility.GetRect(100, 160, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                GUI.DrawTexture(chart, spectrogram);
                Handles.BeginGUI();
                Color previousColor = Handles.color;
                Handles.color = new Color(0.50f, 1f, 0.56f);
                int width = result.Times.Length - 1;
                float scale = order / (60f * result.Data.RevolutionsPerCycle * result.SpectrogramMaxHz);
                Vector3 previous = new Vector3(chart.x, chart.yMax - chart.height * result.TrackedRpm[0] * scale);
                for (int i = 1; i <= width; i++)
                {
                    var point = new Vector3(chart.x + chart.width * i / width,
                        chart.yMax - chart.height * result.TrackedRpm[i] * scale);
                    Handles.DrawLine(previous, point, 2f);
                    previous = point;
                }
                Handles.color = previousColor;
                Handles.EndGUI();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label($"{from + result.Times[0]:F2} s");
                GUILayout.FlexibleSpace();
                GUILayout.Label($"{from + result.Times[result.Times.Length - 1]:F2} s");
            }
            if (low <= minimum * 1.04f || high >= maximum * 0.97f)
                EditorGUILayout.HelpBox("The RPM track is close to a search limit. Compare it with the spectrogram and adjust the search range if needed.", MessageType.Warning);
            DrawPreviewControls(low, high);
            if (GUILayout.Button("Save Unity Profile…")) Run(SaveProfile);
        }

        private void DrawPreviewControls(float low, float high)
        {
            float sliderMax = PreviewMaximum(high);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Preview RPM");
                EditorGUI.BeginChangeCheck();
                float selected = GUILayout.HorizontalSlider(Mathf.Min(rpm, sliderMax), 0, sliderMax);
                if (EditorGUI.EndChangeCheck()) Volatile.Write(ref rpm, selected);
                selected = EditorGUILayout.FloatField(rpm, GUILayout.Width(76));
                Volatile.Write(ref rpm, float.IsNaN(selected) ? low : Mathf.Clamp(selected, 0, EngineSoundSynth.MaximumSupportedRpm));
            }
            EditorGUILayout.LabelField($"Recorded RPM Range {low:F0}–{high:F0} RPM", EditorStyles.miniLabel);
            if (rpm < low || rpm > high)
                EditorGUILayout.HelpBox("Outside the recorded range, the tone is extrapolated from the nearest recorded RPM.", MessageType.Info);
            Volatile.Write(ref load, EditorGUILayout.Slider("Load", load, 0, 1));
            Volatile.Write(ref texture, EditorGUILayout.Slider("Noise and Variation", texture, 0, 1));
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(livePreview))
                    if (GUILayout.Button("Start Live Preview")) Run(StartLivePreview);
                if (GUILayout.Button("Sweep from 500 RPM")) Run(PlaySweepPreview);
                if (GUILayout.Button("Stop")) Run(StopPreview);
            }
            if (livePreview) EditorGUILayout.LabelField("Live preview is playing", EditorStyles.miniLabel);
            EditorGUILayout.Space();
            duration = EditorGUILayout.Slider("WAV Duration (s)", duration, 0.25f, 5);
            using (new EditorGUI.DisabledScope(rpm < EngineSoundSynth.MinimumLoopRpm))
                if (GUILayout.Button("Export WAV at Selected RPM…")) Run(ExportOne);
            loopCount = EditorGUILayout.IntSlider("Number of Loops", loopCount, 2, 64);
            if (GUILayout.Button("Export WAV Bank for Recorded Range…")) Run(ExportBank);
        }

        private void ImportWav()
        {
            Run(() =>
            {
                string path = EditorUtility.OpenFilePanel("Select a Sweep Recording", "", "wav");
                if (string.IsNullOrEmpty(path)) return;
                string assets = Path.GetFullPath(Application.dataPath);
                path = Path.GetFullPath(path);
                string projectPath;
                if (path.StartsWith(assets + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    projectPath = "Assets/" + path.Substring(assets.Length + 1).Replace('\\', '/');
                else
                {
                    projectPath = AssetDatabase.GenerateUniqueAssetPath("Assets/" + Path.GetFileName(path));
                    File.Copy(path, Path.Combine(assets, projectPath.Substring("Assets/".Length)));
                }
                AssetDatabase.ImportAsset(projectPath, ImportAssetOptions.ForceSynchronousImport);
                var clip = AssetDatabase.LoadAssetAtPath<AudioClip>(projectPath);
                if (clip == null) throw new InvalidOperationException("Unity could not import this WAV file.");
                SelectRecording(clip);
            });
        }

        private void SelectRecording(AudioClip clip)
        {
            recording = clip;
            from = 0;
            to = recording == null ? 0 : recording.length;
            ClearResult();
        }

        private void StartAnalysis()
        {
            if (!(from >= 0 && to <= recording.length && to - from >= 0.8f && to - from <= 90))
                throw new ArgumentException("Select a section of the recording between 0.8 and 90 seconds long.");
            if (!(minimum >= 150 && maximum >= minimum + 150 && maximum <= 30000))
                throw new ArgumentException("Set search limits within 150–30,000 RPM and at least 150 RPM apart.");

            ClearResult();
            int rate = recording.frequency;
            int start = Mathf.RoundToInt(from * rate);
            int frames = Math.Min(recording.samples - start, Mathf.RoundToInt((to - from) * rate));
            int channels = recording.channels;
            if (recording.loadState != AudioDataLoadState.Loaded) recording.LoadAudioData();
            var samples = new float[checked(frames * channels)];
            if (!recording.GetData(samples, start))
                throw new InvalidOperationException("Set the recording's Load Type to Decompress On Load and enable Preload Audio Data.");
            float[] mono = channels == 1 ? samples : Downmix(samples, frames, channels);
            int count = cylinders, revolutions = stroke == 0 ? 2 : 1, order = trackingHarmonic;
            float low = minimum, high = maximum;
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            analysis = Task.Run(() => SweepAnalyzer.Analyze(mono, rate, count, revolutions, low, high, token, order), token);
            EditorApplication.update += FinishAnalysis;
        }

        private static float[] Downmix(float[] samples, int frames, int channels)
        {
            var mono = new float[frames];
            var energy = new double[channels];
            double mixedEnergy = 0;
            for (int i = 0; i < frames; i++)
            {
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    float value = samples[i * channels + c];
                    sum += value;
                    energy[c] += value * value;
                }
                mono[i] = sum / channels;
                mixedEnergy += mono[i] * mono[i];
            }
            int loudest = 0;
            for (int i = 1; i < channels; i++)
                if (energy[i] > energy[loudest]) loudest = i;
            if (mixedEnergy < energy[loudest] * 0.15)
                for (int i = 0; i < frames; i++) mono[i] = samples[i * channels + loudest];
            return mono;
        }

        private void FinishAnalysis()
        {
            if (!analysis.IsCompleted) return;
            EditorApplication.update -= FinishAnalysis;
            var completed = analysis;
            analysis = null;
            cancellation.Dispose();
            cancellation = null;
            if (completed.IsCanceled) { Repaint(); return; }
            Run(() =>
            {
                var output = completed.GetAwaiter().GetResult();
                spectrogram = BuildSpectrogram(output);
                result = output;
                rpm = output.Data.Rpm[0];
            });
            Repaint();
        }

        private static Texture2D BuildSpectrogram(SweepAnalyzer.Result output)
        {
            int width = Math.Min(2048, output.Spectrogram.Length), height = output.Spectrogram[0].Length;
            var map = new Texture2D(width, height, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[width * height];
            for (int x = 0; x < width; x++)
            {
                var frame = output.Spectrogram[x * (output.Spectrogram.Length - 1) / (width - 1)];
                for (int y = 0; y < height; y++)
                {
                    float strength = Mathf.InverseLerp(-68, -12, frame[y]);
                    pixels[y * width + x] = new Color(0.05f + strength * 0.9f,
                        0.06f + strength * strength * 0.62f, 0.10f + strength * 0.25f);
                }
            }
            map.SetPixels32(pixels);
            map.Apply(false, true);
            return map;
        }

        private void StartLivePreview()
        {
            StopPreview();
#if UNITY_EDITOR_WIN
            var synth = CreatePreviewSynth(WindowsLiveAudio.SampleRate, rpm, interactive: true);
            liveAudio = new WindowsLiveAudio(buffer => RenderLive(synth, buffer));
            EditorApplication.update += CheckLiveAudio;
#else
            int rate = AudioSettings.outputSampleRate;
            var synth = CreatePreviewSynth(rate, rpm, interactive: true);
            var clip = AudioClip.Create("Engine live preview", rate * 2, 1, rate, true,
                buffer => RenderLive(synth, buffer));
            PlayPreview(clip, true);
#endif
            livePreview = true;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void RenderLive(EngineSoundSynth synth, float[] buffer) =>
            synth.Render(buffer, Volatile.Read(ref rpm), Volatile.Read(ref load), 0.85f, Volatile.Read(ref texture));

#if UNITY_EDITOR_WIN
        private void CheckLiveAudio()
        {
            string message = liveAudio?.Error;
            if (message == null) return;
            StopPreview();
            error = message;
            Repaint();
        }
#endif

        private void PlaySweepPreview()
        {
            StopPreview();
            const int rate = 48000, block = 512, blocks = 660;
            var buffer = new float[block];
            var samples = new float[blocks * block];
            GetRange(out _, out float recordedHigh);
            const float low = 500;
            float high = PreviewMaximum(recordedHigh);
            var synth = CreatePreviewSynth(rate, low);
            for (int i = 0; i < blocks; i++)
            {
                float fraction = i / (float)(blocks - 1);
                float travel = Mathf.Clamp01((fraction - 0.08f) / 0.84f);
                float speed = Mathf.Lerp(low, high, travel);
                float gain = Mathf.Clamp01((1 - fraction) * 25) * 0.85f;
                synth.Render(buffer, speed, load, gain, texture);
                Array.Copy(buffer, 0, samples, i * block, block);
            }
            var clip = AudioClip.Create("Engine sweep preview", samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            PlayPreview(clip, false);
        }

        private static float PreviewMaximum(float recordedHigh) =>
            Mathf.Min(EngineSoundSynth.MaximumSupportedRpm, Mathf.Max(8500, recordedHigh * 1.25f));

        private EngineSoundSynth CreatePreviewSynth(int rate, float speed, bool interactive = false) => result != null
            ? new EngineSoundSynth(result.Data, rate, speed, interactive) : savedProfile.CreateSynth(rate, speed, interactive);

        private void GetRange(out float low, out float high)
        {
            var keys = result?.Data.Rpm;
            low = keys == null ? savedProfile.MinimumRpm : keys[0];
            high = keys == null ? savedProfile.MaximumRpm : keys[keys.Length - 1];
        }

        private void PlayPreview(AudioClip clip, bool loop)
        {
            preview = clip;
            try
            {
                var play = AudioUtil?.GetMethod("PlayPreviewClip", AudioApi,
                    null, new[] { typeof(AudioClip), typeof(int), typeof(bool) }, null);
                if (play == null)
                    throw new InvalidOperationException("Audio preview is unavailable in this Unity version. Export a WAV file to listen to the result.");
                play.Invoke(null, new object[] { preview, 0, loop });
            }
            catch
            {
                StopPreview();
                throw;
            }
        }

        private void StopPreview()
        {
#if UNITY_EDITOR_WIN
            EditorApplication.update -= CheckLiveAudio;
            liveAudio?.Dispose();
            liveAudio = null;
#endif
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            livePreview = false;
            if (preview == null) return;
            AudioUtil?.GetMethod("StopAllPreviewClips", AudioApi)?.Invoke(null, null);
            DestroyImmediate(preview);
            preview = null;
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.ExitingPlayMode)
                StopPreview();
        }

        private void SaveProfile()
        {
            string path = EditorUtility.SaveFilePanelInProject(
                "Save Profile", recording.name + " Engine", "asset", "Select a folder in the project.", directory);
            if (string.IsNullOrEmpty(path)) return;
            directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "Assets";
            var profile = AssetDatabase.LoadAssetAtPath<EngineSoundProfile>(path);
            if (profile == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                    throw new IOException("An asset of a different type already exists at this path.");
                profile = CreateInstance<EngineSoundProfile>();
                profile.SetData(result.Data);
                AssetDatabase.CreateAsset(profile, path);
            }
            else
            {
                Undo.RecordObject(profile, "Update engine sound profile");
                profile.SetData(result.Data);
                EditorUtility.SetDirty(profile);
            }
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(profile);
        }

        private void ExportOne()
        {
            string path = EditorUtility.SaveFilePanel("Save Loop", "", $"engine_{rpm:F0}rpm", "wav");
            if (!string.IsNullOrEmpty(path)) WriteLoop(path, rpm);
        }

        private void ExportBank()
        {
            string parent = EditorUtility.OpenFolderPanel("Select a Folder for the WAV Bank", "", "");
            if (string.IsNullOrEmpty(parent)) return;
            string root = Path.Combine(parent, (result != null ? recording.name : savedProfile.name) + " loops");
            string target = root;
            for (int suffix = 2; Directory.Exists(target) || File.Exists(target); suffix++)
                target = root + " " + suffix;
            Directory.CreateDirectory(target);
            GetRange(out float low, out float high);
            using (var manifest = new StreamWriter(Path.Combine(target, "loops.csv")))
            {
                manifest.WriteLine("rpm,file");
                for (int i = 0; i < loopCount; i++)
                {
                    float speed = Mathf.Lerp(low, high, i / (float)(loopCount - 1));
                    string file = $"engine_{i + 1:D2}_{speed:F0}rpm.wav";
                    WriteLoop(Path.Combine(target, file), speed);
                    manifest.WriteLine(speed.ToString("F2", CultureInfo.InvariantCulture) + "," + file);
                }
            }
            EditorUtility.RevealInFinder(target);
        }

        private void WriteLoop(string path, float speed)
        {
            const int rate = 48000;
            float[] pcm = result != null ? EngineSoundSynth.RenderLoop(result.Data, speed, duration, rate)
                : savedProfile.RenderLoop(speed, duration, rate);
            using (var wav = new BinaryWriter(File.Create(path)))
            {
                wav.Write(0x46464952u); // RIFF
                wav.Write(36 + pcm.Length * 2);
                wav.Write(0x45564157u); // WAVE
                wav.Write(0x20746D66u); // fmt
                wav.Write(16);
                wav.Write((ushort)1);
                wav.Write((ushort)1);
                wav.Write(rate);
                wav.Write(rate * 2);
                wav.Write((ushort)2);
                wav.Write((ushort)16);
                wav.Write(0x61746164u); // data
                wav.Write(pcm.Length * 2);
                foreach (float sample in pcm)
                    wav.Write((short)Mathf.RoundToInt(Mathf.Clamp(sample, -1, 1) * 32767));
            }
        }

        private void Run(Action action)
        {
            error = null;
            try { action(); }
            catch (Exception exception) { error = exception.GetBaseException().Message; }
        }

        private void ClearResult()
        {
            error = null;
            result = null;
            StopPreview();
            if (spectrogram != null) DestroyImmediate(spectrogram);
            spectrogram = null;
        }

        private void OnDisable()
        {
            EditorApplication.update -= FinishAnalysis;
            if (cancellation != null)
            {
                cancellation.Cancel();
                cancellation.Dispose();
                cancellation = null;
                _ = analysis.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                analysis = null;
            }
            ClearResult();
        }
    }
}
