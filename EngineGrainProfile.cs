using System.IO;
using UnityEngine;

namespace RpmSynth
{
    public sealed class EngineGrainProfile : ScriptableObject
    {
        [SerializeField, HideInInspector] private byte[] recording;
        private EngineGrainData cached;

        public EngineGrainData Data
        {
            get
            {
                if (cached == null)
                    using (var stream = new MemoryStream(recording, writable: false))
                        cached = EngineGrainData.Load(stream);
                return cached;
            }
        }

        public void SetRecording(byte[] data)
        {
            recording = data;
            cached = null;
        }

        public EngineGrainSynth CreateSynth(int sampleRate, float rpm) => new EngineGrainSynth(Data, sampleRate, rpm);
    }
}
