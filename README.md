# SoundChunks - Audio Chunking Application

A production-quality web application for manually splitting audio files into sequential MP3 chunks.

## Features

- Upload audio files (MP3, WAV, M4A, FLAC, OGG, AAC, WMA, and other FFmpeg-supported formats)
- Visual waveform display with draggable position indicator
- Audio playback controls (Play, Pause, Resume, Stop)
- Precise CUT operation to create audio chunks
- Automatic sequential chunk numbering (1.mp3, 2.mp3, 3.mp3, etc.)
- Empty TXT file creation for each chunk (for future transcription)
- Project state persistence (survives application restarts)
- Automatic project resumption when reopening the same file
- Concurrent cut operation prevention

## Architecture

The application uses a clean, monolithic architecture:

```
/ProjectRoot
│
├── Web/                          # ASP.NET Core application
│   ├── Controllers/               # API endpoints
│   ├── Services/                 # Business logic
│   ├── Models/                   # Data models
│   ├── ViewModels/               # View models
│   ├── Pages/                    # Razor Pages UI
│   ├── wwwroot/                  # Static files
│   └── Program.cs                # Application entry point
│
├── Python/                       # Python audio processing
│   ├── audio_cutter.py           # FFmpeg CLI wrapper
│   └── requirements.txt          # Python dependencies
│
├── AudioChunks/                  # Output directory (created at runtime)
│
├── Tests/                        # Automated tests
│
└── README.md
```

### Key Components

- **AudioController**: Handles file upload and cut operations
- **ProjectService**: Manages project state and filesystem operations
- **PythonAudioCutterService**: Invokes Python CLI process for audio cutting
- **ChunkManagerService**: Coordinates chunk creation with state management
- **Frontend JavaScript**: WaveSurfer.js for waveform visualization and player controls

## Requirements

### .NET
- .NET 10.0 SDK
- Download from: https://dotnet.microsoft.com/download

### Python
- Python 3.8 or higher
- Download from: https://www.python.org/downloads/

### FFmpeg
- FFmpeg must be installed and available in system PATH
- **Windows**: Download from https://ffmpeg.org/download.html#build-windows
  - Extract and add the `bin` folder to your system PATH
  - Verify with: `ffmpeg -version`
- **macOS**: `brew install ffmpeg`
- **Linux**: `sudo apt-get install ffmpeg`

## Installation

1. **Clone or navigate to the project directory**
   ```bash
   cd "D:\1 Dot Net Projects\0 SoundChunksProject"
   ```

2. **Install FFmpeg** (if not already installed)
   - Windows: Download, extract, and add to PATH
   - macOS: `brew install ffmpeg`
   - Linux: `sudo apt-get install ffmpeg`

3. **Verify Python installation**
   ```bash
   python --version
   ```

4. **Restore .NET dependencies**
   ```bash
   cd Web
   dotnet restore
   dotnet build
   ```

5. **Configure application settings** (optional)
   
   Edit `Web/appsettings.json` to customize paths:
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

## Running the Application

### Development Mode
```bash
cd Web
dotnet run
```

The application will start at: `https://localhost:5001` or `http://localhost:5000`

### Production Mode
```bash
cd Web
dotnet publish -c Release -o ./publish
cd publish
dotnet SoundChunksWeb.dll
```

## Usage

1. **Open the application** in your browser
2. **Select an audio file** using the file input
3. **Play the audio** using the Play button
4. **Navigate the audio** using the waveform or seek bar
5. **Press CUT** at the desired position to create a chunk
6. **Continue playing** and press CUT again to create the next chunk
7. **Chunks are saved** to `AudioChunks/[filename]/` with sequential numbering

### Chunk Naming Convention

```
AudioChunks/
└── My English Lesson/
    ├── 1.mp3        # First chunk (0s → first cut position)
    ├── 1.txt        # Empty text file
    ├── 2.mp3        # Second chunk (first cut → second cut)
    ├── 2.txt        # Empty text file
    ├── 3.mp3        # Third chunk (second cut → third cut)
    ├── 3.txt        # Empty text file
    └── project-state.json  # Project state file
```

