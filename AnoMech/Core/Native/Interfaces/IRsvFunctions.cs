namespace AnoMech.Core.Native.Interfaces;

// The client's RSV table: resolves the placeholder action/status names newer content ships with,
// which the server only sends inside the duty.
public interface IRsvFunctions
{
    // record = one raw RSV packet payload: [u32 valueLength][0x30-byte key][value].
    void Add(byte[] record);

    // For the log: what the table holds, how many of the strings added still resolve, and how the
    // adds since the last report went.
    string Describe();
}
