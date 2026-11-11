using System.Buffers.Binary;

namespace ImageFacade.Internal;

/// <summary>
/// 只读编码数据的头部拿宽高，不解码像素。给 <see cref="ImageEngine"/> 在分配像素之前做尺寸上限校验用。
/// </summary>
/// <remarks>
/// <para>
/// 覆盖各后端支持的常见格式（PNG / JPEG / BMP / GIF / WebP / TIFF / QOI / PNM）。
/// 认不出的格式返回 <c>false</c>，由调用方退化为"解码后再兜底"——嗅探器**不能**成为新的失败点，
/// 所以这里对任何异常数据都只返回 false，从不抛异常。
/// </para>
/// <para>
/// 返回值里的宽高是**声明值**，可能与实际解码结果不同（损坏文件、TIFF 多页等），
/// 因此门面在解码后还会再比一次实际尺寸。
/// </para>
/// </remarks>
internal static class ImageHeaderReader
{
    /// <summary>尝试读取声明尺寸；失败返回 false 且宽高为 0。</summary>
    public static bool TryReadDimensions(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (data.Length < 10)
        {
            return false;
        }

        if (IsPng(data))
        {
            return TryPng(data, out width, out height);
        }

        if (data[0] == 0xFF && data[1] == 0xD8)
        {
            return TryJpeg(data, out width, out height);
        }

        if (data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            return TryBmp(data, out width, out height);
        }

        if (data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'8')
        {
            return TryGif(data, out width, out height);
        }

        if (IsWebp(data))
        {
            return TryWebp(data, out width, out height);
        }

        if (IsTiff(data))
        {
            return TryTiff(data, out width, out height);
        }

        if (data[0] == (byte)'q' && data[1] == (byte)'o' && data[2] == (byte)'i' && data[3] == (byte)'f')
        {
            return TryQoi(data, out width, out height);
        }

        if (data[0] == (byte)'P' && (data[1] == (byte)'5' || data[1] == (byte)'6'))
        {
            return TryPnm(data, out width, out height);
        }

        return false;
    }

    // ---- 格式判定 ----

    private static bool IsPng(ReadOnlySpan<byte> data)
        => data.Length >= 24
        && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
        && data[12] == (byte)'I' && data[13] == (byte)'H' && data[14] == (byte)'D' && data[15] == (byte)'R';

    private static bool IsWebp(ReadOnlySpan<byte> data)
        => data.Length >= 30
        && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
        && data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P';

    private static bool IsTiff(ReadOnlySpan<byte> data)
        => data.Length >= 8
        && ((data[0] == (byte)'I' && data[1] == (byte)'I' && data[2] == 42 && data[3] == 0)
            || (data[0] == (byte)'M' && data[1] == (byte)'M' && data[2] == 0 && data[3] == 42));

    // ---- 各格式的头部解析 ----

    private static bool TryPng(ReadOnlySpan<byte> data, out int width, out int height)
    {
        // 8 字节签名 + 4 字节块长度 + "IHDR" + 4 字节宽 + 4 字节高
        uint w = BinaryPrimitives.ReadUInt32BigEndian(data[16..]);
        uint h = BinaryPrimitives.ReadUInt32BigEndian(data[20..]);
        return ToInt(w, h, out width, out height);
    }

    private static bool TryJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        int position = 2;
        while (position + 3 < data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            byte marker = data[position + 1];
            if (marker == 0xFF)
            {
                position++;      // 填充字节
                continue;
            }

            // 无长度字段的标记：SOI / TEM / RSTn
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                position += 2;
                continue;
            }

            // 到图像结束或扫描数据段，说明前面没有 SOF（尺寸未知）
            if (marker == 0xD9 || marker == 0xDA)
            {
                return false;
            }

