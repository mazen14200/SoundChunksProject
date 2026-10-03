# SoundChunks Project - Agent Information

## Project Overview

This is a production-quality audio chunking web application built with ASP.NET Core and Python/FFmpeg.

## Build Commands

```bash
# Build the web application
cd Web
dotnet build

# Run the web application
dotnet run

# Run tests
cd Tests
dotnet test

# Publish for production
cd Web
dotnet publish -c Release
```

## Test Commands

```bash
# Run all unit tests
cd Tests
dotnet test

# Run specific test
cd Tests
dotnet test --filter "FullyQualifiedName~ProjectServiceTests"
```

## Key Architecture Decisions

1. **Monolithic Architecture**: Single ASP.NET Core application with integrated frontend
2. **Python CLI Integration**: .NET invokes Python as a process (not HTTP API)
3. **FFmpeg for Audio Processing**: Uses FFmpeg via Python subprocess for broad format support
4. **JSON State Persistence**: Project state stored in project-state.json files
5. **WaveSurfer.js**: Used for waveform visualization via CDN
6. **Sequential Chunk Numbering**: Chunks numbered 1, 2, 3, etc.
7. **Empty TXT Files**: Created for each chunk (placeholder for future transcription)

## Important Implementation Details

### State Persistence Logic
- State is ONLY updated after successful MP3 creation
- If Python/FFmpeg fails, state is NOT advanced
- This prevents data loss and allows retry
- Critical for correctness of chunk boundaries

### Cut Position Logic
- Previous cut position starts at 0
- Each CUT creates: previousCutPosition → currentPosition
- After successful cut: previousCutPosition = currentPosition
- STOP does NOT modify previous cut position
- Only successful CUT advances the state

### Path Traversal Prevention
- Project names are sanitized by replacing invalid filename characters with underscores
- ".." sequences are replaced with "_" to prevent path traversal
- All paths are combined within the configured OutputRoot directory

### Concurrency Control
- SemaphoreSlim prevents concurrent CUT operations for the same project
- CUT button is disabled during operation
- Prevents duplicate chunks from rapid clicks

## Dependencies

### .NET
- .NET 10.0 SDK
- ASP.NET Core
- No external NuGet packages beyond default templates

### Python
- Python 3.8+
- No Python packages required (uses FFmpeg via subprocess)

### FFmpeg
- Must be installed and in system PATH
- Required for audio processing
- Download: https://ffmpeg.org/download.html

## Configuration

Configuration is in `Web/appsettings.json`:

```json
{
  "AudioProcessing": {
    "OutputRoot": "AudioChunks",
    "PythonExecutable": "python",
    "PythonScript": "../Python/audio_cutter.py",
    "FfmpegExecutable": "ffmpeg"
  }
}
```

Can be overridden with environment variables:
- `AudioProcessing__OutputRoot`
- `AudioProcessing__PythonExecutable`
- `AudioProcessing__PythonScript`
- `AudioProcessing__FfmpegExecutable`

## File Structure

```
/ProjectRoot
├── Web/                    # ASP.NET Core application
│   ├── Controllers/        # API endpoints
│   ├── Services/           # Business logic
│   ├── Models/             # Data models
│   ├── ViewModels/         # View models
│   ├── Pages/              # Razor Pages
│   └── Program.cs          # Entry point
├── Python/                 # Python scripts
│   └── audio_cutter.py    # FFmpeg wrapper
├── AudioChunks/            # Output directory (created at runtime)
├── Tests/                  # Unit tests
└── README.md
```

## Critical Test Scenarios

The most important acceptance test is the sequential chunk cut scenario:

1. Upload test.m4a
2. CUT @ 10.00 → 1.mp3 (0 → 10)
3. CUT @ 25.50 → 2.mp3 (10 → 25.5)
4. CUT @ 60.75 → 3.mp3 (25.5 → 60.75)
5. CUT @ 100.00 → 4.mp3 (60.75 → 100)
6. Close application
7. Reopen and upload same file
8. CUT @ 130.00 → 5.mp3 (100 → 130) [NOT 0 → 130]

This validates:
- Correct chunk boundaries
- State persistence
- Project resumption
- Sequential numbering

## Known Issues

- FFmpeg must be installed for audio cutting to work
- Python must be installed for the CLI integration
- No built-in authentication
- No multi-user support
- File-based state persistence (not database)

## Future Work

1. Add file hash-based project identity (currently filename-based)
2. Add database backend for state persistence
3. Add authentication/authorization
4. Add automatic chunk detection (silence-based)
5. Add transcription integration
6. Add cloud storage support
7. Add project management UI (list, rename, delete)
