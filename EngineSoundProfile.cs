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
        [SerializeField, HideInInspector] private float[] residualSpectrum;
        [SerializeField, HideInInspector] private float[] residualModulation;
        [SerializeField, HideInInspector] private float[] focusedResidualModulation;
        [SerializeField, HideInInspector] private float[] bandResidualModulation;
        [SerializeField, HideInInspector] private float[] mechanicalEnvelope;
        [SerializeField, HideInInspector] private float[] resonantAttacks;
        [SerializeField, HideInInspector] private float backgroundTrimDb;
        [SerializeField, HideInInspector] private bool hasSavedBackgroundTrim;
        [SerializeField, HideInInspector] private float noiseAndVariation;
        [SerializeField, HideInInspector] private bool hasSavedNoiseAndVariation;

        public float MinimumRpm => rpm == null || rpm.Length == 0 ? 0 : rpm[0];
        public float MaximumRpm => rpm == null || rpm.Length == 0 ? 0 : rpm[rpm.Length - 1];
        public bool HasMechanicalDetail => rpm != null && rpm.Length >= 2 &&
            mechanicalEnvelope?.Length == rpm.Length * EngineSoundData.MechanicalPhaseBins;
        public bool HasResonantAttacks => rpm != null && EngineAttackData.HasAttacks(resonantAttacks, rpm.Length);
        public bool HasResidualSpectrum => rpm != null && residualSpectrum?.Length == rpm.Length * EngineResidualNoise.BandCount;
        public bool HasSavedBackgroundTrim => hasSavedBackgroundTrim;
        public float BackgroundTrimDb => backgroundTrimDb;
        public bool HasSavedNoiseAndVariation => hasSavedNoiseAndVariation;
        public float NoiseAndVariation => hasSavedNoiseAndVariation ? noiseAndVariation : 0.5f;

        public void SetBackgroundTrim(float trimDb)
        {
            backgroundTrimDb = EngineResidualNoise.ClampBackgroundTrim(trimDb);
            hasSavedBackgroundTrim = true;
        }

        public void SetNoiseAndVariation(float amount)
        {
            noiseAndVariation = float.IsNaN(amount) ? 0.5f : Mathf.Clamp01(amount);
            hasSavedNoiseAndVariation = true;
        }

        public EngineSoundSynth CreateSynth(int sampleRate, float initialRpm, bool interactive = false)
        {
            return new EngineSoundSynth(GetData(), sampleRate, initialRpm, interactive, BackgroundTrimDb);
        }

        public float[] RenderLoop(float speed, float seconds, int sampleRate, float mechanicalDetail = 1f,
            float? texture = null, float resonantDetail = 1f, float? backgroundTrimDb = null)
        {
            return EngineSoundSynth.RenderLoop(GetData(), speed, seconds, sampleRate, mechanicalDetail, texture ?? NoiseAndVariation,
                resonantDetail, backgroundTrimDb ?? BackgroundTrimDb);
        }

        public void SetData(EngineSoundData data)
        {
            revolutionsPerCycle = data.RevolutionsPerCycle;
            harmonicCount = data.Harmonics;
            rpm = data.Rpm;
            cosine = data.Cosine;
            sine = data.Sine;
            residual = data.Residual;
            residualSpectrum = data.ResidualSpectrum;
            residualModulation = data.ResidualModulation;
            focusedResidualModulation = data.FocusedResidualModulation;
            bandResidualModulation = data.BandResidualModulation;
            mechanicalEnvelope = data.MechanicalEnvelope;
            resonantAttacks = data.ResonantAttacks;
        }

        private EngineSoundData GetData()
        {
            if (rpm == null || rpm.Length < 2)
                throw new InvalidOperationException("Generate the engine profile from a recording first.");
            return new EngineSoundData(revolutionsPerCycle, harmonicCount, rpm, cosine, sine, residual,
                mechanicalEnvelope, residualSpectrum, resonantAttacks, residualModulation, focusedResidualModulation,
                bandResidualModulation);
        }
    }
}
