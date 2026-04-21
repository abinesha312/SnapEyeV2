# SnapEye Audio Capture Documentation

## 📖 Documentation Index

This directory contains comprehensive documentation for SnapEye's audio capture implementation, which uses stealth techniques to capture system audio and microphone input.

### 📚 Available Documents

#### 1. [Quick Reference Guide](AUDIO_CAPTURE_QUICK_REFERENCE.md) ⚡
**Start here for:** Quick implementation, common patterns, troubleshooting

**Contents:**
- 🚀 Quick start guide with code examples
- 📊 Audio format specifications
- 💡 Common patterns and use cases
- 🔍 Debugging tips and tricks
- 🆘 Common issues and solutions

**Best for:** Developers who want to get started quickly or need a quick reference.

---

#### 2. [Implementation Guide](AUDIO_CAPTURE_IMPLEMENTATION.md) 🔧
**Start here for:** In-depth technical understanding, architecture details

**Contents:**
- 📐 System architecture overview
- 🎤 Microphone capture (WaveInEvent) implementation
- 🔊 Speaker capture (WasapiLoopbackCapture) implementation
- 🔄 Audio format conversion pipeline
- 🛡️ Stealth characteristics and detection avoidance
- ⚙️ Event-driven architecture
- 📈 Performance characteristics

**Best for:** Developers who need to understand how the system works internally.

---

#### 3. [Migration Guide](AUDIO_CAPTURE_MIGRATION_GUIDE.md) 🔄
**Start here for:** Understanding the changes, before/after comparison

**Contents:**
- 📊 Before/after implementation comparison
- 🔑 Key differences between WasapiCapture and WaveInEvent
- 🎯 Migration impact (spoiler: no breaking changes!)
- 📈 Performance comparison
- 🧪 Testing results and stealth improvements
- 🔙 Rollback instructions (if needed)

**Best for:** Developers maintaining existing code or curious about why changes were made.

---

#### 4. [Changelog](AUDIO_CAPTURE_CHANGELOG.md) 📝
**Start here for:** Version history, what's new, release notes

**Contents:**
- 🔄 Version 2.0 changes summary
- ✨ New features and enhancements
- 🐛 Bug fixes
- 📊 Testing results and metrics
- 🔮 Future enhancements roadmap
- 🤝 Contributors and references

**Best for:** Project managers, stakeholders, or developers tracking changes over time.

---

## 🎯 Quick Navigation

### By Role

| Role | Start With | Then Read |
|------|-----------|-----------|
| **New Developer** | Quick Reference | Implementation Guide |
| **Integrating Service** | Quick Reference | Migration Guide |
| **Understanding Architecture** | Implementation Guide | Migration Guide |
| **Maintaining Code** | Migration Guide | Quick Reference |
| **Project Manager** | Changelog | Implementation Guide |

### By Task

| Task | Document | Section |
|------|----------|---------|
| **Implement audio capture** | Quick Reference | Quick Start |
| **Understand stealth techniques** | Implementation Guide | Stealth Characteristics |
| **Debug audio issues** | Quick Reference | Debugging Tips |
| **Configure capture settings** | Quick Reference | Configuration Options |
| **Compare old vs new** | Migration Guide | Changes Summary |
| **Check performance** | Changelog | Performance Metrics |
| **Review testing results** | Changelog | Testing Results |

### By Question

| Question | Document | Section |
|----------|----------|---------|
| How do I start capturing audio? | Quick Reference | Quick Start |
| What audio formats are used? | Quick Reference | Audio Format Specifications |
| Why WaveInEvent instead of WasapiCapture? | Migration Guide | Key Differences |
| How does stealth work? | Implementation Guide | Stealth Characteristics |
| What changed in v2.0? | Changelog | Changes Summary |
| How do I handle errors? | Quick Reference | Error Handling |
| What are the performance metrics? | Changelog | Performance Metrics |
| How do I debug audio issues? | Quick Reference | Debugging Tips |

---

## 🚀 Quick Start

If you just want to get started immediately:

```csharp
using SnapEye.Services;

// Create and start
var audioService = new AudioCaptureService();

audioService.MicrophoneDataAvailable += (s, e) => {
    // Handle microphone audio (24kHz, mono, 16-bit PCM)
    ProcessAudio(e.AudioData, e.BytesRecorded);
};

audioService.SpeakerDataAvailable += (s, e) => {
    // Handle speaker audio (24kHz, mono, 16-bit PCM)
    ProcessAudio(e.AudioData, e.BytesRecorded);
};

audioService.StartCapture();

// ... app runs ...

audioService.StopCapture();
audioService.Dispose();
```

For more details, see [Quick Reference Guide](AUDIO_CAPTURE_QUICK_REFERENCE.md).

---

## 🎓 Learning Path

### Beginner Path
1. **Read:** Quick Reference Guide → Quick Start section
2. **Try:** Implement basic capture example
3. **Explore:** Quick Reference → Common Patterns
4. **Debug:** Quick Reference → Debugging Tips

### Intermediate Path
1. **Read:** Implementation Guide → Overview
2. **Understand:** Implementation Guide → Audio Capture Methods
3. **Review:** Migration Guide → Key Differences
4. **Optimize:** Implementation Guide → Performance Characteristics

### Advanced Path
1. **Study:** Implementation Guide → Complete document
2. **Compare:** Migration Guide → Complete document
3. **Analyze:** Changelog → Testing Results
4. **Contribute:** Implement future enhancements from roadmap

---

## 🔑 Key Concepts

### Audio Capture Methods

| Method | Used For | API | Stealth Level |
|--------|----------|-----|---------------|
| **WasapiLoopbackCapture** | System Audio (Speakers) | Windows Core Audio | ⭐⭐⭐⭐⭐ |
| **WaveInEvent** | Microphone Input | Windows Multimedia | ⭐⭐⭐⭐ |

