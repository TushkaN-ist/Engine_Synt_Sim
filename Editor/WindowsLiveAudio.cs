#if UNITY_EDITOR_WIN
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RpmSynth.Editor
{
    internal sealed class WindowsLiveAudio : IDisposable
    {
        internal const int SampleRate = 48000;
        private const int BlockSamples = 960;
        private const int BufferCount = 3;
        private const uint CallbackEvent = 0x00050000;
        private const int HeaderDone = 1;

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public ushort Format, Channels;
            public uint SampleRate, BytesPerSecond;
            public ushort BlockAlign, BitsPerSample, ExtraSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr Data;
            public uint Length, BytesRecorded;
            public UIntPtr User;
            public int Flags;
            public uint Loops;
            public IntPtr Next;
            public UIntPtr Reserved;
        }

        private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
        private static readonly int FlagsOffset = Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags)).ToInt32();

        private sealed class Buffer
        {
            public IntPtr Data, Header;
            public bool Prepared, Submitted;
        }

        private readonly AutoResetEvent ready = new AutoResetEvent(false);
        private readonly Buffer[] buffers = new Buffer[BufferCount];
        private readonly float[] samples = new float[BlockSamples];
        private readonly short[] pcm = new short[BlockSamples];
        private readonly Action<float[]> render;
        private Thread thread;
        private IntPtr device;
        private volatile bool stopping;
        private string error;

        public string Error => Volatile.Read(ref error);

        public WindowsLiveAudio(Action<float[]> render)
        {
            this.render = render;
            try
            {
                var format = new WaveFormat
                {
                    Format = 1, Channels = 1, SampleRate = SampleRate, BytesPerSecond = SampleRate * 2,
                    BlockAlign = 2, BitsPerSample = 16
                };
                Check(waveOutOpen(out device, uint.MaxValue, ref format,
                    ready.SafeWaitHandle.DangerousGetHandle(), IntPtr.Zero, CallbackEvent), "opening the audio device");
                for (int i = 0; i < buffers.Length; i++)
                {
                    var buffer = new Buffer();
                    buffers[i] = buffer;
                    buffer.Data = Marshal.AllocHGlobal(BlockSamples * 2);
                    buffer.Header = Marshal.AllocHGlobal((int)HeaderSize);
                    Marshal.StructureToPtr(new WaveHeader { Data = buffer.Data, Length = BlockSamples * 2 },
                        buffer.Header, false);
                    Check(waveOutPrepareHeader(device, buffer.Header, HeaderSize), "preparing the buffer");
                    buffer.Prepared = true;
                }
                thread = new Thread(Play) { IsBackground = true, Name = "Engine sound preview",
                    Priority = ThreadPriority.AboveNormal };
                thread.Start();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void Play()
        {
            try
            {
                int next = 0;
                while (!stopping)
                {
                    var buffer = buffers[next];
                    if (buffer.Submitted && (Marshal.ReadInt32(buffer.Header, FlagsOffset) & HeaderDone) == 0)
                    {
                        ready.WaitOne();
                        continue;
                    }
                    render(samples);
                    for (int i = 0; i < samples.Length; i++)
                        pcm[i] = (short)(samples[i] * 32767f);
                    Marshal.Copy(pcm, 0, buffer.Data, pcm.Length);
                    Check(waveOutWrite(device, buffer.Header, HeaderSize), "submitting an audio buffer");
                    buffer.Submitted = true;
                    next = (next + 1) % buffers.Length;
                }
            }
            catch (Exception exception)
            {
                Volatile.Write(ref error, exception.Message);
            }
        }

        public void Dispose()
        {
            if (stopping) return;
            stopping = true;
            ready.Set();
            if (thread != null && thread.IsAlive) thread.Join();

            if (device != IntPtr.Zero) waveOutReset(device);
            foreach (var buffer in buffers)
            {
                if (buffer == null) continue;
                if (buffer.Prepared) waveOutUnprepareHeader(device, buffer.Header, HeaderSize);
                if (buffer.Header != IntPtr.Zero) Marshal.FreeHGlobal(buffer.Header);
                if (buffer.Data != IntPtr.Zero) Marshal.FreeHGlobal(buffer.Data);
            }
            if (device != IntPtr.Zero) waveOutClose(device);
            ready.Dispose();
        }

        private static void Check(uint code, string operation)
        {
            if (code != 0)
                throw new InvalidOperationException($"Windows audio output failed while {operation} (error {code}).");
        }

        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format,
            IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint waveOutReset(IntPtr device);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
        [DllImport("winmm.dll", ExactSpelling = true)]
        private static extern uint waveOutClose(IntPtr device);
    }
}
#endif
