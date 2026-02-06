using System;

namespace SolutionManagerDatabase.Schema;

public sealed class DbControllerAction
{
    public long Id { get; set; }

    public long ProjectId { get; set; }
    public DbProject Project { get; set; } = null!;

    public long ControllerArtifactId { get; set; }
    public DbArtifact ControllerArtifact { get; set; } = null!;

    public string ControllerName { get; set; } = null!;
    public string ActionName { get; set; } = null!;

    public string HttpMethod { get; set; } = "ANY";         // GET/POST/PUT/DELETE/PATCH/ANY
    public string? RouteTemplate { get; set; }              // best-effort combined
    public bool IsApi { get; set; }                         // from artifact subtype
    public bool IsAsync { get; set; }
    public string? ReturnType { get; set; }                 // "IActionResult", "Task<IActionResult>", etc.
    public string? Parameters { get; set; }                 // "Guid id, CancellationToken ct" etc.

    public string RelativeFilePath { get; set; } = null!;   // gitroot-relative
    public int SpanStart { get; set; }
    public int SpanLength { get; set; }

    public DateTime UpdatedOnUtc { get; set; } = DateTime.UtcNow;
}
