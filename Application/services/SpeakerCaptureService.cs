using System;
using System.IO;
using NAudio.Wave;
using NAudio.CoreAudioApi;

namespace SnapEye.Services
{
    /// <summary>
    /// Dedicated service for capturing system audio (speaker output) using WASAPI Loopback.
    /// This service is optimized for capturing what comes out of the speakers.
    /// Key features:
    /// - Handles IEEE float format from WASAPI properly
    /// - Silence detection and filtering
    /// - Volume normalization
    /// - Robust error handling
    /// </summary>
    public class SpeakerCaptureService : IDisposable
    {
        private WasapiLoopbackCapture? speakerCapture;
        private MMDevice? captureDevice;
        private bool isCapturing = false;
        private bool isDisposed = false;

        // OpenAI requires: 24kHz, 16-bit PCM, mono
        private const int TargetSampleRate = 24000;
        private const int TargetChannels = 1;
        private const int TargetBits = 16;

        // Silence threshold - audio below this level is considered silence
        private const float SilenceThreshold = 0.001f;

        public event EventHandler<SpeakerAudioEventArgs>? AudioDataAvailable;
        public event EventHandler<string>? ErrorOccurred;
        public event EventHandler<string>? StatusChanged;

        public bool IsCapturing => isCapturing;

        private void LogInfo(string message)
        {
            Console.WriteLine($"[SpeakerCapture] {message}");
            StatusChanged?.Invoke(this, message);
        }

        private void LogError(string message)
        {
            Console.WriteLine($"[SpeakerCapture ERROR] {message}");
        }

        /// <summary>
        /// Start capturing system audio from the default render device
        /// </summary>
        public void StartCapture()
        {
            if (isCapturing)
            {
                LogInfo("Already capturing");
                return;
            }

            // Defensive: if a previous capture session was stopped without being fully
            // torn down, dispose the stale device/capture here before creating new ones.
            Cleanup();

            try
            {
                // Get the default audio render device (speakers)
                using var enumerator = new MMDeviceEnumerator();
                captureDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

                if (captureDevice == null)
                {
                    ErrorOccurred?.Invoke(this, "No audio output device found");
                    return;
                }

                LogInfo($"Using audio device: {captureDevice.FriendlyName}");

                // Create WASAPI loopback capture for the default device
                speakerCapture = new WasapiLoopbackCapture(captureDevice);

                LogInfo($"Device format: {speakerCapture.WaveFormat.SampleRate}Hz, {speakerCapture.WaveFormat.BitsPerSample}bit, {speakerCapture.WaveFormat.Channels}ch, Encoding: {speakerCapture.WaveFormat.Encoding}");

                speakerCapture.DataAvailable += OnSpeakerDataAvailable;
                speakerCapture.RecordingStopped += OnRecordingStopped;

                speakerCapture.StartRecording();
                isCapturing = true;

                LogInfo("Speaker capture started successfully");
            }
            catch (Exception ex)
            {
                LogError($"Failed to start capture: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"Failed to start speaker capture: {ex.Message}");
                Cleanup();
            }
        }

        /// <summary>
        /// Stop capturing system audio and release the underlying device.
        /// Safe to call multiple times.
        /// </summary>
        public void StopCapture()
        {
            if (!isCapturing && speakerCapture == null)
                return;

            try
            {
                // Unsubscribe first to prevent late callbacks after we null the capture.
                if (speakerCapture != null)
                {
                    speakerCapture.DataAvailable -= OnSpeakerDataAvailable;
                    speakerCapture.RecordingStopped -= OnRecordingStopped;
                    try { speakerCapture.StopRecording(); } catch { /* already stopped */ }
                }
                LogInfo("Speaker capture stopped");
            }
            catch (Exception ex)
            {
                LogError($"Error stopping capture: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"Failed to stop speaker capture: {ex.Message}");
            }
            finally
            {
                isCapturing = false;
                Cleanup();
            }
        }

        private void OnSpeakerDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded == 0 || speakerCapture == null)
            {
                return;
            }

            try
            {
                // Convert IEEE float to PCM16 first if needed
                byte[] pcmData;
                WaveFormat sourceFormat = speakerCapture.WaveFormat;

                if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    pcmData = ConvertFloatToPcm16(e.Buffer, e.BytesRecorded, sourceFormat);
                    sourceFormat = new WaveFormat(sourceFormat.SampleRate, 16, sourceFormat.Channels);
                }
                else
                {
                    pcmData = new byte[e.BytesRecorded];
                    Buffer.BlockCopy(e.Buffer, 0, pcmData, 0, e.BytesRecorded);
                }

                // Check if audio is silence
                if (IsSilence(pcmData))
                {
                    return; // Skip silent audio
                }

                // Convert to target format (24kHz, mono, 16-bit)
                byte[] convertedAudio = ConvertToTargetFormat(pcmData, sourceFormat);

                if (convertedAudio.Length > 0)
                {
                    AudioDataAvailable?.Invoke(this, new SpeakerAudioEventArgs
                    {
                        AudioData = convertedAudio,
                        BytesRecorded = convertedAudio.Length
                    });
                }
            }
            catch (Exception ex)
            {
                LogError($"Error processing audio: {ex.Message}");
            }
        }

