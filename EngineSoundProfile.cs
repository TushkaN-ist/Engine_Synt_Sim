using System;
using UnityEngine;

namespace RpmSynth
{
    public sealed class EngineSoundProfile : ScriptableObject
    {
        [SerializeField, HideInInspector] private int revolutionsPerCycle;
        [SerializeField, HideInInspector] private int harmonicCount;
        [SerializeField, HideInInspector] private float[] rpm;
        [SerializeField, HideInInspector] private float[] cosine;
        [SerializeField, HideInInspector] private float[] sine;
        [SerializeField, HideInInspector] private float[] residual;

        public float MinimumRpm => rpm == null || rpm.Length == 0 ? 0 : rpm[0];
        public float MaximumRpm => rpm == null || rpm.Length == 0 ? 0 : rpm[rpm.Length - 1];

        public EngineSoundSynth CreateSynth(int sampleRate, float initialRpm, bool interactive = false)
        {
            return new EngineSoundSynth(GetData(), sampleRate, initialRpm, interactive);
        }

        public float[] RenderLoop(float speed, float seconds, int sampleRate)
        {
            return EngineSoundSynth.RenderLoop(GetData(), speed, seconds, sampleRate);
        }

        public void SetData(EngineSoundData data)
        {
            revolutionsPerCycle = data.RevolutionsPerCycle;
            harmonicCount = data.Harmonics;
            rpm = data.Rpm;
            cosine = data.Cosine;
            sine = data.Sine;
            residual = data.Residual;
        }

        private EngineSoundData GetData()
        {
            if (rpm == null || rpm.Length < 2)
                throw new InvalidOperationException("Generate the engine profile from a recording first.");
            return new EngineSoundData(revolutionsPerCycle, harmonicCount, rpm, cosine, sine, residual);
        }
    }
}
