using System.IO;

namespace AspNetCoreExtras.Solace.Server
{
    public static class StreamExtensions
    {
        public static byte[] ReadAllBytes(this Stream stream)
        {
            if (stream is MemoryStream ms)
                return ms.ToArray();

            using var memoryStream = new MemoryStream();

            stream.CopyTo(memoryStream);

            return memoryStream.ToArray();
        }
    }
}
