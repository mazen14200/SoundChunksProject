namespace SoundChunksWeb.Models;

public class AudioProcessingSettings
{
    public const string SectionName = "AudioProcessing";

    public string OutputRoot { get; set; } = "AudioChunks";
    public string PythonExecutable { get; set; } = "python";
    public string PythonScript { get; set; } = "../Python/audio_cutter.py";
    public string FfmpegExecutable { get; set; } = "ffmpeg";
}