### Project Resumption

When you upload the same audio file again:
- The application detects the existing project
- Loads the previous cut position
- Continues chunk numbering from where it left off
- No data is lost or overwritten

### Player Controls

- **Play**: Start audio playback from current position
- **Pause**: Pause at current position
- **Resume**: Continue playback from current position
- **Stop**: Stop playback and reset to beginning (does NOT change cut position)
- **CUT**: Create a chunk from previous cut position to current position

## .NET → Python CLI Communication

The .NET application invokes Python as a CLI process:

```
.NET Application
    ↓
PythonAudioCutterService
    ↓
ProcessStartInfo (python audio_cutter.py --input ... --output ... --start ... --end ...)
    ↓
Python Process
    ↓
FFmpeg (subprocess)
    ↓
MP3 Output
    ↓
.NET Verification (file exists, has content)
    ↓
State Update (only after success)
```

### CLI Contract

```bash
python audio_cutter.py --input "source.mp3" --output "chunk.mp3" --start 35.72 --end 82.10
```

Arguments:
- `--input`: Path to source audio file
- `--output`: Path to output MP3 file
- `--start`: Start time in seconds
- `--end`: End time in seconds
- `--ffmpeg`: (optional) Path to FFmpeg executable
- `--bitrate`: (optional) MP3 bitrate (default: 192k)

Exit codes:
- `0`: Success
- `1`: Failure (error details in stderr)

## State Persistence

Project state is persisted in `project-state.json`:

```json
{
  "projectName": "My English Lesson",
  "sourceFileName": "My English Lesson.m4a",
  "lastCutPosition": 120.0,
  "duration": 180.5,
  "lastModified": "2026-10-03T21:30:00Z",
  "createdAt": "2026-10-03T21:00:00Z"
}
```

Key fields:
- `lastCutPosition`: The position where the last successful cut was made
- `duration`: Total audio duration
- `projectName`: Normalized filename (without extension)

State is only updated **after** successful chunk creation. If Python/FFmpeg fails, the state is not advanced.

## Waveform Implementation

The waveform is implemented using **WaveSurfer.js**, a modern JavaScript library for audio visualization:

- Real-time waveform rendering from audio data
- Click-to-seek functionality
- Draggable/playback position indicator
- Synchronized with HTML5 audio element
- Responsive design

WaveSurfer.js is loaded from CDN: `https://cdn.jsdelivr.net/npm/wavesurfer.js@7/dist/wavesurfer.min.js`

## Testing

### Run Unit Tests
```bash
cd Tests
dotnet test
```

### Test Coverage

The test suite covers:
- New project initialization (position 0)
- First chunk numbering (starts at 1)
- Sequential chunk numbering
- Existing chunk detection
- Project state loading and persistence
- Path traversal prevention
- Invalid timestamp rejection
- State rollback on Python failure
- Empty TXT file creation
- Concurrent cut prevention

### Manual End-to-End Test

1. Upload an audio file (e.g., `test.m4a`)
2. Play to 10 seconds, press CUT
3. Verify: `AudioChunks/test/1.mp3` and `1.txt` exist
4. Play to 25.5 seconds, press CUT
5. Verify: `2.mp3` and `2.txt` exist
6. Play to 60.75 seconds, press CUT
7. Verify: `3.mp3` and `3.txt` exist
8. Play to 100 seconds, press CUT
9. Verify: `4.mp3` and `4.txt` exist
10. Close application
11. Reopen and upload the same file
12. Verify: Last cut position is 100, next chunk is 5
13. Play to 130 seconds, press CUT
14. Verify: `5.mp3` contains 100 → 130 seconds (NOT 0 → 130)

## Deployment

### Pre-Deployment Checklist

