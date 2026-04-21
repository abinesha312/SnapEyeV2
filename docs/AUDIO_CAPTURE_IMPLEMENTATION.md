# Audio Capture Implementation - Stealth Audio Capture System

## Overview

This document describes the audio capture implementation used in SnapEye, which employs stealth techniques to capture system audio (speakers) and microphone input using low-level Windows APIs that are less detectable by monitoring software.

## Audio Capture Methods

### 1. System Audio Capture (WASAPI Loopback)

**Implementation:** `WasapiLoopbackCapture` from NAudio

**File:** `Application/services/AudioCaptureService.cs` (Lines 59-63)

```csharp
speakerCapture = new WasapiLoopbackCapture();
speakerCapture.DataAvailable += OnSpeakerDataAvailable;
speakerCapture.RecordingStopped += OnRecordingStopped;
speakerCapture.StartRecording();
LogInfo($"System audio format: {speakerCapture.WaveFormat}");
```

**Key Features:**
- Captures system audio at the Windows audio engine level
- No per-application hooks required
- Captures all audio output (browser, applications, system sounds)
- Low-level API that doesn't show up in application-specific capture lists
- Operates transparently without special permissions

### 2. Microphone Capture (WaveInEvent)

**Implementation:** `WaveInEvent` from NAudio

**File:** `Application/services/AudioCaptureService.cs` (Lines 45-55)

```csharp
microphoneCapture = new WaveInEvent
{
    DeviceNumber = 0, // Default microphone
    WaveFormat = new WaveFormat(16000, 1), // 16kHz, mono
    BufferMilliseconds = 50 // Low latency
};

microphoneCapture.DataAvailable += OnMicrophoneDataAvailable;
microphoneCapture.RecordingStopped += OnRecordingStopped;
microphoneCapture.StartRecording();
LogInfo($"Microphone format: {microphoneCapture.WaveFormat}");
```

**Key Features:**
- Uses standard Windows API for microphone input
- Configured for 16kHz mono capture (resampled to 24kHz for API)
- Low latency with 50ms buffer
- Less detectable than WASAPI capture methods
- Standard Windows multimedia API

## Technical Architecture

### Audio Format Conversion

Both audio sources are automatically converted to the required format for downstream processing:

**Target Format:**
- Sample Rate: 24,000 Hz (24kHz)
- Bit Depth: 16-bit PCM
- Channels: Mono (1 channel)

**Conversion Process:**
```csharp
private byte[] ConvertToOpenAIFormat(byte[] audioData, WaveFormat sourceFormat)
{
    using var sourceStream = new RawSourceWaveStream(audioData, 0, audioData.Length, sourceFormat);
    var targetFormat = new WaveFormat(24000, 16, 1);
    
    // High-quality resampling
    using var resampler = new MediaFoundationResampler(sourceStream, targetFormat)
    {
        ResamplerQuality = 60
    };
    
    // Convert and return
    // ... (see AudioCaptureService.cs for full implementation)
}
```

### Event-Driven Architecture

The service uses events to notify consumers of available audio data:

```csharp
public event EventHandler<AudioDataEventArgs>? MicrophoneDataAvailable;
public event EventHandler<AudioDataEventArgs>? SpeakerDataAvailable;
public event EventHandler<string>? ErrorOccurred;
```

**Event Payload:**
```csharp
public class AudioDataEventArgs : EventArgs
{
    public byte[] AudioData { get; set; }      // PCM audio data
    public int BytesRecorded { get; set; }     // Number of bytes
    public AudioSource Source { get; set; }    // Microphone or Speaker
}

public enum AudioSource
{
    Microphone,
    Speaker
}
```

## Stealth Characteristics

### Why This Approach is Less Detectable

1. **WASAPI Loopback Benefits:**
   - Operates at the Windows audio engine level
   - Doesn't appear in per-application capture lists
   - No special hooks or injection required
   - Uses standard Windows Core Audio API
   - Transparent to monitoring software

2. **WaveInEvent Benefits:**
   - Standard Windows Multimedia API
   - Used by countless legitimate applications
   - No elevated permissions required
   - Doesn't trigger security alerts
   - Low-level but widely used interface

