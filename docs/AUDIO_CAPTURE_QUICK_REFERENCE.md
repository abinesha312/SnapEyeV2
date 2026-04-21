# Audio Capture Quick Reference Guide

## 🎯 Quick Overview

SnapEye uses **two different audio capture methods** for stealth operation:

| Audio Source | Method | API | Stealth Level |
|--------------|--------|-----|---------------|
| **Speaker** (System Audio) | `WasapiLoopbackCapture` | Windows Core Audio | ⭐⭐⭐⭐⭐ Very High |
| **Microphone** (User Input) | `WaveInEvent` | Windows Multimedia | ⭐⭐⭐⭐ High |

## 🚀 Quick Start

```csharp
using SnapEye.Services;

// Create service
var audioService = new AudioCaptureService();

// Subscribe to events
audioService.MicrophoneDataAvailable += (s, e) => {
    Console.WriteLine($"Mic: {e.BytesRecorded} bytes");
    // Process e.AudioData (byte[])
};

audioService.SpeakerDataAvailable += (s, e) => {
    Console.WriteLine($"Speaker: {e.BytesRecorded} bytes");
    // Process e.AudioData (byte[])
};

audioService.ErrorOccurred += (s, msg) => {
    Console.WriteLine($"Error: {msg}");
};

// Start capturing
audioService.StartCapture();

// ... your app runs ...

// Stop and cleanup
audioService.StopCapture();
audioService.Dispose();
```

## 📊 Audio Format Specifications

### Input Formats

#### Microphone (WaveInEvent)
- **Sample Rate:** 16,000 Hz (16kHz)
- **Channels:** 1 (Mono)
- **Bit Depth:** 16-bit PCM
- **Buffer:** 50ms

#### Speaker (WasapiLoopbackCapture)
- **Sample Rate:** Device-dependent (typically 48kHz)
- **Channels:** Device-dependent (typically 2 - stereo)
- **Bit Depth:** Device-dependent (typically 32-bit float)
- **Buffer:** System-managed

### Output Format (Auto-Converted)

Both sources are automatically converted to:
- **Sample Rate:** 24,000 Hz (24kHz)
- **Channels:** 1 (Mono)
- **Bit Depth:** 16-bit PCM
- **Format:** Raw PCM, Little-endian

## 🔧 Key Classes and Methods

### AudioCaptureService

```csharp
public class AudioCaptureService : IDisposable
{
    // Events
    event EventHandler<AudioDataEventArgs>? MicrophoneDataAvailable;
    event EventHandler<AudioDataEventArgs>? SpeakerDataAvailable;
    event EventHandler<string>? ErrorOccurred;
    
    // Methods
    void StartCapture();      // Start capturing both sources
    void StopCapture();       // Stop capturing
    void Dispose();           // Cleanup resources
}
```

### AudioDataEventArgs

```csharp
public class AudioDataEventArgs : EventArgs
{
    byte[] AudioData { get; set; }        // PCM audio bytes (24kHz, mono, 16-bit)
    int BytesRecorded { get; set; }       // Number of bytes in AudioData
    AudioSource Source { get; set; }      // Microphone or Speaker
}
```

### AudioSource Enum

```csharp
public enum AudioSource
{
    Microphone,    // From user's microphone
    Speaker        // From system audio output
}
```

## 🛡️ Stealth Features

### Why These Methods Are Stealthy

#### WasapiLoopbackCapture (Speaker)
- ✅ Operates at Windows audio engine level
- ✅ No per-application hooks
- ✅ Captures all system audio transparently
- ✅ Doesn't appear in app-specific capture lists
- ✅ Standard Windows API - looks legitimate

#### WaveInEvent (Microphone)
- ✅ Standard Windows Multimedia API
- ✅ Used by millions of legitimate apps
- ✅ No special permissions needed
- ✅ Simple, non-suspicious API calls
- ✅ Lower detection profile than WASAPI capture

### What Makes It Work

1. **No Application Hooks**: No need to inject into browsers or apps
2. **System-Level Capture**: Works at OS audio pipeline level
3. **Standard APIs**: Uses APIs that are commonly used everywhere
4. **Low Profile**: Minimal CPU/memory footprint
5. **No Special Permissions**: Works with standard user privileges

## 💡 Common Patterns

### Pattern 1: Basic Transcription

```csharp
var audioService = new AudioCaptureService();

audioService.MicrophoneDataAvailable += (s, e) => {
    SendToTranscriptionAPI(e.AudioData, "user");
};

audioService.SpeakerDataAvailable += (s, e) => {
    SendToTranscriptionAPI(e.AudioData, "system");
};

audioService.StartCapture();
```

### Pattern 2: Separate Streams

```csharp
var micStream = new MemoryStream();
var speakerStream = new MemoryStream();

audioService.MicrophoneDataAvailable += (s, e) => {
    micStream.Write(e.AudioData, 0, e.BytesRecorded);
};

audioService.SpeakerDataAvailable += (s, e) => {
    speakerStream.Write(e.AudioData, 0, e.BytesRecorded);
};
```

### Pattern 3: Conditional Recording

```csharp
bool recordMic = true;
bool recordSpeaker = true;

audioService.MicrophoneDataAvailable += (s, e) => {
    if (recordMic) ProcessAudio(e.AudioData);
};

audioService.SpeakerDataAvailable += (s, e) => {
    if (recordSpeaker) ProcessAudio(e.AudioData);
};
```

### Pattern 4: Quality Monitoring

```csharp
audioService.MicrophoneDataAvailable += (s, e) => {
    var volume = CalculateRMS(e.AudioData);
    if (volume < threshold) {
        Console.WriteLine("Microphone volume too low");
    }
};
```

## ⚠️ Important Considerations

### Error Handling

