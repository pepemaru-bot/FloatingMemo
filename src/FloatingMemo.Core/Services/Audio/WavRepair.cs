using System.Buffers.Binary;
using System.Text;

namespace FloatingMemo.Services.Audio;

/// <summary>
/// A WAV file whose writer never finished (the app crashed or the PC lost power) has sizes in
/// its header that are too small: they were last written at the latest flush, or never. The
/// audio bytes are on disk all the same. This brings the RIFF and data sizes up to what the
/// file really holds (whole frames only), so the audio can be read again.
/// </summary>
internal static class WavRepair
{
    /// <summary>True if the header was extended; false if it was already right or the file is not a WAV we know.</summary>
    public static bool ExtendToFileLength(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Span<byte> head = stackalloc byte[12];
        if (file.Read(head) < 12 || Encoding.ASCII.GetString(head[..4]) != "RIFF" || Encoding.ASCII.GetString(head[8..12]) != "WAVE")
        {
            return false;
        }

        int blockAlign = 0;
        Span<byte> chunk = stackalloc byte[8];
        while (file.Position + 8 <= file.Length)
        {
            long chunkStart = file.Position;
            file.ReadExactly(chunk);
            string id = Encoding.ASCII.GetString(chunk[..4]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (id == "fmt ")
            {
                Span<byte> fmt = stackalloc byte[16];
                file.ReadExactly(fmt);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..14]);
                file.Position = chunkStart + 8 + size + (size % 2);
                continue;
            }

            if (id != "data")
            {
                file.Position = chunkStart + 8 + size + (size % 2);
                continue;
            }

            long dataStart = chunkStart + 8;
            long actual = file.Length - dataStart;
            if (blockAlign > 0)
            {
                actual -= actual % blockAlign;
            }

            actual = Math.Min(actual, uint.MaxValue - 64);
            if (actual <= size)
            {
                return false;
            }

            Span<byte> value = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)actual);
            file.Position = chunkStart + 4;
            file.Write(value);
            BinaryPrimitives.WriteUInt32LittleEndian(value, (uint)Math.Min(uint.MaxValue, dataStart + actual - 8));
            file.Position = 4;
            file.Write(value);
            return true;
        }

        return false;
    }
}
