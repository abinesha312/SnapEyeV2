using System;
using System.IO;
using NAudio.Wave;

namespace SnapEye.Services
{
    /// <summary>
    /// Dedicated microphone capture service using WaveInEvent.
    /// This service captures audio from the default microphone input device.
    /// For speaker/system audio capture, use SpeakerCaptureService instead.
    /// </summary>
    public class AudioCaptureService : IDisposable
    {
        private WaveInEvent? microphoneCapture;
        private bool isCapturing = false;

        // OpenAI requires: 24kHz, 16-bit PCM, mono
        private const int TargetSampleRate = 24000;
        private const int TargetChannels = 1;
        private const int TargetBits = 16;

        public event EventHandler<AudioDataEventArgs>? MicrophoneDataAvailable;
        public event EventHandler<string>? ErrorOccurred;

        private void LogInfo(string message)
        {
            Console.WriteLine($"[AudioCapture] {message}");
        }

        public bool IsCapturing => isCapturing;

        // Start capturing microphone audio only
        public void StartCapture()
        {
            if (isCapturing) return;

            try
            {
                // Initialize microphone capture (User input) using WaveInEvent
                // Uses standard Windows API for microphone input
                microphoneCapture = new WaveInEvent
                {
                    DeviceNumber = 0, // Default microphone
                    WaveFormat = new WaveFormat(16000, 1), // 16kHz, mono (will be resampled to 24kHz)
                    BufferMilliseconds = 50 // Low latency
                };

                microphoneCapture.DataAvailable += OnMicrophoneDataAvailable;
                microphoneCapture.RecordingStopped += OnRecordingStopped;
                microphoneCapture.StartRecording();
                LogInfo($"Microphone format: {microphoneCapture.WaveFormat}");

                isCapturing = true;
                LogInfo("Microphone capture started successfully");
            }
            catch (Exception ex)
            {
                LogInfo($"Failed to start microphone capture: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"Failed to start microphone capture: {ex.Message}");
            }
        }

        // Stop capturing microphone audio
        public void StopCapture()
        {
            if (!isCapturing) return;

            try
            {
                microphoneCapture?.StopRecording();
                microphoneCapture?.Dispose();
                microphoneCapture = null;
                isCapturing = false;
                LogInfo("Microphone capture stopped");
            }
            catch (Exception ex)
            {
                LogInfo($"Failed to stop microphone capture: {ex.Message}");
                ErrorOccurred?.Invoke(this, $"Failed to stop microphone capture: {ex.Message}");
            }
        }

        private void OnMicrophoneDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded > 0 && microphoneCapture != null)
            {
                byte[] audioData = new byte[e.BytesRecorded];
                Buffer.BlockCopy(e.Buffer, 0, audioData, 0, e.BytesRecorded);
                
                // Convert to OpenAI format: 24kHz, mono, 16-bit PCM
                byte[] convertedAudio = ConvertToOpenAIFormat(audioData, microphoneCapture.WaveFormat);
                
                MicrophoneDataAvailable?.Invoke(this, new AudioDataEventArgs
                {
                    AudioData = convertedAudio,
                    BytesRecorded = convertedAudio.Length,
                    Source = AudioSource.Microphone
                });
            }
        }

        /// <summary>
        /// Convert audio to OpenAI Real-time API format: 24kHz, mono, 16-bit PCM
        /// </summary>
        private byte[] ConvertToOpenAIFormat(byte[] audioData, WaveFormat sourceFormat)
        {
            try
            {
                using var sourceStream = new RawSourceWaveStream(audioData, 0, audioData.Length, sourceFormat);
                
                // Target format for OpenAI: 24kHz, 16-bit, mono
                var targetFormat = new WaveFormat(TargetSampleRate, TargetBits, TargetChannels);
                
                // Check if conversion is needed
                if (sourceFormat.SampleRate == targetFormat.SampleRate &&
                    sourceFormat.Channels == targetFormat.Channels &&
                    sourceFormat.BitsPerSample == targetFormat.BitsPerSample)
                {
                    return audioData; // Already in correct format
                }
                
                // Convert to target format
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
                ErrorOccurred?.Invoke(this, $"Audio conversion failed: {ex.Message}");
                return audioData; // Return original if conversion fails
            }
        }

        private void OnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
            {
                ErrorOccurred?.Invoke(this, $"Recording stopped with error: {e.Exception.Message}");
            }
        }

        public void Dispose()
        {
            StopCapture();
            microphoneCapture?.Dispose();
        }
    }

    public class AudioDataEventArgs : EventArgs
    {
        public byte[] AudioData { get; set; } = Array.Empty<byte>();
        public int BytesRecorded { get; set; }
        public AudioSource Source { get; set; }
    }

    public enum AudioSource
    {
        Microphone,
        Speaker
    }
}