            if (position + 4 > data.Length)
            {
                return false;
            }

            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);
            if (length < 2)
            {
                return false;
            }

            // SOF0..SOF15 里除了 DHT(C4) / JPG(C8) / DAC(CC) 之外都带尺寸
            bool isStartOfFrame = marker >= 0xC0 && marker <= 0xCF
                && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;

            if (isStartOfFrame)
            {
                // 段内布局：长度(2) 精度(1) 高(2) 宽(2)
                if (position + 9 > data.Length)
                {
                    return false;
                }

                int h = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 5)..]);
                int w = BinaryPrimitives.ReadUInt16BigEndian(data[(position + 7)..]);
                width = w;
                height = h;
                return w > 0 && h > 0;
            }

            position += 2 + length;
        }

        return false;
    }

    private static bool TryBmp(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (data.Length < 26)
        {
            return false;
        }

        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);

        // BITMAPCOREHEADER：16 位宽高
        if (headerSize == 12)
        {
            if (data.Length < 22)
            {
                return false;
            }

            width = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]);
            height = BinaryPrimitives.ReadUInt16LittleEndian(data[20..]);
            return width > 0 && height > 0;
        }

        if (headerSize < 40)
        {
            return false;
        }

        int w = BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        int h = BinaryPrimitives.ReadInt32LittleEndian(data[22..]);

        // 负高度表示自底向上存储
        width = w;
        height = h == int.MinValue ? 0 : Math.Abs(h);
        return w > 0 && height > 0;
    }

    private static bool TryGif(ReadOnlySpan<byte> data, out int width, out int height)
    {
        // 6 字节签名 + 逻辑屏幕宽(2) 高(2)
        width = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
        height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        return width > 0 && height > 0;
    }

    private static bool TryWebp(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        // 扩展格式：画布宽高各 3 字节小端，存的是「尺寸 - 1」
        if (data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)'X')
        {
            width = (data[24] | (data[25] << 8) | (data[26] << 16)) + 1;
            height = (data[27] | (data[28] << 8) | (data[29] << 16)) + 1;
            return true;
        }

        // 有损：帧头 3 字节后是起始码 9D 01 2A，再往后宽高各 14 位
        if (data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)' ')
        {
            if (data[23] != 0x9D || data[24] != 0x01 || data[25] != 0x2A)
            {
                return false;
            }

            width = (data[26] | (data[27] << 8)) & 0x3FFF;
            height = (data[28] | (data[29] << 8)) & 0x3FFF;
            return width > 0 && height > 0;
        }

        // 无损：0x2F 签名后紧跟 14 位宽 + 14 位高
        if (data[12] == (byte)'V' && data[13] == (byte)'P' && data[14] == (byte)'8' && data[15] == (byte)'L')
        {
            if (data.Length < 25 || data[20] != 0x2F)
            {
                return false;
            }

            uint bits = (uint)(data[21] | (data[22] << 8) | (data[23] << 16) | (data[24] << 24));
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
            return true;
        }

        return false;
    }

    private static bool TryTiff(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        bool little = data[0] == (byte)'I';
        uint ifdOffset = little
            ? BinaryPrimitives.ReadUInt32LittleEndian(data[4..])
            : BinaryPrimitives.ReadUInt32BigEndian(data[4..]);

        if ((long)ifdOffset + 2 > data.Length)
        {
            return false;
        }

        int position = (int)ifdOffset;
        int entryCount = little
            ? BinaryPrimitives.ReadUInt16LittleEndian(data[position..])
            : BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
        position += 2;

        int widthValue = 0;
        int heightValue = 0;

        for (int i = 0; i < entryCount; i++, position += 12)
        {
            if (position + 12 > data.Length)
            {
                break;
            }

            int tag = little
                ? BinaryPrimitives.ReadUInt16LittleEndian(data[position..])
                : BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (tag != 256 && tag != 257)
            {
                continue;
            }

            int type = little
                ? BinaryPrimitives.ReadUInt16LittleEndian(data[(position + 2)..])
                : BinaryPrimitives.ReadUInt16BigEndian(data[(position + 2)..]);

            // SHORT 的值内联在 4 字节字段的前 2 字节；LONG 占满 4 字节
            int value = type switch
            {
                3 => little
                    ? BinaryPrimitives.ReadUInt16LittleEndian(data[(position + 8)..])
                    : BinaryPrimitives.ReadUInt16BigEndian(data[(position + 8)..]),
                4 => (int)(little
                    ? BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 8)..])
                    : BinaryPrimitives.ReadUInt32BigEndian(data[(position + 8)..])),
                _ => 0,
            };

            if (tag == 256)
            {
                widthValue = value;
            }
            else
            {
                heightValue = value;
            }
        }

        width = widthValue;
        height = heightValue;
        return widthValue > 0 && heightValue > 0;
    }

    private static bool TryQoi(ReadOnlySpan<byte> data, out int width, out int height)
    {
        // 4 字节魔法 + 4 字节宽 + 4 字节高（都按大端）
        uint w = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        uint h = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        return ToInt(w, h, out width, out height);
    }

    private static bool TryPnm(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        int position = 2;
        if (!TryReadPnmNumber(data, ref position, out int w) || !TryReadPnmNumber(data, ref position, out int h))
        {
            return false;
        }

        width = w;
        height = h;
        return w > 0 && h > 0;
    }

    /// <summary>读一个十进制数，跳过空白与 <c>#</c> 注释。</summary>
    private static bool TryReadPnmNumber(ReadOnlySpan<byte> data, ref int position, out int value)
    {
        value = 0;

        while (position < data.Length)
        {
            byte b = data[position];
            if (b == (byte)'#')
            {
                while (position < data.Length && data[position] != (byte)'\n')
                {
                    position++;
                }

                continue;
            }

            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                position++;
                continue;
            }

            break;
        }

        int start = position;
        long parsed = 0;
        while (position < data.Length && data[position] >= (byte)'0' && data[position] <= (byte)'9')
        {
            parsed = (parsed * 10) + (data[position] - (byte)'0');
            if (parsed > int.MaxValue)
            {
                return false;
            }

            position++;
        }

        if (position == start)
        {
            return false;
        }

        value = (int)parsed;
        return true;
    }

    private static bool ToInt(uint width, uint height, out int resultWidth, out int resultHeight)
    {
        resultWidth = 0;
        resultHeight = 0;

        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            return false;
        }

        resultWidth = (int)width;
        resultHeight = (int)height;
        return true;
    }
}