```csharp
try {
    audioService.StartCapture();
} catch (Exception ex) {
    // Handle errors:
    // - No audio device available
    // - Device already in use (exclusive mode)
    // - Driver issues
    // - Permission denied
}
```

### Resource Management

```csharp
// Always dispose - use 'using' statement
using (var audioService = new AudioCaptureService()) {
    audioService.StartCapture();
    // ... do work ...
} // Automatic cleanup

// Or manual
try {
    audioService.StartCapture();
    // ... do work ...
} finally {
    audioService.StopCapture();
    audioService.Dispose();
}
```

### Thread Safety

```csharp
// Event callbacks run on NAudio's thread
// Marshal to UI thread if needed
audioService.MicrophoneDataAvailable += (s, e) => {
    Dispatcher.Invoke(() => {
        UpdateUI(e.AudioData);
    });
};
```

## 🔍 Debugging Tips

### Check Audio Format

```csharp
audioService.MicrophoneDataAvailable += (s, e) => {
    Console.WriteLine($"Mic: {e.BytesRecorded} bytes, " +
                     $"~{e.BytesRecorded / 48.0:F2}ms of audio");
};

// At 24kHz, 16-bit, mono: 48,000 bytes/sec
// So 48 bytes = 1ms of audio
```

### Monitor Data Flow

```csharp
int micPackets = 0;
int speakerPackets = 0;

audioService.MicrophoneDataAvailable += (s, e) => {
    micPackets++;
    Console.WriteLine($"Mic packets: {micPackets}");
};

audioService.SpeakerDataAvailable += (s, e) => {
    speakerPackets++;
    Console.WriteLine($"Speaker packets: {speakerPackets}");
};
```

### Verify Capture is Active

```csharp
audioService.StartCapture();

// Wait a moment
await Task.Delay(1000);

// Check if events fired
if (micPackets == 0) {
    Console.WriteLine("⚠️ No microphone data received");
}

if (speakerPackets == 0) {
    Console.WriteLine("⚠️ No speaker data received (might be silent)");
}
```

## 🎛️ Configuration Options

### Change Microphone Device

Currently hardcoded to device 0. To change:

```csharp
// In AudioCaptureService.cs, line 47
DeviceNumber = 1, // Use different device
```

To list available devices:

```csharp
for (int i = 0; i < WaveInEvent.DeviceCount; i++) {
    var caps = WaveInEvent.GetCapabilities(i);
    Console.WriteLine($"Device {i}: {caps.ProductName}");
}
```

### Adjust Buffer Size

```csharp
// In AudioCaptureService.cs, line 49
BufferMilliseconds = 100, // Increase for less CPU, more latency
                          // Decrease for less latency, more CPU
```

### Modify Resampling Quality

```csharp
// In ConvertToOpenAIFormat(), line 152
ResamplerQuality = 60 // Max quality (0-60)
                      // Lower = faster, lower quality
                      // Higher = slower, better quality
```

## 📈 Performance Metrics

### Typical Resource Usage

| Metric | Value |
|--------|-------|
| CPU Usage | 1-3% (single core) |
| Memory | ~5-10 MB |
| Latency | 50-100ms total |
| Throughput | ~96 KB/s (both streams) |

### Optimization Tips

1. **Reduce resampling quality** if CPU is constrained
2. **Increase buffer size** to reduce callback frequency
3. **Process audio in batches** rather than per-packet
4. **Use async processing** to avoid blocking callbacks

## 🧪 Testing Checklist

- [ ] Microphone captures user voice
- [ ] Speaker captures system audio (play music/video)
- [ ] Both streams convert to 24kHz correctly
- [ ] No audio dropouts during long sessions
- [ ] Handles device disconnection gracefully
- [ ] Works with different audio devices (USB, Bluetooth)
- [ ] Multiple start/stop cycles work correctly
- [ ] Proper cleanup on disposal

## 📚 Related Documentation

- [AUDIO_CAPTURE_IMPLEMENTATION.md](./AUDIO_CAPTURE_IMPLEMENTATION.md) - Full technical details
- [AUDIO_CAPTURE_MIGRATION_GUIDE.md](./AUDIO_CAPTURE_MIGRATION_GUIDE.md) - Migration from old implementation
- [NAudio Documentation](https://github.com/naudio/NAudio) - NAudio library docs

## 🆘 Common Issues

### No Microphone Audio
**Problem:** `MicrophoneDataAvailable` event never fires  
**Solution:** 
- Check default microphone in Windows Sound settings
- Verify microphone permissions for the app
- Ensure microphone is not in exclusive mode

### No Speaker Audio
**Problem:** `SpeakerDataAvailable` event never fires  
**Solution:**
- Check if system audio is actually playing
- Verify default playback device in Windows
- Some systems require audio to be playing to capture

### Distorted Audio
**Problem:** Audio sounds garbled or distorted  
**Solution:**
- Check source audio format matches expected format
- Verify resampling is working correctly
- Reduce resampler quality if CPU is overloaded

### High Latency
**Problem:** Audio processing is delayed  
**Solution:**
- Reduce `BufferMilliseconds` (line 49)
- Process audio asynchronously
- Optimize event handlers

## 💻 Code Location

**Primary File:** `Application/services/AudioCaptureService.cs`

**Key Lines:**
- Lines 18-19: Field declarations
- Lines 37-72: `StartCapture()` method
- Lines 91-127: Data event handlers
- Lines 132-171: `ConvertToOpenAIFormat()` method

**Usage Example:** `Application/OverlayWindow.xaml.cs`
- Lines 18, 32: Service initialization
- Lines 84-86: Event subscription
- Line 213: Start capture
- Line 229: Stop capture

---

**Quick Reference Version:** 1.0  
**Last Updated:** November 18, 2025  
**Status:** ✅ Current Implementation