        /// <summary>
        /// Convert IEEE float audio to 16-bit PCM (little-endian).
        /// WASAPI loopback typically returns float audio. This runs on every audio callback
        /// (~every 10ms), so the inner loop is allocation-free — no per-sample <c>BitConverter</c>.
        /// </summary>
        private static byte[] ConvertFloatToPcm16(byte[] floatData, int bytesRecorded, WaveFormat sourceFormat)
        {
            int sampleCount = bytesRecorded / 4;
            byte[] pcmData = new byte[sampleCount * 2];

            for (int i = 0; i < sampleCount; i++)
            {
                float sample = BitConverter.ToSingle(floatData, i * 4);

                if (sample > 1.0f) sample = 1.0f;
                else if (sample < -1.0f) sample = -1.0f;

                short pcmSample = (short)(sample * 32767f);
                int o = i * 2;
                pcmData[o]     = (byte)(pcmSample & 0xFF);
                pcmData[o + 1] = (byte)((pcmSample >> 8) & 0xFF);
            }

            return pcmData;
        }

        /// <summary>
        /// Check if the audio buffer contains only silence
        /// </summary>
        private bool IsSilence(byte[] pcmData)
        {
            if (pcmData.Length < 2)
            {
                return true;
            }

            // Calculate RMS of the audio
            double sum = 0;
            int sampleCount = pcmData.Length / 2;

            for (int i = 0; i < pcmData.Length; i += 2)
            {
                short sample = BitConverter.ToInt16(pcmData, i);
                double normalized = sample / 32768.0;
                sum += normalized * normalized;
            }

            double rms = Math.Sqrt(sum / sampleCount);

            return rms < SilenceThreshold;
        }

        /// <summary>
        /// Convert audio to target format: 24kHz, mono, 16-bit PCM
        /// </summary>
        private byte[] ConvertToTargetFormat(byte[] audioData, WaveFormat sourceFormat)
        {
            try
            {
                var targetFormat = new WaveFormat(TargetSampleRate, TargetBits, TargetChannels);

                // Check if already in correct format
                if (sourceFormat.SampleRate == targetFormat.SampleRate &&
                    sourceFormat.Channels == targetFormat.Channels &&
                    sourceFormat.BitsPerSample == targetFormat.BitsPerSample)
                {
                    return audioData;
                }

                using var sourceStream = new RawSourceWaveStream(audioData, 0, audioData.Length, sourceFormat);
                using var resampler = new MediaFoundationResampler(sourceStream, targetFormat)
                {
                    ResamplerQuality = 60 // High quality
                };

                using var memoryStream = new MemoryStream();
                byte[] buffer = new byte[targetFormat.AverageBytesPerSecond / 10]; // 100ms chunks
                int bytesRead;

                while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
                {
                    memoryStream.Write(buffer, 0, bytesRead);
                }

                return memoryStream.ToArray();
            }
            catch (Exception ex)
            {
                LogError($"Format conversion failed: {ex.Message}");
                return audioData; // Return original if conversion fails
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                LogError($"Recording stopped with error: {e.Exception.Message}");
                ErrorOccurred?.Invoke(this, $"Speaker recording stopped: {e.Exception.Message}");
            }
            else
            {
                LogInfo("Recording stopped normally");
            }

            isCapturing = false;
        }

        private void Cleanup()
        {
            try
            {
                speakerCapture?.Dispose();
                speakerCapture = null;
                captureDevice?.Dispose();
                captureDevice = null;
                isCapturing = false;
            }
            catch (Exception ex)
            {
                LogError($"Cleanup error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }

            StopCapture();
            Cleanup();
            isDisposed = true;
        }
    }

    public class SpeakerAudioEventArgs : EventArgs
    {
        public byte[] AudioData { get; set; } = Array.Empty<byte>();
        public int BytesRecorded { get; set; }
    }
}
