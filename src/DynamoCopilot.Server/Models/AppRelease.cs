using System;

namespace DynamoCopilot.Server.Models;

public class AppRelease
{
    public Guid     Id            { get; set; } = Guid.NewGuid();
    public string   Version       { get; set; } = "";
    public string   MinVersion    { get; set; } = "1.0.0";
    public string   ReleaseNotes  { get; set; } = "";
    public string   DllsUrl       { get; set; } = "";
    public long     DllsSizeBytes { get; set; }
    public string?  DbVersion     { get; set; }
    public string?  DbUrl         { get; set; }
    public long?    DbSizeBytes   { get; set; }
    public DateTime PublishedAt   { get; set; } = DateTime.UtcNow;
}
