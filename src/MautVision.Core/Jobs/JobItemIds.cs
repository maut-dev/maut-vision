using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MautVision.Core.Jobs;

/// <summary>
/// IDs of the items a job creates, derived from the job ID and the item index (RFC 9562 version 8 layout). A run repeated
/// after a crash creates the same IDs again, so it finds its own rows and files instead of producing copies.
/// </summary>
internal static class JobItemIds
{
    internal static Guid Derive(Guid jobId, int index)
    {
        Span<byte> seed = stackalloc byte[20];
        jobId.TryWriteBytes(seed);
        BinaryPrimitives.WriteInt32LittleEndian(seed[16..], index);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(seed, hash);
        hash[7] = (byte)((hash[7] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16]);
    }
}
