using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace RpmSynth.Editor
{
    public sealed partial class EngineSoundWindow : EditorWindow
    {
        private AudioClip recording, preview;
        private EngineSoundProfile savedProfile;
        private float from, to, minimum = 650, maximum = 8500;
        private bool useRpmAnchors;
        private float startRpm = 1000, endRpm = 8000;
        private bool remapSavedRange;
        private float savedMinimumRpm, savedMaximumRpm;
        private EngineSoundData remappedData;
        private string remapError;
        private EngineSoundSynth liveSynth;
        private int liveSampleRate;
        private readonly List<Vector2> rpmCheckpoints = new List<Vector2>();
        private int cylinders = 4, stroke, trackingHarmonic;
        private float rpm, load = 1, texture = 0.5f, duration = 1.5f;
        private float mechanicalDetail = 1;
        private float resonantAttacks = 1;
        private float backgroundTrimDb;
        private bool analysisSettingsOpen, rpmMappingOpen, soundTuningOpen, advancedSoundOpen, wavExportOpen;
        private bool advancedAnalysisOpen;
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
                    SelectSavedProfile(profile);
                EditorGUI.BeginChangeCheck();
                var selected = (AudioClip)EditorGUILayout.ObjectField("Sweep Recording", recording, typeof(AudioClip), false);
                if (EditorGUI.EndChangeCheck()) SelectRecording(selected);
                if (GUILayout.Button("Import WAV…")) ImportWav();
                if (recording != null)
                {
                    EditorGUI.BeginChangeCheck();
                    cylinders = EditorGUILayout.IntSlider(new GUIContent("Cylinders",
                        "Sets the default harmonic for RPM tracking. A tracking harmonic selected manually or from RPM anchors takes priority."),
                        cylinders, 1, 16);
                    stroke = EditorGUILayout.Popup("Engine Cycle", stroke, new[] { "Four-Stroke", "Two-Stroke" });
                    if (EditorGUI.EndChangeCheck()) ClearResult();
                    analysisSettingsOpen = EditorGUILayout.Foldout(analysisSettingsOpen, "Analysis Settings", true);
                    if (analysisSettingsOpen) DrawAnalysisSettings();
                    DrawSpectrumWorkspace();
                    if (GUILayout.Button(guideEditsPending ? "Analyze with Guides" : "Analyze Recording")) Run(StartAnalysis);
                    if (!string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
                }
            }
            if (analysis != null)
            {
                EditorGUILayout.HelpBox("Tracking RPM and extracting harmonics…", MessageType.Info);
                if (GUILayout.Button("Cancel")) cancellation.Cancel();
            }
            if (result != null)
            {
                if (guideEditsPending)
                    EditorGUILayout.HelpBox("Analyze with Guides to apply your edits to the current track and preview.", MessageType.Info);
                using (new EditorGUI.DisabledScope(guideEditsPending || analysis != null)) DrawResult();
            }
            else if (analysis == null && savedProfile != null && savedProfile.MaximumRpm > savedProfile.MinimumRpm)
            {
                DrawPreviewControls(savedProfile.MinimumRpm, savedProfile.MaximumRpm);
                bool settingsNeedSaving = !savedProfile.HasSavedNoiseAndVariation || texture != savedProfile.NoiseAndVariation
                    || (savedProfile.HasResidualSpectrum && (!savedProfile.HasSavedBackgroundTrim
                        || backgroundTrimDb != savedProfile.BackgroundTrimDb));
                if (settingsNeedSaving && GUILayout.Button("Save Profile Settings"))
                    Run(SaveLoadedProfileSettings);
                DrawWavExport();
            }
            if (recording == null && !string.IsNullOrEmpty(error)) EditorGUILayout.HelpBox(error, MessageType.Error);
            EditorGUILayout.EndScrollView();
        }

        private void DrawAnalysisSettings()
        {
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUI.BeginChangeCheck();
                from = EditorGUILayout.FloatField("Start Time (s)", from);
                to = EditorGUILayout.FloatField("End Time (s)", to);
                useRpmAnchors = EditorGUILayout.Toggle("Use RPM Anchors", useRpmAnchors);
                if (useRpmAnchors) DrawRpmAnchors();
                else
                {
                    minimum = EditorGUILayout.FloatField("Minimum Search RPM", minimum);
                    maximum = EditorGUILayout.FloatField("Maximum Search RPM", maximum);
                }
                if (EditorGUI.EndChangeCheck()) ClearResult();

                advancedAnalysisOpen = EditorGUILayout.Foldout(advancedAnalysisOpen, "Advanced", true);
                if (advancedAnalysisOpen)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        EditorGUI.BeginChangeCheck();
                        using (new EditorGUI.DisabledScope(PairInSelection))
                            trackingHarmonic = Mathf.Clamp(EditorGUILayout.IntField(new GUIContent("Tracking Harmonic",
                                "Use 0 for automatic selection. Remove the harmonic pair to use a numeric override."),
                                trackingHarmonic), 0, 32);
                        if (EditorGUI.EndChangeCheck()) ClearResult();
                    }
                }
            }
        }

        private void DrawRpmAnchors()
        {
            EditorGUILayout.HelpBox("Enter known RPM at the selected start and end times. Add tachometer readings inside the sweep if the track follows the wrong harmonic.", MessageType.Info);
            startRpm = EditorGUILayout.FloatField("Start RPM", startRpm);
            endRpm = EditorGUILayout.FloatField("End RPM", endRpm);
            for (int i = 0; i < rpmCheckpoints.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var point = rpmCheckpoints[i];
                    point.x = EditorGUILayout.FloatField(new GUIContent($"Time {i + 1} (s)", "Time from the start of the recording."), point.x);
                    GUILayout.Label("RPM", GUILayout.Width(30));
                    point.y = EditorGUILayout.FloatField(point.y);
                    rpmCheckpoints[i] = point;
                    if (GUILayout.Button("Remove", GUILayout.Width(65)))
                    {
                        rpmCheckpoints.RemoveAt(i);
                        ClearResult();
                        break;
                    }
                }
            }
            using (new EditorGUI.DisabledScope(rpmCheckpoints.Count >= 16))
                if (GUILayout.Button("Add RPM Checkpoint"))
                {
                    rpmCheckpoints.Add(new Vector2((from + to) * 0.5f, (startRpm + endRpm) * 0.5f));
                    ClearResult();
                }
        }

        private void DrawResult()
        {
            var keys = result.Data.Rpm;
            float low = keys[0], high = keys[keys.Length - 1];
            EditorGUILayout.Space();
            string rangeLabel = useRpmAnchors ? "Anchored RPM Range" : "Detected RPM Range";
            EditorGUILayout.LabelField($"{rangeLabel} {low:F0}–{high:F0} RPM", EditorStyles.boldLabel);
            string trackingSource = result.TrackingSource switch
            {
                SweepAnalyzer.TrackingOrderSource.RpmAnchors => "RPM anchors",
                SweepAnalyzer.TrackingOrderSource.Manual => "Manual override",
                SweepAnalyzer.TrackingOrderSource.HarmonicPair => "Harmonic pair",
                _ => "Cylinders"
            };
            EditorGUILayout.LabelField("Tracking Harmonic", $"{result.TrackingOrder} ({trackingSource})");
            if (!useRpmAnchors && (low <= minimum * 1.04f || high >= maximum * 0.97f))
                EditorGUILayout.HelpBox("The RPM track is close to a search limit. Check the green line on the spectrogram.", MessageType.Warning);

            rpmMappingOpen = EditorGUILayout.Foldout(rpmMappingOpen, "RPM Mapping", true);
            if (rpmMappingOpen)
            {
                using (new EditorGUI.IndentLevelScope())
                    DrawRpmMapping();
            }
            if (remapError != null)
                EditorGUILayout.HelpBox(remapError + " Preview is using the last valid range.", MessageType.Warning);
            GetRange(out float previewLow, out float previewHigh);
            DrawPreviewControls(previewLow, previewHigh, remappedData != null);
            using (new EditorGUI.DisabledScope(remapError != null))
                if (GUILayout.Button("Save Unity Profile…")) Run(SaveProfile);
            DrawWavExport();
        }

        private void DrawRpmMapping()
        {
            EditorGUI.BeginChangeCheck();
            remapSavedRange = EditorGUILayout.Toggle("Remap RPM Range", remapSavedRange);
            if (remapSavedRange)
            {
                savedMinimumRpm = EditorGUILayout.DelayedFloatField("Target Minimum RPM", savedMinimumRpm);
                savedMaximumRpm = EditorGUILayout.DelayedFloatField("Target Maximum RPM", savedMaximumRpm);
            }
            if (EditorGUI.EndChangeCheck()) UpdateRemap();
        }

        private void UpdateRemap()
        {
            try
            {
                var mapped = remapSavedRange ? result.Data.RemapRpm(savedMinimumRpm, savedMaximumRpm) : null;
                if (livePreview)
                {
                    var synth = new EngineSoundSynth(mapped ?? result.Data, liveSampleRate,
                        Volatile.Read(ref rpm), interactive: true);
                    Volatile.Write(ref liveSynth, synth);
                }
                else if (preview != null)
                {
                    StopPreview();
                }
                remappedData = mapped;
                remapError = null;
            }
            catch (ArgumentException exception)
            {
                remapError = exception.Message;
            }
        }

        private EngineSoundData ActiveAnalysis => remappedData ?? result?.Data;

        private void DrawPreviewControls(float low, float high, bool remapped = false)
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
            string rangeLabel = remapped ? "Remapped RPM Range" : "Profile RPM Range";
            EditorGUILayout.LabelField($"{rangeLabel} {low:F0}–{high:F0} RPM", EditorStyles.miniLabel);
            if (rpm < low || rpm > high)
                EditorGUILayout.HelpBox("Outside the profile range, the tone is extrapolated from the nearest profile point.", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (livePreview)
                {
                    if (GUILayout.Button("Stop Live Preview")) Run(StopPreview);
                }
                else if (GUILayout.Button("Start Live Preview")) Run(StartLivePreview);
                if (GUILayout.Button(remapped ? "Sweep Remapped Range" : "Sweep from 500 RPM")) Run(PlaySweepPreview);
                if (!livePreview && preview != null && GUILayout.Button("Stop Sweep")) Run(StopPreview);
            }
            string tuningLabel = backgroundTrimDb > 0 ? $"Sound Tuning ({backgroundTrimDb:0.#} dB trim)" : "Sound Tuning";
            soundTuningOpen = EditorGUILayout.Foldout(soundTuningOpen, tuningLabel, true);
            if (soundTuningOpen)
                using (new EditorGUI.IndentLevelScope()) DrawSoundTuning();
        }

        private void DrawSoundTuning()
        {
            Volatile.Write(ref texture, EditorGUILayout.Slider(new GUIContent("Noise and Variation",
                "Sets the amount of residual noise and small cycle-to-cycle variations."),
                texture, 0, 1));
            bool hasNoiseSpectrum = result != null ? ActiveAnalysis.ResidualSpectrum != null : savedProfile.HasResidualSpectrum;
            if (hasNoiseSpectrum)
                Volatile.Write(ref backgroundTrimDb, EditorGUILayout.Slider(new GUIContent("Background Noise Trim (dB)",
                    "Reduces steady noise around 2–8 kHz. Saving the profile stores this setting for playback and export."),
                    backgroundTrimDb, 0, EngineResidualNoise.MaximumBackgroundTrimDb));
            advancedSoundOpen = EditorGUILayout.Foldout(advancedSoundOpen, "Advanced Sound", true);
            if (!advancedSoundOpen) return;
            using (new EditorGUI.IndentLevelScope())
                DrawAdvancedSound();
        }

        private void DrawAdvancedSound()
        {
            Volatile.Write(ref load, EditorGUILayout.Slider("Preview Load", load, 0, 1));
            bool hasMechanical = result != null ? ActiveAnalysis.MechanicalEnvelope != null
                : savedProfile.HasMechanicalDetail;
            if (hasMechanical)
                Volatile.Write(ref mechanicalDetail, EditorGUILayout.Slider("Mechanical Detail", mechanicalDetail, 0, 2));
            bool hasAttacks = result != null ? ActiveAnalysis.ResonantAttacks != null : savedProfile.HasResonantAttacks;
            if (hasAttacks)
                Volatile.Write(ref resonantAttacks, EditorGUILayout.Slider("Resonant Attacks", resonantAttacks, 0, 2));
        }

        private void DrawWavExport()
        {
            EditorGUILayout.Space();
            wavExportOpen = EditorGUILayout.Foldout(wavExportOpen, "WAV Export", true);
            if (!wavExportOpen) return;
            using (new EditorGUI.IndentLevelScope())
            {
                duration = EditorGUILayout.Slider("WAV Duration (s)", duration, 0.25f, 5);
                using (new EditorGUI.DisabledScope(remapError != null || rpm < EngineSoundSynth.MinimumLoopRpm))
                    if (GUILayout.Button("Export WAV at Selected RPM…")) Run(ExportOne);
                loopCount = EditorGUILayout.IntSlider("Number of Loops", loopCount, 2, 64);
                using (new EditorGUI.DisabledScope(remapError != null))
                    if (GUILayout.Button("Export WAV Bank for Profile Range…")) Run(ExportBank);
            }
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
            if (clip != null)
            {
                savedProfile = null;
                backgroundTrimDb = 0;
                texture = 0.5f;
            }
            from = 0;
            to = recording == null ? 0 : recording.length;
            rpmCheckpoints.Clear();
            ClearResult();
            SetSpectrumRecording();
        }

        private void SelectSavedProfile(EngineSoundProfile profile)
        {
            ClearResult();
            savedProfile = profile;
            backgroundTrimDb = profile != null ? profile.BackgroundTrimDb : 0;
            texture = profile != null ? profile.NoiseAndVariation : 0.5f;
            if (profile != null) rpm = profile.MinimumRpm;
        }

        private void StartAnalysis()
        {
            if (!(from >= 0 && to <= recording.length && to - from >= 0.8f && to - from <= 90))
                throw new ArgumentException("Select a section of the recording between 0.8 and 90 seconds long.");
            float low = useRpmAnchors ? startRpm : minimum;
            float high = useRpmAnchors ? endRpm : maximum;
            if (!(low >= 150 && high >= low + 150 && high <= 30000))
                throw new ArgumentException("Set search limits within 150–30,000 RPM and at least 150 RPM apart.");

            var anchors = useRpmAnchors ? BuildRpmAnchors() : null;
            if (guideEditsPending) StopPreview();
            else ClearResult();
            int rate = recording.frequency;
            GetSelectionFrames(out int start, out int frames);
            int channels = recording.channels;
            var samples = ReadRecording(start, frames);
            int count = cylinders, revolutions = Revolutions;
            int order = PairInSelection ? harmonicPair.Value.LowerOrder : trackingHarmonic;
            int referenceOrder = PairInSelection ? harmonicPair.Value.UpperOrder : 0;
            var guides = BuildVisualGuides(revolutions);
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            analysis = Task.Run(() => SweepAnalyzer.Analyze(channels == 1 ? samples : Downmix(samples, frames, channels),
                rate, count, revolutions, low, high, token, order, anchors, guides, referenceOrder), token);
            EditorApplication.update += FinishAnalysis;
        }

        private SweepAnalyzer.RpmAnchor[] BuildRpmAnchors()
        {
            var points = rpmCheckpoints.ToArray();
            Array.Sort(points, (a, b) => a.x.CompareTo(b.x));
            var anchors = new SweepAnalyzer.RpmAnchor[points.Length + 2];
            anchors[0] = new SweepAnalyzer.RpmAnchor(0, startRpm);
            for (int i = 0; i < points.Length; i++)
            {
                if (!(points[i].x > from && points[i].x < to))
                    throw new ArgumentException("Checkpoint times must be inside the selected recording section.");
                anchors[i + 1] = new SweepAnalyzer.RpmAnchor(points[i].x - from, points[i].y);
            }
            anchors[anchors.Length - 1] = new SweepAnalyzer.RpmAnchor(to - from, endRpm);
            return anchors;
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
                displayedOrders = DisplayedHarmonics(output);
                guideEditsPending = false;
                if (savedProfile != null)
                {
                    savedProfile = null;
                    backgroundTrimDb = 0;
                    texture = 0.5f;
                }
                result = output;
                rpm = output.Data.Rpm[0];
                remapSavedRange = false;
                savedMinimumRpm = output.Data.Rpm[0];
                savedMaximumRpm = output.Data.Rpm[output.Data.Rpm.Length - 1];
            });
            Repaint();
        }

        private void StartLivePreview()
        {
            StopPreview();
#if UNITY_EDITOR_WIN
            liveSampleRate = WindowsLiveAudio.SampleRate;
            Volatile.Write(ref liveSynth, CreatePreviewSynth(liveSampleRate, rpm, interactive: true));
            liveAudio = new WindowsLiveAudio(RenderLive);
            EditorApplication.update += CheckLiveAudio;
#else
            liveSampleRate = AudioSettings.outputSampleRate;
            Volatile.Write(ref liveSynth, CreatePreviewSynth(liveSampleRate, rpm, interactive: true));
            var clip = AudioClip.Create("Engine live preview", liveSampleRate * 2, 1, liveSampleRate, true, RenderLive);
            PlayPreview(clip, true);
#endif
            livePreview = true;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void RenderLive(float[] buffer)
        {
            var synth = Volatile.Read(ref liveSynth);
            if (synth == null) Array.Clear(buffer, 0, buffer.Length);
            else synth.Render(buffer, Volatile.Read(ref rpm), Volatile.Read(ref load), EngineSoundSynth.DefaultPlaybackGain,
                Volatile.Read(ref texture), Volatile.Read(ref mechanicalDetail), Volatile.Read(ref resonantAttacks),
                Volatile.Read(ref backgroundTrimDb));
        }

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
            GetRange(out float profileLow, out float profileHigh);
            float low = remappedData != null ? profileLow : 500;
            float high = remappedData != null ? profileHigh : PreviewMaximum(profileHigh);
            var synth = CreatePreviewSynth(rate, low);
            for (int i = 0; i < blocks; i++)
            {
                float fraction = i / (float)(blocks - 1);
                float travel = Mathf.Clamp01((fraction - 0.08f) / 0.84f);
                float speed = Mathf.Lerp(low, high, travel);
                float gain = Mathf.Clamp01((1 - fraction) * 25) * EngineSoundSynth.DefaultPlaybackGain;
                synth.Render(buffer, speed, load, gain, texture, mechanicalDetail, resonantAttacks, backgroundTrimDb);
                Array.Copy(buffer, 0, samples, i * block, block);
            }
            var clip = AudioClip.Create("Engine sweep preview", samples.Length, 1, rate, false);
            clip.SetData(samples, 0);
            PlayPreview(clip, false);
        }

        private static float PreviewMaximum(float recordedHigh) =>
            Mathf.Min(EngineSoundSynth.MaximumSupportedRpm, Mathf.Max(8500, recordedHigh * 1.25f));

        private EngineSoundSynth CreatePreviewSynth(int rate, float speed, bool interactive = false) => result != null
            ? new EngineSoundSynth(ActiveAnalysis, rate, speed, interactive) : savedProfile.CreateSynth(rate, speed, interactive);

        private void GetRange(out float low, out float high)
        {
            var keys = ActiveAnalysis?.Rpm;
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
            EditorApplication.update -= CheckRangePreview;
            selectionPreview = false;
            Volatile.Write(ref liveSynth, null);
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
            SaveAnalyzedProfile(path);
        }

        private void SaveAnalyzedProfile(string path)
        {
            var data = ActiveAnalysis;
            var profile = AssetDatabase.LoadAssetAtPath<EngineSoundProfile>(path);
            if (profile == null)
            {
                if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                    throw new IOException("An asset of a different type already exists at this path.");
                profile = CreateInstance<EngineSoundProfile>();
                profile.SetData(data);
                profile.SetBackgroundTrim(backgroundTrimDb);
                profile.SetNoiseAndVariation(texture);
                AssetDatabase.CreateAsset(profile, path);
            }
            else
            {
                Undo.RecordObject(profile, "Update engine sound profile");
                profile.SetData(data);
                profile.SetBackgroundTrim(backgroundTrimDb);
                profile.SetNoiseAndVariation(texture);
                EditorUtility.SetDirty(profile);
            }
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(profile);
        }

        private void SaveLoadedProfileSettings()
        {
            Undo.RecordObject(savedProfile, "Change engine sound settings");
            if (savedProfile.HasResidualSpectrum)
                savedProfile.SetBackgroundTrim(backgroundTrimDb);
            savedProfile.SetNoiseAndVariation(texture);
            EditorUtility.SetDirty(savedProfile);
            AssetDatabase.SaveAssetIfDirty(savedProfile);
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
            float[] pcm = result != null
                ? EngineSoundSynth.RenderLoop(ActiveAnalysis, speed, duration, rate, mechanicalDetail, texture,
                    resonantAttacks, backgroundTrimDb)
                : savedProfile.RenderLoop(speed, duration, rate, mechanicalDetail, texture, resonantAttacks, backgroundTrimDb);
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
            remappedData = null;
            remapError = null;
            StopPreview();
            guideEditsPending = false;
            displayedOrders = Array.Empty<int>();
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
            ReleaseSpectrum();
            ClearResult();
        }
    }
}