### Audio Flow

```
Microphone (16kHz, mono, 16-bit)
    ↓ WaveInEvent Capture
    ↓ Resample to 24kHz
    ↓ Event: MicrophoneDataAvailable
    → Your Application

Speaker (48kHz, stereo, 32-bit float)
    ↓ WasapiLoopbackCapture
    ↓ Convert to mono + Resample to 24kHz
    ↓ Event: SpeakerDataAvailable
    → Your Application
```

### Output Format (Both Sources)
- **Sample Rate:** 24,000 Hz (24kHz)
- **Channels:** 1 (Mono)
- **Bit Depth:** 16-bit PCM
- **Byte Rate:** 48,000 bytes/second

---

## 🛡️ Stealth Features

Both capture methods employ stealth techniques:

1. **Low-Level APIs**: Standard Windows APIs used by millions of applications
2. **No Hooks**: No application injection or special hooks required
3. **Transparent**: Operates at OS audio pipeline level
4. **Legitimate**: Uses same APIs as Skype, Zoom, Discord, etc.
5. **No Special Permissions**: Works with standard user privileges

For detailed explanation, see [Implementation Guide → Stealth Characteristics](AUDIO_CAPTURE_IMPLEMENTATION.md#stealth-characteristics).

---

## 🔧 Configuration

### Basic Configuration
All configuration is in `AudioCaptureService.cs`:

| Setting | Line | Default | Description |
|---------|------|---------|-------------|
| Microphone Device | 47 | `0` | Device number (0 = default) |
| Microphone Sample Rate | 48 | `16000` | 16kHz input |
| Microphone Channels | 48 | `1` | Mono |
| Buffer Size | 49 | `50` | 50ms buffer |
| Target Sample Rate | 23 | `24000` | 24kHz output |
| Resampler Quality | 152 | `60` | Max quality (0-60) |

### Advanced Configuration

See [Quick Reference Guide → Configuration Options](AUDIO_CAPTURE_QUICK_REFERENCE.md#-configuration-options).

---

## 📊 Performance

### Typical Metrics
- **CPU Usage:** 1-3% (single core)
- **Memory:** 5-10 MB
- **Latency:** 50-100ms total
- **Throughput:** ~96 KB/s (both streams combined)

For detailed metrics, see [Changelog → Performance Metrics](AUDIO_CAPTURE_CHANGELOG.md#-performance-metrics).

---

## 🆘 Troubleshooting

### Common Issues

| Issue | Quick Fix | Details |
|-------|-----------|---------|
| No microphone audio | Check Windows default device | [Quick Ref](AUDIO_CAPTURE_QUICK_REFERENCE.md#no-microphone-audio) |
| No speaker audio | Ensure audio is playing | [Quick Ref](AUDIO_CAPTURE_QUICK_REFERENCE.md#no-speaker-audio) |
| Distorted audio | Check resampler quality | [Quick Ref](AUDIO_CAPTURE_QUICK_REFERENCE.md#distorted-audio) |
| High latency | Reduce buffer size | [Quick Ref](AUDIO_CAPTURE_QUICK_REFERENCE.md#high-latency) |

For complete troubleshooting guide, see [Quick Reference → Common Issues](AUDIO_CAPTURE_QUICK_REFERENCE.md#-common-issues).

---

## 🔗 External Resources

### NAudio Library
- [GitHub Repository](https://github.com/naudio/NAudio)
- [Documentation](https://github.com/naudio/NAudio/tree/master/Docs)
- [Samples](https://github.com/naudio/NAudio/tree/master/NAudioDemo)

### Windows Audio APIs
- [Core Audio API](https://docs.microsoft.com/en-us/windows/win32/coreaudio/core-audio-apis)
- [WASAPI](https://docs.microsoft.com/en-us/windows/win32/coreaudio/wasapi)
- [Windows Multimedia API](https://docs.microsoft.com/en-us/windows/win32/multimedia/windows-multimedia-start-page)

---

## 📞 Support

### Getting Help

1. **Check Documentation**: Start with Quick Reference for common issues
2. **Review Examples**: See Quick Reference → Common Patterns
3. **Debug**: Use Quick Reference → Debugging Tips
4. **Search Issues**: Check if others had similar problems

### Reporting Issues

When reporting issues, include:
- Windows version (10/11)
- Audio device information
- Error messages or logs
- Steps to reproduce
- Expected vs actual behavior

---

## 🔒 Security & Ethics

### Important Reminders

⚠️ **Always:**
- Obtain user consent before recording
- Comply with local laws
- Handle audio securely
- Be transparent about capabilities
- Use for legitimate purposes only

✅ **Approved Use Cases:**
- Accessibility tools
- Personal productivity
- Educational applications
- Legal documentation

❌ **Prohibited:**
- Unauthorized surveillance
- Academic dishonesty
- Privacy violations
- Malicious recording

For full discussion, see [Implementation Guide → Security Considerations](AUDIO_CAPTURE_IMPLEMENTATION.md#security-considerations).

---

## 📈 Version Information

- **Current Version:** 2.0
- **Release Date:** November 18, 2025
- **Status:** ✅ Production Ready
- **Compatibility:** Windows 10/11

For version history, see [Changelog](AUDIO_CAPTURE_CHANGELOG.md).

---

## 🤝 Contributing

Future enhancements are planned. See [Changelog → Future Enhancements](AUDIO_CAPTURE_CHANGELOG.md#-future-enhancements) for the roadmap.

---

## 📄 License

[Include your license information here]

---

**Last Updated:** November 18, 2025  
**Documentation Version:** 1.0  
**Maintained By:** SnapEye Development Team

