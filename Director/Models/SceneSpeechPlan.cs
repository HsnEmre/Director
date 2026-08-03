using Director.Enums;

namespace Director.Models;

public sealed class SceneSpeechPlan
{
    public int Id { get; set; }
    public int FilmProjectId { get; set; }
    public FilmProject FilmProject { get; set; } = null!;
    public int SceneId { get; set; }
    public FilmScene Scene { get; set; } = null!;
    public int TargetDurationSeconds { get; set; }
    public SpeechPlanStatus Status { get; set; } = SpeechPlanStatus.Draft;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public ICollection<SceneSpeechSegment> Segments { get; set; } = new List<SceneSpeechSegment>();
}
