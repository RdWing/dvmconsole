namespace DvmConsole.Core.Settings;

// Persisted patch member identity. Runtime patch routing converts this safe
// settings DTO into a validated `PatchMemberAddress`.
public sealed class PatchMemberSetting
{
    public string SystemName { get; set; } = string.Empty;
    public uint DestinationId { get; set; }
}