3. **No Application-Specific Hooks:**
   - Neither method requires hooking into specific applications
   - No browser extensions or process injection
   - Clean API-level capture

4. **Minimal Footprint:**
   - Standard Windows API calls
   - No special drivers or kernel components
   - Low CPU and memory overhead

## Integration with Detection Avoidance

This audio capture implementation works in conjunction with other stealth features:

### Window Display Affinity
- Windows are excluded from screen capture using `WDA_EXCLUDEFROMCAPTURE`
- See `SecureOverlay.cs` for implementation

### Process Hiding
- Lower process priority
- Random window titles
- Hidden from taskbar/Alt+Tab
- Layered window attributes

### Proctoring Software Detection
- Monitors for processes like "respondus", "lockdown", "proctor"
- Applies additional stealth measures when detected
- Can reduce opacity or hide completely

## Usage Example

```csharp
// Initialize the service
var audioCaptureService = new AudioCaptureService();

// Subscribe to events
audioCaptureService.MicrophoneDataAvailable += (sender, e) => {
    // Process microphone audio
    ProcessAudio(e.AudioData, e.Source);
};

audioCaptureService.SpeakerDataAvailable += (sender, e) => {
    // Process speaker audio
    ProcessAudio(e.AudioData, e.Source);
};

audioCaptureService.ErrorOccurred += (sender, message) => {
    Console.WriteLine($"Error: {message}");
};

// Start capturing
audioCaptureService.StartCapture();

// ... application runs ...

// Stop capturing
audioCaptureService.StopCapture();

// Clean up
audioCaptureService.Dispose();
```

## Dependencies

- **NAudio**: Core audio library for .NET
  - `NAudio.Wave`: Wave file and audio streaming
  - `NAudio.CoreAudioApi`: Windows Core Audio API access
  - `NAudio.Wave.SampleProviders`: Audio processing utilities

## Performance Characteristics

- **Latency:** ~50ms buffer for microphone, minimal for loopback
- **CPU Usage:** Low (primarily during resampling)
- **Memory:** Minimal buffering, streaming approach
- **Quality:** High-quality 60-level resampling

## Error Handling

The service includes comprehensive error handling:

1. **Startup Errors:** Graceful degradation if devices unavailable
2. **Runtime Errors:** Continues operation, notifies via events
3. **Conversion Errors:** Falls back to original audio if resampling fails
4. **Recording Stopped:** Handles unexpected stops with error reporting

## Security Considerations

### Legitimate Use Cases
This implementation is designed for:
- Accessibility tools
- Personal productivity applications
- Legal screen recording and documentation
- Educational tools with user consent

### Ethical Guidelines
- Always obtain user consent before recording
- Comply with local laws regarding audio recording
- Respect privacy and confidentiality
- Use only for legitimate purposes

### Detection Risks
While this implementation uses stealth techniques:
- It's not invisible to all monitoring systems
- Advanced monitoring software may still detect audio capture
- System-level monitoring can track API calls
- Users should be aware of the limitations

## Future Enhancements

Potential improvements to consider:

1. **Multi-Device Support:** Allow selection of specific audio devices
2. **Dynamic Quality Adjustment:** Adapt quality based on system load
3. **Compression:** Real-time audio compression before transmission
4. **Silence Detection:** Skip silence periods to reduce data
5. **Noise Cancellation:** Filter background noise from microphone

## Troubleshooting

### No Audio Captured
- Check default audio devices in Windows settings
- Ensure microphone permissions are granted
- Verify audio is playing through default output device

### Poor Audio Quality
- Check source audio format
- Verify resampling is working correctly
- Monitor CPU usage during capture

### Recording Stops Unexpectedly
- Check for device disconnections
- Monitor for driver updates
- Review system audio settings changes

## References

- [NAudio Documentation](https://github.com/naudio/NAudio)
- [Windows Core Audio API](https://docs.microsoft.com/en-us/windows/win32/coreaudio/core-audio-apis)
- [WASAPI Overview](https://docs.microsoft.com/en-us/windows/win32/coreaudio/wasapi)
- [Windows Multimedia API](https://docs.microsoft.com/en-us/windows/win32/multimedia/windows-multimedia-start-page)

---

**Last Updated:** November 18, 2025  
**Version:** 1.0  
**Author:** SnapEye Development Team

