using System.Security.Cryptography;
using System.Text;
using IsoForge.Media;
using Xunit;

namespace IsoForge.Tests;

public sealed class FatImageBuilderTests
{
    [Theory]
    [InlineData(1024)]
    [InlineData(200_000)]
    [InlineData(1_800_000)]
    [InlineData(6_000_000)]
    public void CreatedImage_ExposesBootloaderAtTheExpectedPath(int payloadSize)
    {
        byte[] payload = RandomNumberGenerator.GetBytes(payloadSize);
        byte[] image = FatImageBuilder.CreateEfiBootImage(payload);

        FatReader reader = new(image);

        Assert.Equal(512, reader.BytesPerSector);
        Assert.Equal(0x55, image[510]);
        Assert.Equal(0xAA, image[511]);
        Assert.Equal("FAT16", reader.FileSystemType);

        byte[] actual = reader.ReadFile("EFI/BOOT/BOOTX64.EFI");
        Assert.Equal(payload, actual);
    }

    [Fact]
    public void ImageSize_IsAMultipleOfTheSectorSize()
    {
        byte[] image = FatImageBuilder.CreateEfiBootImage(RandomNumberGenerator.GetBytes(4096));
        Assert.Equal(0, image.Length % 512);
    }

    [Fact]
    public void SeveralFiles_AreAllReadable()
    {
        byte[] x64 = RandomNumberGenerator.GetBytes(120_000);
        byte[] ia32 = RandomNumberGenerator.GetBytes(90_000);

        byte[] image = FatImageBuilder.CreateEfiBootImage(x64, ia32);
        FatReader reader = new(image);

        Assert.Equal(x64, reader.ReadFile("EFI/BOOT/BOOTX64.EFI"));
        Assert.Equal(ia32, reader.ReadFile("EFI/BOOT/BOOTIA32.EFI"));
    }

    [Fact]
    public void OversizedPayload_IsRejected()
    {
        Assert.Throws<NotSupportedException>(() => FatImageBuilder.CreateEfiBootImage(new byte[40 * 1024 * 1024]));
    }

    /// <summary>Minimaler FAT16-Leser, der die Struktur unabhaengig vom Schreiber nachvollzieht.</summary>
    private sealed class FatReader
    {
        private readonly byte[] _image;
        private readonly int _fatOffset;
        private readonly int _rootOffset;
        private readonly int _dataOffset;
        private readonly int _rootEntryCount;
        private readonly int _clusterBytes;

        public FatReader(byte[] image)
        {
            _image = image;
            BytesPerSector = BitConverter.ToUInt16(image, 11);
            int sectorsPerCluster = image[13];
            int reserved = BitConverter.ToUInt16(image, 14);
            int fatCount = image[16];
            _rootEntryCount = BitConverter.ToUInt16(image, 17);
            int fatSectors = BitConverter.ToUInt16(image, 22);

            _clusterBytes = BytesPerSector * sectorsPerCluster;
            _fatOffset = reserved * BytesPerSector;
            _rootOffset = _fatOffset + (fatCount * fatSectors * BytesPerSector);
            _dataOffset = _rootOffset + (_rootEntryCount * 32);

            FileSystemType = Encoding.ASCII.GetString(image, 54, 8).Trim();
        }

        public int BytesPerSector { get; }

        public string FileSystemType { get; }

        public byte[] ReadFile(string path)
        {
            string[] parts = path.Split('/');
            int directoryOffset = _rootOffset;
            int directoryLength = _rootEntryCount * 32;

            for (int i = 0; i < parts.Length - 1; i++)
            {
                (ushort cluster, _) = FindEntry(directoryOffset, directoryLength, parts[i], expectDirectory: true);
                directoryOffset = _dataOffset + ((cluster - 2) * _clusterBytes);
                directoryLength = ClusterChainLength(cluster) * _clusterBytes;
            }

            (ushort fileCluster, uint size) = FindEntry(directoryOffset, directoryLength, parts[^1], expectDirectory: false);
            return ReadChain(fileCluster, size);
        }

        private (ushort Cluster, uint Size) FindEntry(int offset, int length, string name, bool expectDirectory)
        {
            string expected = ShortName(name);
            for (int i = 0; i < length; i += 32)
            {
                int entry = offset + i;
                if (_image[entry] == 0x00)
                {
                    break;
                }

                if (_image[entry] == 0xE5 || (_image[entry + 11] & 0x08) != 0)
                {
                    continue;
                }

                string candidate = Encoding.ASCII.GetString(_image, entry, 11);
                bool isDirectory = (_image[entry + 11] & 0x10) != 0;
                if (candidate == expected && isDirectory == expectDirectory)
                {
                    return (BitConverter.ToUInt16(_image, entry + 26), BitConverter.ToUInt32(_image, entry + 28));
                }
            }

            throw new FileNotFoundException($"'{name}' wurde im FAT-Abbild nicht gefunden.");
        }

        private static string ShortName(string name)
        {
            int dot = name.LastIndexOf('.');
            string stem = (dot > 0 ? name[..dot] : name).ToUpperInvariant();
            string ext = (dot > 0 ? name[(dot + 1)..] : string.Empty).ToUpperInvariant();
            return stem.PadRight(8)[..8] + ext.PadRight(3)[..3];
        }

        private int ClusterChainLength(ushort first)
        {
            int count = 0;
            ushort cluster = first;
            while (cluster is >= 2 and < 0xFFF8)
            {
                count++;
                cluster = BitConverter.ToUInt16(_image, _fatOffset + (cluster * 2));
            }

            return count;
        }

        private byte[] ReadChain(ushort first, uint size)
        {
            using MemoryStream buffer = new();
            ushort cluster = first;
            while (cluster is >= 2 and < 0xFFF8 && buffer.Length < size)
            {
                int offset = _dataOffset + ((cluster - 2) * _clusterBytes);
                int take = (int)Math.Min(_clusterBytes, size - buffer.Length);
                buffer.Write(_image, offset, take);
                cluster = BitConverter.ToUInt16(_image, _fatOffset + (cluster * 2));
            }

            return buffer.ToArray();
        }
    }
}