1. **Install FFmpeg** on target machine
2. **Install Python 3.8+** on target machine
3. **Configure paths** in `appsettings.json` if needed
4. **Set filesystem permissions** for AudioChunks directory (write access required)
5. **Test FFmpeg**: `ffmpeg -version`
6. **Test Python**: `python --version`

### Deploy as Self-Contained Application

```bash
cd Web
dotnet publish -c Release -r win-x64 --self-contained
```

This creates a standalone executable that includes the .NET runtime.

### Deploy as Framework-Dependent Application

```bash
cd Web
dotnet publish -c Release
```

Requires .NET 10.0 runtime to be installed on target machine.

### IIS Deployment

1. Publish the application
2. Create a new IIS site pointing to the publish folder
3. Configure application pool (no special requirements)
4. Ensure IIS AppPool has write access to AudioChunks directory

### Docker Deployment (Optional)

Create a `Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY Web/publish .
RUN apt-get update && apt-get install -y ffmpeg python3
EXPOSE 80
ENTRYPOINT ["dotnet", "SoundChunksWeb.dll"]
```

Build and run:
```bash
docker build -t soundchunks .
docker run -p 8080:80 -v $(pwd)/AudioChunks:/app/AudioChunks soundchunks
```

## Configuration

### Environment Variables

You can override configuration using environment variables:

```bash
set AudioProcessing__OutputRoot=AudioChunks
set AudioProcessing__PythonExecutable=python
set AudioProcessing__PythonScript=../Python/audio_cutter.py
set AudioProcessing__FfmpegExecutable=ffmpeg
```

### Filesystem Permissions

The application requires:
- **Read access**: To uploaded audio files
- **Write access**: To AudioChunks directory and project subdirectories
- **Execute access**: To Python and FFmpeg executables

## Known Limitations

1. **FFmpeg Dependency**: FFmpeg must be installed and in PATH
2. **Python Dependency**: Python must be installed and in PATH
3. **No Authentication**: Application has no built-in authentication (can be added if needed)
4. **Single User**: No multi-user support (state is filesystem-based)
5. **No Database**: Uses JSON files for state persistence (suitable for single-user scenarios)
6. **Browser Compatibility**: Requires modern browser with HTML5 audio support
7. **File Size**: Large audio files may take time to upload and process

## Future Extension Points

1. **Stronger File Identity**: Add file hash-based project identification (currently filename-based)
2. **Database Backend**: Replace JSON state with database for better scalability
3. **Authentication**: Add user authentication and multi-tenant support
4. **Batch Processing**: Add support for automatic chunk detection (silence-based)
5. **Transcription Integration**: Connect to speech-to-text services for TXT file content
6. **Cloud Storage**: Support S3/Azure Blob Storage for audio files
7. **Waveform Customization**: More waveform visualization options
8. **Export Formats**: Support additional output formats (WAV, FLAC, etc.)
9. **Project Management**: Project list, rename, delete functionality
10. **Audio Processing**: Normalize volume, trim silence, etc.

## Troubleshooting

### FFmpeg not found
```
Error: FFmpeg error: The term 'ffmpeg' is not recognized
```
**Solution**: Install FFmpeg and add to system PATH, or configure full path in appsettings.json

### Python not found
```
Error: Error executing Python: The term 'python' is not recognized
```
**Solution**: Install Python 3.8+ and add to system PATH, or configure full path in appsettings.json

### Cut operation fails
```
Error: Failed to create audio chunk: FFmpeg failed
```
**Solution**: Check FFmpeg installation, verify input file is valid, check logs for details

### Upload fails
```
Error: Error uploading file
```
**Solution**: Check file size limits, ensure uploads directory has write permissions

### Project not resuming
```
Issue: Re-uploaded file starts from 0 instead of last cut position
```
**Solution**: Ensure filename is exactly the same (case-sensitive), check project-state.json exists

## License

This project is provided as-is for educational and commercial use.

## Support

For issues or questions, please refer to the project documentation or contact the development team.
