namespace PS5Emulator.Models;

public class GameInfo
{
    public string? Title { get; set; }
    public string? TitleId { get; set; }
    public string? Version { get; set; }
    public string? Category { get; set; }
    public long EntryPoint { get; set; }
    public long ImageBase { get; set; }
    public bool Is64Bit { get; set; }
}
