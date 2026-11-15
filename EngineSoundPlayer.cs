using System.Threading;
using UnityEngine;

namespace RpmSynth
{
    [AddComponentMenu("Audio/Engine Sound Player")]
    [RequireComponent(typeof(AudioSource))]
    public sealed class EngineSoundPlayer : MonoBehaviour
    {
        [SerializeField] private EngineSoundProfile profile;
        [SerializeField, Min(0)] private float rpm = 1000;
        [SerializeField, Range(0, 1)] private float load = 1;
        [SerializeField, Range(0, 1)] private float gain = 0.8f;
        [SerializeField, Range(0, 1)] private float texture = 0.5f;
        [SerializeField, Range(0, 2)] private float mechanicalDetail = 1f;
        [SerializeField, Range(0, 2)] private float resonantAttacks = 1f;
        // Keep previously serialized instance settings until the profile has a saved trim.
        [SerializeField, HideInInspector]
        private float backgroundTrimDb;
        private AudioSource source;
        private EngineSoundProfile playbackProfile;
        private AudioClip engineClip, previousClip;
        private bool previousLoop;
        private EngineSoundSynth synth;
        private int sampleRate;
        private float[] block;
        private int playhead, writeHead, queued;

        /// <summary>Sets crankshaft RPM, including speeds outside the recorded profile, and normalized load.</summary>
        public void SetEngineState(float engineRpm, float engineLoad)
        {
            Volatile.Write(ref rpm, engineRpm);
            Volatile.Write(ref load, engineLoad);
        }

        private void OnEnable()
        {
            if (profile == null || profile.MaximumRpm <= profile.MinimumRpm)
                return;
            playbackProfile = profile;
            source = GetComponent<AudioSource>();
            previousClip = source.clip;
            previousLoop = source.loop;
            StartPlayback();
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
        }

        private void StartPlayback()
        {
            sampleRate = AudioSettings.outputSampleRate;
            AudioSettings.GetDSPBufferSize(out int blockSize, out _);
            block = new float[blockSize];
            synth = playbackProfile.CreateSynth(sampleRate, Volatile.Read(ref rpm), interactive: true);
            engineClip = AudioClip.Create("Engine synthesis", blockSize * 16, 1, sampleRate, false);
            playhead = writeHead = queued = 0;
            FillAhead(0);
            source.clip = engineClip;
            source.loop = true;
            source.Play();
        }

        private void Update()
        {
            if (engineClip == null || source.clip != engineClip || !source.isPlaying) return;
            int cursor = source.timeSamples;
            int advanced = (cursor - playhead + engineClip.samples) % engineClip.samples;
            playhead = cursor;
            queued -= advanced;
            if (queued < 0)
            {
                // Leave the mixer one block of room before writing after a stall or seek.
                queued = block.Length;
                writeHead = (cursor + queued) % engineClip.samples;
            }
            FillAhead(Time.unscaledDeltaTime);
        }

        private void FillAhead(float frameSeconds)
        {
            // Write into the mono clip before Unity applies volume, pitch and spatialization.
            float frames = Mathf.Max(2f * block.Length, frameSeconds * sampleRate);
            int target = Mathf.Clamp(Mathf.CeilToInt((frames + block.Length) * Mathf.Max(1f, source.pitch)),
                block.Length * 3, engineClip.samples - block.Length * 2);
            float trim = playbackProfile != null && playbackProfile.HasSavedBackgroundTrim
                ? playbackProfile.BackgroundTrimDb : backgroundTrimDb;
            while (queued < target)
            {
                synth.Render(block, Volatile.Read(ref rpm), Volatile.Read(ref load),
                    Volatile.Read(ref gain), Volatile.Read(ref texture), Volatile.Read(ref mechanicalDetail),
                    Volatile.Read(ref resonantAttacks), trim);
                engineClip.SetData(block, writeHead);
                writeHead = (writeHead + block.Length) % engineClip.samples;
                queued += block.Length;
            }
        }

        private void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            if (engineClip == null || source.clip != engineClip || playbackProfile == null) return;
            int rate = AudioSettings.outputSampleRate;
            AudioSettings.GetDSPBufferSize(out int blockSize, out _);
            if (rate != sampleRate || blockSize != block.Length)
            {
                var oldClip = engineClip;
                StartPlayback();
                Destroy(oldClip);
            }
        }

        private void OnDisable()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
            synth = null;
            playbackProfile = null;
            if (engineClip == null) return;
            if (source != null && source.clip == engineClip)
            {
                source.Stop();
                source.clip = previousClip;
                source.loop = previousLoop;
            }
            Destroy(engineClip);
            engineClip = null;
        }
    }
}
