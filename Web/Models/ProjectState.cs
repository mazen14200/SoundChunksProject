namespace SoundChunksWeb.Models;

public class ProjectState
{
    public string ProjectName { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public string SourceStoredName { get; set; } = string.Empty;
    public double LastCutPosition { get; set; }
    public double Duration { get; set; }
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
