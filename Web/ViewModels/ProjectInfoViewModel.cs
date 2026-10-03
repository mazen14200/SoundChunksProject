namespace SoundChunksWeb.ViewModels;

public class ProjectInfoViewModel
{
    public string ProjectName { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public double LastCutPosition { get; set; }
    public double Duration { get; set; }
    public int NextChunkNumber { get; set; }
}
