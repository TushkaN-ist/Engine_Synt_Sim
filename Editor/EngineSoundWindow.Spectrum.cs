using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace RpmSynth.Editor
{
    public sealed partial class EngineSoundWindow
    {
        private enum SpectrumTool { Range, CorrectTrack, Pair }
        private enum RangeEdge { None, Start, End }

        private const float RangeGripRadius = 12;
        private const float MinimumDragRange = .05f;
        private static readonly string[] SpectrumTools = { "Select Range", "Correct Track", "Harmonic Pair" };
        private static readonly float[] FrequencyTicks = { 0, 25, 50, 75, 100, 150, 200, 300, 500, 750, 1000, 1500,
            2000, 3000, 4500, 6000, 9000, 12000, 16000, 20000, 24000, 32000, 48000 };
        private static readonly Color TrackColor = new Color(.55f, 1f, .64f);
        private static readonly Color GuideColor = new Color(.55f, .85f, 1f);
        private static readonly Color PairColor = new Color(1f, .62f, .34f);

        private RecordingSpectrum recordingSpectrum;
        private Texture2D spectrumTexture;
        private Task<RecordingSpectrum> spectrumTask;
        private CancellationTokenSource spectrumCancellation;
        private string spectrumError;
        private SpectrumTool spectrumTool;
        private float viewFrom, viewTo, viewMinHz, viewMaxHz, plotMinimumAxis;
        private bool snapToPeaks = true, showHarmonics = true, guideEditsPending;
        private int orderGap = 1;
        // Store cycle frequency so points also work when a different harmonic is tracked.
        private readonly List<Vector2> trackPoints = new List<Vector2>();
        private HarmonicPair? harmonicPair;
        private float pairTime;
        private Vector2? pendingPairPoint;
        private bool spectrumDragging;
        private RangeEdge hoveredRangeEdge, draggedRangeEdge;
        private int draggedPoint = -1;
        private float dragOrigin, dragFrom, dragTo, rangeGrabOffset;
        private Vector2 draggedGuide;
        private bool selectionPreview;
#if !UNITY_EDITOR_WIN
        private double selectionEndTime;
#endif
        private int[] displayedOrders = Array.Empty<int>();

        private int Revolutions => stroke == 0 ? 2 : 1;
        private float MinimumFrequencyAxisSpan => 2f / recordingSpectrum.Height;
        private bool PairInSelection => harmonicPair.HasValue && pairTime >= from && pairTime <= to;
        private int VisualTrackingOrder => PairInSelection ? harmonicPair.Value.LowerOrder
            : result?.TrackingOrder ?? (trackingHarmonic > 0 ? trackingHarmonic : cylinders);

        private void SetSpectrumRecording()
        {
            ReleaseSpectrum();
            trackPoints.Clear();
            harmonicPair = null;
            pendingPairPoint = null;
            spectrumTool = SpectrumTool.Range;
            guideEditsPending = false;
            spectrumDragging = false;
            hoveredRangeEdge = draggedRangeEdge = RangeEdge.None;
            spectrumError = null;
            viewFrom = 0;
            viewTo = recording != null ? recording.length : 0;
            if (recording == null) return;
            wantsMouseMove = true;
            viewMinHz = 0;
            viewMaxHz = Mathf.Min(2200, recording.frequency * .5f);
            if (recording.loadState == AudioDataLoadState.Unloaded) recording.LoadAudioData();
            EditorApplication.update += UpdateRecordingSpectrum;
        }

        private void UpdateRecordingSpectrum()
        {
            try
            {
                if (spectrumTask == null)
                {
                    if (recording.loadState == AudioDataLoadState.Loading) return;
                    int frames = recording.samples, channels = recording.channels, rate = recording.frequency;
                    float[] samples = ReadRecording(0, frames);
                    spectrumCancellation = new CancellationTokenSource();
                    var token = spectrumCancellation.Token;
                    spectrumTask = Task.Run(() => RecordingSpectrum.Build(
                        channels == 1 ? samples : Downmix(samples, frames, channels), rate, token), token);
                    return;
                }
                if (!spectrumTask.IsCompleted) return;
                recordingSpectrum = spectrumTask.GetAwaiter().GetResult();
                var pixels = new Color32[recordingSpectrum.Intensity.Length];
                var background = new Color32(12, 17, 29, 255);
                var mid = new Color32(28, 118, 130, 255);
                var bright = new Color32(252, 213, 135, 255);
                for (int i = 0; i < pixels.Length; i++)
                {
                    float value = recordingSpectrum.Intensity[i] / 255f;
                    pixels[i] = value < .62f ? Color32.Lerp(background, mid, value / .62f)
                        : Color32.Lerp(mid, bright, (value - .62f) / .38f);
                }
                spectrumTexture = new Texture2D(recordingSpectrum.Width, recordingSpectrum.Height, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear
                };
                spectrumTexture.SetPixels32(pixels);
                spectrumTexture.Apply(false, true);
            }
            catch (Exception exception)
            {
                spectrumError = exception.GetBaseException().Message;
            }
            EditorApplication.update -= UpdateRecordingSpectrum;
            spectrumCancellation?.Dispose();
            spectrumCancellation = null;
            spectrumTask = null;
            Repaint();
        }

        private void ReleaseSpectrum()
        {
            EditorApplication.update -= UpdateRecordingSpectrum;
            spectrumCancellation?.Cancel();
            spectrumCancellation?.Dispose();
            spectrumCancellation = null;
            if (spectrumTask != null)
                _ = spectrumTask.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            spectrumTask = null;
            if (spectrumTexture != null) DestroyImmediate(spectrumTexture);
            spectrumTexture = null;
            recordingSpectrum = null;
        }

        private float[] ReadRecording(int start, int frames)
        {
            if (recording.loadState == AudioDataLoadState.Unloaded) recording.LoadAudioData();
            if (recording.loadState != AudioDataLoadState.Loaded)
                throw new InvalidOperationException("Set the clip's Load Type to Decompress On Load and enable Preload Audio Data.");
            var samples = new float[checked(frames * recording.channels)];
            if (!recording.GetData(samples, start))
                throw new InvalidOperationException("Unity could not read the clip. Set its Load Type to Decompress On Load.");
            return samples;
        }

        private void PlaySelectedRange()
        {
            GetSelectionFrames(out int start, out int frames);
            var samples = ReadRecording(start, frames);
            StopPreview();
#if UNITY_EDITOR_WIN
            liveAudio = new WindowsLiveAudio(samples, recording.frequency, recording.channels);
#else
            var clip = AudioClip.Create("Source range", frames, recording.channels, recording.frequency, false);
            if (!clip.SetData(samples, 0))
            {
                DestroyImmediate(clip);
                throw new InvalidOperationException("Could not create the range preview.");
            }
            PlayPreview(clip, false);
            selectionEndTime = EditorApplication.timeSinceStartup + clip.length;
#endif
            selectionPreview = true;
            EditorApplication.update += CheckRangePreview;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void CheckRangePreview()
        {
#if UNITY_EDITOR_WIN
            var message = liveAudio?.Error;
            if (message == null && liveAudio != null && !liveAudio.IsComplete) return;
            if (message != null) error = message;
#else
            if (EditorApplication.timeSinceStartup < selectionEndTime) return;
#endif
            StopPreview();
            Repaint();
        }

        private void GetSelectionFrames(out int start, out int frames)
        {
            start = Mathf.Clamp(Mathf.RoundToInt(from * recording.frequency), 0, recording.samples);
            int end = Mathf.Clamp(Mathf.RoundToInt(to * recording.frequency), 0, recording.samples);
            frames = end - start;
            if (frames <= 0) throw new ArgumentException("Select a range with a start time before its end.");
        }

        private SweepAnalyzer.RpmAnchor[] BuildVisualGuides(int revolutions)
        {
            var guides = new List<SweepAnalyzer.RpmAnchor>();
            foreach (var point in trackPoints)
                if (point.x >= from && point.x <= to)
                    guides.Add(new SweepAnalyzer.RpmAnchor(point.x - from, point.y * (60 * revolutions)));
            if (PairInSelection)
                guides.Add(new SweepAnalyzer.RpmAnchor(pairTime - from, harmonicPair.Value.CycleHz * (60 * revolutions)));
            return guides.Count == 0 ? null : guides.ToArray();
        }

        private void DrawSpectrumWorkspace()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Recording Spectrogram", EditorStyles.boldLabel);
            if (spectrumTexture == null)
            {
                if (spectrumError == null)
                    EditorGUILayout.LabelField("Building the recording spectrogram…", EditorStyles.miniLabel);
                else
                {
                    EditorGUILayout.HelpBox(spectrumError, MessageType.Warning);
                    if (GUILayout.Button("Build Spectrogram Again")) SetSpectrumRecording();
                }
                return;
            }

            var nextTool = (SpectrumTool)GUILayout.Toolbar((int)spectrumTool, SpectrumTools);
            if (nextTool != spectrumTool)
            {
                spectrumTool = nextTool;
                pendingPairPoint = null;
            }
            string hint = spectrumTool switch
            {
                SpectrumTool.Range => "Drag the highlighted handles to trim the range, or drag anywhere else to start a new selection.",
                SpectrumTool.CorrectTrack => "Click or drag to put a point on the ridge. Right-click a point to remove it.",
                _ => pendingPairPoint.HasValue ? "Click the higher line at the marked time."
                    : "Click the lower of two lines at a time where both are visible."
            };
            EditorGUILayout.LabelField(hint, EditorStyles.wordWrappedMiniLabel);
            if (spectrumTool == SpectrumTool.Pair)
                orderGap = EditorGUILayout.IntSlider(new GUIContent("Order Gap",
                    "Use 1 for adjacent engine orders. Increase it if orders between the chosen lines are missing."),
                    orderGap, 1, 16);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fit Recording")) { viewFrom = 0; viewTo = recording.length; }
                using (new EditorGUI.DisabledScope(!(to > from)))
                    if (GUILayout.Button("Fit Range")) SetTimeView(from, to);
                snapToPeaks = GUILayout.Toggle(snapToPeaks, "Snap to Peaks");
                if (result != null) showHarmonics = GUILayout.Toggle(showHarmonics, "Show Harmonics");
            }
            DrawFrequencyView();
            Rect area = GUILayoutUtility.GetRect(1, 334, GUILayout.ExpandWidth(true));
            var plot = new Rect(area.x + 48, area.y, Mathf.Max(1, area.width - 64), area.height - 36);
            DrawSpectrumScrollbars(plot);
            float top = recordingSpectrum.ToAxis(viewMaxHz);
            plotMinimumAxis = recordingSpectrum.ToAxis(viewMinHz);
            HandleSpectrumInput(plot, top);
            if (Event.current.type == EventType.Repaint) DrawSpectrumPlot(plot, top);
            EditorGUILayout.LabelField("Scroll: frequency   Shift + scroll: time   Ctrl + scroll: zoom at cursor",
                EditorStyles.wordWrappedMiniLabel);
            if (result != null)
            {
                string secondary = result.SupportingOrder > 0 ? "Orange: supporting harmonic   "
                    : result.ReferenceOrder > 0 ? "Orange: second guide   " : "";
                EditorGUILayout.LabelField("Green: tracked harmonic   " + secondary + "Teal: extracted harmonics",
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.LabelField("Selected Range", $"{from:F3}–{to:F3} s  ({to - from:F3} s)");
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!(to > from)))
                    if (GUILayout.Button("Play Selection")) Run(PlaySelectedRange);
                if (selectionPreview && GUILayout.Button("Stop Selection")) StopPreview();
                if (GUILayout.Button("Select Full Recording")) SetRange(0, recording.length);
            }
            if (harmonicPair.HasValue)
            {
                var pair = harmonicPair.Value;
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"Harmonic Pair  {pair.LowerOrder} + {pair.UpperOrder}  ·  {pairTime:F2} s",
                        PairInSelection ? EditorStyles.label : EditorStyles.miniLabel);
                    if (GUILayout.Button("Remove Pair", GUILayout.ExpandWidth(false)))
                    {
                        harmonicPair = null;
                        pendingPairPoint = null;
                        GuidesChanged();
                    }
                }
            }
            if (trackPoints.Count > 0 && GUILayout.Button($"Clear Track Points ({trackPoints.Count})", GUILayout.ExpandWidth(false)))
            {
                trackPoints.Clear();
                GuidesChanged();
            }
        }

        private void DrawFrequencyView()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.PrefixLabel("Frequency View (Hz)");
                float maximumLow = recordingSpectrum.ToHz(1 - MinimumFrequencyAxisSpan);
                float low = EditorGUILayout.DelayedFloatField(viewMinHz, GUILayout.Width(57));
                if (!float.IsNaN(low)) viewMinHz = Mathf.Clamp(low, 0, maximumLow);
                float high = Mathf.Max(viewMaxHz, LowestUpperFrequency());
                EditorGUILayout.MinMaxSlider(ref viewMinHz, ref high, 0, recordingSpectrum.MaxHz);
                viewMinHz = Mathf.Min(viewMinHz, maximumLow);
                float enteredHigh = EditorGUILayout.DelayedFloatField(high, GUILayout.Width(57));
                viewMaxHz = Mathf.Clamp(float.IsNaN(enteredHigh) ? high : enteredHigh,
                    LowestUpperFrequency(), recordingSpectrum.MaxHz);
            }
        }

        private float LowestUpperFrequency() => recordingSpectrum.ToHz(
            Mathf.Min(1, recordingSpectrum.ToAxis(viewMinHz) + MinimumFrequencyAxisSpan));

        private void DrawSpectrumScrollbars(Rect plot)
        {
            float length = viewTo - viewFrom;
            float left = GUI.HorizontalScrollbar(new Rect(plot.x, plot.yMax + 21, plot.width, 14),
                viewFrom, length, 0, recording.length);
            if (left != viewFrom) SetTimeView(left, left + length);

            float top = recordingSpectrum.ToAxis(viewMaxHz);
            float span = top - recordingSpectrum.ToAxis(viewMinHz);
            float position = 1 - top;
            float scrolled = GUI.VerticalScrollbar(new Rect(plot.xMax + 2, plot.y, 14, plot.height),
                position, span, 0, 1);
            if (scrolled != position) SetFrequencyAxisView(1 - scrolled - span, 1 - scrolled);
        }

        private void HandleSpectrumInput(Rect plot, float top)
        {
            int id = GUIUtility.GetControlID(FocusType.Passive, plot);
            var e = Event.current;
            var hover = RangeEdge.None;
            bool inside = plot.Contains(e.mousePosition);
            if (GUI.enabled && spectrumTool == SpectrumTool.Range && to > from)
            {
                if (inside && !spectrumDragging) hover = HitRangeEdge(plot, e.mousePosition.x);
                if (spectrumDragging && draggedRangeEdge != RangeEdge.None)
                    EditorGUIUtility.AddCursorRect(plot, MouseCursor.ResizeHorizontal, id);
                else
                {
                    AddRangeGripCursor(plot, from, id);
                    AddRangeGripCursor(plot, to, id);
                }
            }
            if (hoveredRangeEdge != hover)
            {
                hoveredRangeEdge = hover;
                if (e.type != EventType.Repaint) Repaint();
            }
            if (!GUI.enabled) return;
            var pointer = new Vector2((e.mousePosition.x - plot.x) / plot.width,
                (plot.yMax - e.mousePosition.y) / plot.height);
            float time = Mathf.Lerp(viewFrom, viewTo, pointer.x);
            float hz = recordingSpectrum.ToHz(Mathf.Lerp(plotMinimumAxis, top, pointer.y));
            if (e.type == EventType.ScrollWheel && inside && !spectrumDragging)
            {
                bool moved = ScrollSpectrum(e.delta, e.shift, e.control, pointer);
                if (moved || e.shift || e.control)
                {
                    e.Use();
                    Repaint();
                }
                return;
            }
            if (e.type == EventType.MouseDown && inside)
            {
                if (spectrumTool == SpectrumTool.CorrectTrack && e.button == 1)
                {
                    int point = HitTrackPoint(plot, top, e.mousePosition);
                    if (point >= 0) { trackPoints.RemoveAt(point); GuidesChanged(); }
                    e.Use();
                    return;
                }
                if (e.button != 0) return;
                if (spectrumTool == SpectrumTool.Pair)
                {
                    Run(() => PickHarmonicPair(time, hz, top, plot.height));
                    e.Use();
                    return;
                }
                if (spectrumTool == SpectrumTool.CorrectTrack && (time < from || time > to)) return;
                GUIUtility.hotControl = id;
                spectrumDragging = true;
                if (spectrumTool == SpectrumTool.Range)
                    BeginRangeDrag(hover, time);
                else
                {
                    draggedPoint = HitTrackPoint(plot, top, e.mousePosition);
                    draggedGuide = ReadGuide(time, hz, top, plot.height);
                    if (draggedPoint >= 0) draggedGuide = trackPoints[draggedPoint];
                }
                e.Use();
                Repaint();
                return;
            }
            if (GUIUtility.hotControl != id) return;
            if (e.type == EventType.MouseDrag)
            {
                if (spectrumTool == SpectrumTool.Range)
                {
                    if (draggedRangeEdge != RangeEdge.None || dragTo > dragFrom ||
                        Mathf.Abs(e.mousePosition.x - TimeToX(plot, dragOrigin)) >= 3)
                        UpdateDraggedRange(Mathf.LerpUnclamped(viewFrom, viewTo, pointer.x));
                }
                else draggedGuide = ReadGuide(time, hz, top, plot.height);
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                GUIUtility.hotControl = 0;
                spectrumDragging = false;
                if (spectrumTool == SpectrumTool.Range)
                {
                    if (draggedRangeEdge != RangeEdge.None || dragTo - dragFrom >= MinimumDragRange)
                        SetRange(dragFrom, dragTo);
                    draggedRangeEdge = RangeEdge.None;
                }
                else if (draggedPoint >= 0 || trackPoints.Count < 32)
                {
                    if (draggedPoint >= 0) trackPoints[draggedPoint] = draggedGuide;
                    else trackPoints.Add(draggedGuide);
                    GuidesChanged();
                }
                draggedPoint = -1;
                e.Use();
                Repaint();
            }
        }

        private bool ScrollSpectrum(Vector2 delta, bool shift, bool zoom, Vector2 pointer)
        {
            float bottom = recordingSpectrum.ToAxis(viewMinHz);
            float top = recordingSpectrum.ToAxis(viewMaxHz);
            float span = top - bottom;
            float duration = viewTo - viewFrom;
            bool horizontal = Mathf.Abs(delta.x) > Mathf.Abs(delta.y);
            float steps = horizontal ? delta.x : delta.y;
            if (steps == 0) return false;
            if (zoom)
            {
                float scale = Mathf.Pow(1.12f, Mathf.Clamp(steps, -16, 16));
                float length = Mathf.Clamp(duration * scale, Mathf.Min(.25f, recording.length), recording.length);
                float start = viewFrom + (duration - length) * pointer.x;
                SetTimeView(start, start + length);

                float height = Mathf.Clamp(span * scale, MinimumFrequencyAxisSpan, 1);
                float low = bottom + (span - height) * pointer.y;
                SetFrequencyAxisView(low, low + height);
                return true;
            }
            if (shift || horizontal)
            {
                float old = viewFrom;
                float start = viewFrom + steps * duration * .065f;
                SetTimeView(start, start + duration);
                return viewFrom != old;
            }
            float next = Mathf.Clamp(bottom - steps * span * .065f, 0, 1 - span);
            if (Mathf.Approximately(next, bottom)) return false;
            SetFrequencyAxisView(next, next + span);
            return true;
        }

        private void SetFrequencyAxisView(float bottom, float top)
        {
            float span = Mathf.Clamp(top - bottom, MinimumFrequencyAxisSpan, 1);
            bottom = Mathf.Clamp(bottom, 0, 1 - span);
            viewMinHz = recordingSpectrum.ToHz(bottom);
            viewMaxHz = recordingSpectrum.ToHz(bottom + span);
        }

        private RangeEdge HitRangeEdge(Rect plot, float mouseX)
        {
            float startDistance = from >= viewFrom && from <= viewTo
                ? Mathf.Abs(TimeToX(plot, from) - mouseX) : float.PositiveInfinity;
            float endDistance = to >= viewFrom && to <= viewTo
                ? Mathf.Abs(TimeToX(plot, to) - mouseX) : float.PositiveInfinity;
            if (Mathf.Min(startDistance, endDistance) > RangeGripRadius) return RangeEdge.None;
            return startDistance <= endDistance ? RangeEdge.Start : RangeEdge.End;
        }

        private void AddRangeGripCursor(Rect plot, float time, int id)
        {
            if (time < viewFrom || time > viewTo) return;
            float x = TimeToX(plot, time);
            float left = Mathf.Max(plot.x, x - RangeGripRadius);
            float right = Mathf.Min(plot.xMax, x + RangeGripRadius);
            EditorGUIUtility.AddCursorRect(new Rect(left, plot.y, right - left, plot.height),
                MouseCursor.ResizeHorizontal, id);
        }

        private void BeginRangeDrag(RangeEdge edge, float time)
        {
            draggedRangeEdge = edge;
            if (edge == RangeEdge.None)
            {
                dragOrigin = dragFrom = dragTo = time;
                return;
            }
            rangeGrabOffset = time - (edge == RangeEdge.Start ? from : to);
            dragFrom = from;
            dragTo = to;
        }

        private void PickHarmonicPair(float time, float hz, float top, float height)
        {
            if (pendingPairPoint.HasValue) time = pendingPairPoint.Value.x;
            if (time < from || time > to)
                throw new ArgumentException("Pick harmonic lines inside the selected range.");
            hz = SnapFrequency(time, hz, top, height);
            if (!pendingPairPoint.HasValue)
            {
                pendingPairPoint = new Vector2(time, hz);
                Repaint();
                return;
            }
            harmonicPair = HarmonicPair.FromFrequencies(pendingPairPoint.Value.y, hz, orderGap);
            pairTime = time;
            pendingPairPoint = null;
            GuidesChanged();
        }

        private Vector2 ReadGuide(float time, float hz, float top, float height)
        {
            time = Mathf.Clamp(time, from, to);
            return new Vector2(time, Mathf.Max(1, SnapFrequency(time, hz, top, height)) / VisualTrackingOrder);
        }

        private float SnapFrequency(float time, float hz, float top, float height) => snapToPeaks
            ? Mathf.Clamp(recordingSpectrum.SnapHz(time / recording.length, hz, 6 * (top - plotMinimumAxis) / height),
                viewMinHz, viewMaxHz) : hz;

        private void GuidesChanged()
        {
            error = null;
            guideEditsPending = result != null;
            StopPreview();
            Repaint();
        }

        private void UpdateDraggedRange(float time)
        {
            float minimumLength = Mathf.Min(MinimumDragRange, to - from);
            if (draggedRangeEdge == RangeEdge.Start)
                dragFrom = Mathf.Clamp(time - rangeGrabOffset, 0, to - minimumLength);
            else if (draggedRangeEdge == RangeEdge.End)
                dragTo = Mathf.Clamp(time - rangeGrabOffset, from + minimumLength, recording.length);
            else
            {
                time = Mathf.Clamp(time, 0, recording.length);
                dragFrom = Mathf.Min(dragOrigin, time);
                dragTo = Mathf.Max(dragOrigin, time);
            }
        }

        private void SetRange(float start, float end)
        {
            if (start == from && end == to) return;
            from = start;
            to = end;
            pendingPairPoint = null;
            ClearResult();
        }

        private void SetTimeView(float start, float end)
        {
            float length = Mathf.Clamp(end - start, Mathf.Min(.05f, recording.length), recording.length);
            viewFrom = Mathf.Clamp(start, 0, recording.length - length);
            viewTo = viewFrom + length;
        }

        private int HitTrackPoint(Rect plot, float top, Vector2 mouse)
        {
            for (int i = trackPoints.Count - 1; i >= 0; i--)
            {
                var point = trackPoints[i];
                if ((PlotPoint(plot, top, point.x, point.y * VisualTrackingOrder) - mouse).sqrMagnitude <= 64) return i;
            }
            return -1;
        }

        private float TimeToX(Rect plot, float time) => plot.x + plot.width * (time - viewFrom) / (viewTo - viewFrom);

        private Vector2 PlotPoint(Rect plot, float top, float time, float hz) => new Vector2(
            TimeToX(plot, time),
            plot.yMax - plot.height * (recordingSpectrum.ToAxis(hz) - plotMinimumAxis) / (top - plotMinimumAxis));

        private void DrawSpectrumPlot(Rect plot, float top)
        {
            GUI.DrawTextureWithTexCoords(plot, spectrumTexture,
                new Rect(viewFrom / recording.length, plotMinimumAxis, (viewTo - viewFrom) / recording.length, top - plotMinimumAxis), false);
            float lastLabel = float.PositiveInfinity;
            foreach (float hz in FrequencyTicks)
            {
                float y = PlotPoint(plot, top, viewFrom, hz).y;
                if (y < plot.y || y > plot.yMax || lastLabel - y < 17) continue;
                lastLabel = y;
                GUI.Label(new Rect(plot.x - 48, Mathf.Min(y - 8, plot.yMax - 13), 46, 16),
                    hz >= 1000 ? $"{hz / 1000:g} kHz" : $"{hz:g} Hz", EditorStyles.miniLabel);
                EditorGUI.DrawRect(new Rect(plot.x, y, plot.width, 1), new Color(1, 1, 1, .09f));
            }
            for (int i = 0; i <= 4; i++)
            {
                float x = Mathf.Lerp(plot.x, plot.xMax, i / 4f);
                float time = Mathf.Lerp(viewFrom, viewTo, i / 4f);
                float labelX = Mathf.Clamp(x - 31, plot.x, Mathf.Max(plot.x, plot.xMax - 62));
                GUI.Label(new Rect(labelX, plot.yMax + 2, 62, 16), $"{time:F2} s", EditorStyles.centeredGreyMiniLabel);
            }

            bool selecting = spectrumTool == SpectrumTool.Range && spectrumDragging &&
                (draggedRangeEdge != RangeEdge.None || dragTo > dragFrom);
            float rangeStart = selecting ? dragFrom : from;
            float rangeEnd = selecting ? dragTo : to;
            float left = Mathf.Clamp(TimeToX(plot, rangeStart), plot.x, plot.xMax);
            float right = Mathf.Clamp(TimeToX(plot, rangeEnd), plot.x, plot.xMax);
            var shade = new Color(.01f, .02f, .04f, .66f);
            EditorGUI.DrawRect(new Rect(plot.x, plot.y, left - plot.x, plot.height), shade);
            EditorGUI.DrawRect(new Rect(right, plot.y, plot.xMax - right, plot.height), shade);
            DrawRangeGrip(plot, rangeStart, RangeEdge.Start);
            if (rangeEnd > rangeStart) DrawRangeGrip(plot, rangeEnd, RangeEdge.End);

            GUI.BeginGroup(plot);
            var local = new Rect(0, 0, plot.width, plot.height);
            Handles.BeginGUI();
            var oldColor = Handles.color;
            if (result != null)
            {
                if (showHarmonics)
                    foreach (int order in displayedOrders)
                        DrawRidge(local, top, order, new Color(.47f, .87f, .87f, .38f), 1f);
                int secondOrder = Math.Max(result.ReferenceOrder, result.SupportingOrder);
                if (secondOrder > 0) DrawRidge(local, top, secondOrder, PairColor, 1.5f);
                DrawRidge(local, top, result.TrackingOrder, TrackColor, 2f);
            }
            Handles.color = oldColor;
            Handles.EndGUI();
            if (harmonicPair.HasValue)
            {
                var pair = harmonicPair.Value;
                DrawDot(local, top, pairTime, pair.CycleHz * pair.LowerOrder, PairColor);
                DrawDot(local, top, pairTime, pair.CycleHz * pair.UpperOrder, PairColor);
            }
            if (pendingPairPoint.HasValue)
            {
                var point = pendingPairPoint.Value;
                float x = PlotPoint(local, top, point.x, point.y).x;
                EditorGUI.DrawRect(new Rect(x, 0, 1, local.height), PairColor);
                DrawDot(local, top, point.x, point.y, PairColor);
            }
            for (int i = 0; i < trackPoints.Count; i++)
            {
                if (spectrumDragging && spectrumTool == SpectrumTool.CorrectTrack && i == draggedPoint) continue;
                var point = trackPoints[i];
                DrawDot(local, top, point.x, point.y * VisualTrackingOrder, GuideColor);
            }
            if (spectrumDragging && spectrumTool == SpectrumTool.CorrectTrack)
                DrawDot(local, top, draggedGuide.x, draggedGuide.y * VisualTrackingOrder, GuideColor);
            if (useRpmAnchors)
            {
                float hzPerRpm = VisualTrackingOrder / (60f * Revolutions);
                DrawDot(local, top, from, startRpm * hzPerRpm, PairColor);
                DrawDot(local, top, to, endRpm * hzPerRpm, PairColor);
                foreach (var checkpoint in rpmCheckpoints)
                    DrawDot(local, top, checkpoint.x, checkpoint.y * hzPerRpm, PairColor);
            }
            GUI.EndGroup();
        }

        private void DrawRangeGrip(Rect plot, float time, RangeEdge edge)
        {
            if (time < viewFrom || time > viewTo) return;
            bool handlesVisible = spectrumTool == SpectrumTool.Range;
            bool highlighted = handlesVisible && (spectrumDragging ? draggedRangeEdge == edge : hoveredRangeEdge == edge);
            float x = TimeToX(plot, time);
            var color = highlighted ? new Color(.36f, .91f, 1f) : new Color(.67f, .80f, 1f, .85f);
            float width = Mathf.Min(plot.width, highlighted ? 4 : 2);
            EditorGUI.DrawRect(new Rect(Mathf.Clamp(x - width / 2, plot.x, plot.xMax - width), plot.y,
                width, plot.height), color);
            if (!handlesVisible) return;
            float gripWidth = Mathf.Min(plot.width, highlighted ? 14 : 11);
            var grip = new Rect(Mathf.Clamp(x - gripWidth / 2, plot.x, plot.xMax - gripWidth), plot.y + 5, gripWidth, 19);
            EditorGUI.DrawRect(grip, color);
            var gripDetail = new Color(.10f, .21f, .29f, .85f);
            EditorGUI.DrawRect(new Rect(grip.center.x - 2, grip.y + 4, 1, 11), gripDetail);
            EditorGUI.DrawRect(new Rect(grip.center.x + 1, grip.y + 4, 1, 11), gripDetail);
        }

        private void DrawRidge(Rect plot, float top, int order, Color color, float width)
        {
            float hzPerRpm = order / (60f * result.Data.RevolutionsPerCycle);
            Handles.color = color;
            int stride = Mathf.Max(1, result.Times.Length / 220);
            var previous = PlotPoint(plot, top, from + result.Times[0], result.TrackedRpm[0] * hzPerRpm);
            for (int i = 1; i < result.Times.Length;)
            {
                var next = PlotPoint(plot, top, from + result.Times[i], result.TrackedRpm[i] * hzPerRpm);
                if (next.x >= plot.x && previous.x <= plot.xMax && (next.y >= plot.y || previous.y >= plot.y))
                    Handles.DrawLine(previous, next, width);
                previous = next;
                if (i == result.Times.Length - 1) break;
                i = Mathf.Min(result.Times.Length - 1, i + stride);
            }
        }

        private void DrawDot(Rect plot, float top, float time, float hz, Color color)
        {
            Vector2 p = PlotPoint(plot, top, time, hz);
            if (p.x < plot.x || p.x > plot.xMax || p.y < plot.y || p.y > plot.yMax) return;
            EditorGUI.DrawRect(new Rect(p.x - 4, p.y - 4, 8, 8), color);
        }

        private static int[] DisplayedHarmonics(SweepAnalyzer.Result analysisResult)
        {
            var data = analysisResult.Data;
            var powers = new float[data.Harmonics];
            float strongest = 0;
            for (int key = 0; key < data.Rpm.Length; key++)
                for (int h = 0; h < powers.Length; h++)
                {
                    int i = key * powers.Length + h;
                    float power = data.Cosine[i] * data.Cosine[i] + data.Sine[i] * data.Sine[i];
                    powers[h] = Mathf.Max(powers[h], power);
                    strongest = Mathf.Max(strongest, power);
                }
            var orders = new List<int>();
            for (int h = 0; h < powers.Length; h++)
                if (powers[h] > strongest * .000025f && h + 1 != analysisResult.TrackingOrder
                    && h + 1 != analysisResult.ReferenceOrder && h + 1 != analysisResult.SupportingOrder)
                    orders.Add(h + 1);
            orders.Sort((a, b) => powers[b - 1].CompareTo(powers[a - 1]));
            if (orders.Count > 36) orders.RemoveRange(36, orders.Count - 36);
            return orders.ToArray();
        }
    }
}
